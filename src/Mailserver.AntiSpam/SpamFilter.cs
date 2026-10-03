using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mailserver.AntiSpam.Checks;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.AntiSpam;

/// <summary>State of one inbound SMTP session, collected across MAIL FROM, RCPT TO and DATA.</summary>
public sealed class InboundSession(IPAddress? ip, string? helo)
{
    public IPAddress? Ip { get; } = ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    public string Helo { get; } = helo ?? "";
    public bool ConnectionChecked { get; internal set; }
    public bool Trusted { get; internal set; }
    public string? ReverseDns { get; internal set; }
    public List<(string Zone, double Score)> Listings { get; } = [];
    public string MailFrom { get; internal set; } = "";
    public SpfOutcome? Spf { get; internal set; }
}

public sealed record FilterResult(string? Rejection, InboundVerdict Verdict, string Headers);

/// <summary>
/// Combines SPF, DKIM, DMARC, DNS blocklists, reverse DNS and simple header checks into a spam score.
/// </summary>
public sealed class SpamFilter(
    IOptions<MailserverOptions> options,
    IDnsResolver dns,
    SpfChecker spf,
    DkimChecker dkim,
    DmarcChecker dmarc,
    DnsBlocklistChecker blocklists,
    Greylist greylist,
    AccountStore accounts,
    ILogger<SpamFilter> logger)
{
    private SpamOptions Settings => options.Value.Spam;

    /// <summary>Connection-level checks, run once per session at the first MAIL FROM. Returns a rejection text or null.</summary>
    public async Task<string?> CheckConnectionAsync(InboundSession session, CancellationToken cancellationToken)
    {
        if (session.ConnectionChecked)
        {
            return session.Listings.Any(l => double.IsPositiveInfinity(l.Score)) ? Rejection(session) : null;
        }

        session.ConnectionChecked = true;
        session.Trusted = !Settings.Enabled || session.Ip is null || IsTrusted(session.Ip);
        if (session.Trusted)
        {
            return null;
        }

        var ptr = await dns.GetPtrAsync(session.Ip!, cancellationToken);
        session.ReverseDns = ptr.Records.FirstOrDefault();

        var checks = Settings.EffectiveDnsBlocklists.Where(b => b.Zone.Length > 0)
            .Select(async b => (List: b, Listed: await blocklists.IsListedAsync(session.Ip!, b.Zone, cancellationToken)));
        foreach (var (list, listed) in await Task.WhenAll(checks))
        {
            if (listed)
            {
                var reject = list.Action.Equals("Reject", StringComparison.OrdinalIgnoreCase);
                session.Listings.Add((list.Zone, reject ? double.PositiveInfinity : list.Score));
            }
        }

        if (session.Listings.Any(l => double.IsPositiveInfinity(l.Score)))
        {
            logger.LogInformation("Rejecting {Ip}: listed in {Zones}", session.Ip, string.Join(", ", session.Listings.Select(l => l.Zone)));
            return Rejection(session);
        }

        return null;

        static string Rejection(InboundSession s) =>
            $"5.7.1 Service unavailable; client [{s.Ip}] blocked using {s.Listings.First(l => double.IsPositiveInfinity(l.Score)).Zone}";
    }

    /// <summary>SPF for the envelope sender. Returns a rejection text or null.</summary>
    public async Task<string?> CheckSenderAsync(InboundSession session, string mailFrom, CancellationToken cancellationToken)
    {
        session.MailFrom = mailFrom;
        session.Spf = null;
        if (session.Trusted)
        {
            return null;
        }

        var sender = mailFrom.Length > 0 ? mailFrom : session.Helo;
        session.Spf = await spf.CheckAsync(session.Ip!, sender, session.Helo, cancellationToken);
        if (Settings.RejectSpfFail && session.Spf.Result == SpfResult.Fail)
        {
            return $"5.7.23 SPF of {session.Spf.Domain} does not allow [{session.Ip}] to send";
        }

        return null;
    }

    /// <summary>Greylisting decision for one recipient: false means "defer".</summary>
    public bool AcceptRecipient(InboundSession session, string recipient)
    {
        var settings = Settings.Greylisting;
        if (session.Trusted || !settings.Enabled || (settings.SkipOnSpfPass && session.Spf?.Result == SpfResult.Pass))
        {
            return true;
        }

        return greylist.Check(session.Ip!, session.MailFrom, recipient);
    }

    /// <summary>Content checks on DATA: DKIM, DMARC and scoring. Produces the headers to add.</summary>
    public async Task<FilterResult> CheckMessageAsync(InboundSession session, byte[] rawMessage, CancellationToken cancellationToken)
    {
        if (session.Trusted)
        {
            return new FilterResult(null, InboundVerdict.Clean, "");
        }

        var message = await MimeMessage.LoadAsync(new MemoryStream(rawMessage, writable: false), cancellationToken);
        var dkimResults = await dkim.VerifyAsync(message, cancellationToken);
        var spfOutcome = session.Spf ?? new SpfOutcome(SpfResult.None, "");
        var fromDomain = message.From.Mailboxes.FirstOrDefault()?.Domain?.ToLowerInvariant();
        var dmarcOutcome = string.IsNullOrEmpty(fromDomain)
            ? new DmarcOutcome(DmarcResult.None, DmarcPolicy.None, "")
            : await dmarc.CheckAsync(fromDomain, spfOutcome, dkimResults, cancellationToken);

        var tests = new List<(string Name, double Score)>();
        void Add(string name, double score) => tests.Add((name, score));

        switch (spfOutcome.Result)
        {
            case SpfResult.Fail: Add("SPF_FAIL", 3.5); break;
            case SpfResult.SoftFail: Add("SPF_SOFTFAIL", 1.5); break;
            case SpfResult.PermError: Add("SPF_PERMERROR", 1.0); break;
            case SpfResult.Neutral: Add("SPF_NEUTRAL", 0.3); break;
            case SpfResult.None: Add("SPF_NONE", 0.5); break;
        }

        if (dkimResults.Count == 0)
        {
            Add("DKIM_NONE", 0.3);
        }
        else if (!dkimResults.Any(d => d.Result == DkimResult.Pass) && dkimResults.Any(d => d.Result is DkimResult.Fail or DkimResult.PermError))
        {
            Add("DKIM_INVALID", 1.0);
        }

        if (dmarcOutcome.Result == DmarcResult.Fail)
        {
            if (dmarcOutcome.Policy == DmarcPolicy.Reject && Settings.EnforceDmarcReject)
            {
                logger.LogInformation("Rejecting message from [{Ip}]: DMARC of {Domain} fails with p=reject", session.Ip, dmarcOutcome.FromDomain);
                return new FilterResult($"5.7.1 Message rejected by DMARC policy of {dmarcOutcome.FromDomain}", InboundVerdict.Clean, "");
            }

            Add(dmarcOutcome.Policy switch
            {
                DmarcPolicy.Reject => "DMARC_REJECT",
                DmarcPolicy.Quarantine => "DMARC_QUARANTINE",
                _ => "DMARC_FAIL",
            }, dmarcOutcome.Policy switch
            {
                DmarcPolicy.Reject => 6.0,
                DmarcPolicy.Quarantine => 5.0,
                _ => 1.5,
            });
        }

        // A foreign server claiming to send as one of our own domains without a valid signature is almost always spoofing.
        if (fromDomain is not null && accounts.IsLocalDomain(fromDomain) && dmarcOutcome.Result != DmarcResult.Pass)
        {
            Add("FROM_LOCAL_SPOOF", 5.0);
        }

        foreach (var (zone, score) in session.Listings)
        {
            Add($"DNSBL_{zone.ToUpperInvariant()}", score);
        }

        if (session.ReverseDns is null)
        {
            Add("NO_REVERSE_DNS", 1.5);
        }

        if (!session.Helo.Contains('.') && !session.Helo.StartsWith('['))
        {
            Add("HELO_NOT_FQDN", 1.0);
        }

        if (!message.Headers.Contains(HeaderId.MessageId))
        {
            Add("MISSING_MESSAGE_ID", 0.5);
        }

        if (!message.Headers.Contains(HeaderId.Date))
        {
            Add("MISSING_DATE", 0.5);
        }

        var total = Math.Round(tests.Sum(t => t.Score), 1);
        var isSpam = total >= Settings.JunkThreshold;
        var discard = Settings.DeleteThreshold > 0 && total >= Settings.DeleteThreshold;
        var verdict = new InboundVerdict(total, isSpam, discard, tests.Select(t => t.Name).ToList());

        var headers = new StringBuilder()
            .Append(AuthenticationResults(session, spfOutcome, dkimResults, dmarcOutcome))
            .Append(CultureInfo.InvariantCulture, $"X-Spam-Score: {total:F1}\r\n")
            .Append(CultureInfo.InvariantCulture,
                $"X-Spam-Status: {(isSpam ? "Yes" : "No")}, score={total:F1} required={Settings.JunkThreshold:F1}\r\n\ttests={(tests.Count == 0 ? "NONE" : string.Join(',', tests.Select(t => t.Name)))}\r\n");
        if (isSpam)
        {
            headers.Append("X-Spam-Flag: YES\r\n");
        }

        logger.LogInformation("Message from [{Ip}] <{Sender}>: score {Score:F1} ({Tests})", session.Ip, session.MailFrom, total,
            string.Join(", ", tests.Select(t => $"{t.Name}={t.Score}")));
        return new FilterResult(null, verdict, headers.ToString());
    }

    /// <summary>
    /// Headers a sender must not be able to set: spam headers (rules could match them) and Authentication-Results claiming
    /// to come from this server (RFC 8601 section 5).
    /// </summary>
    public bool IsSpoofableHeader(string name, string value) =>
        name.StartsWith("X-Spam-", StringComparison.OrdinalIgnoreCase) ||
        (name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) &&
         value.TrimStart().StartsWith(options.Value.Hostname, StringComparison.OrdinalIgnoreCase));

    private string AuthenticationResults(InboundSession session, SpfOutcome spfOutcome, IReadOnlyList<DkimSignatureResult> dkimResults,
        DmarcOutcome dmarcOutcome)
    {
        var parts = new List<string>
        {
            $"spf={spfOutcome.Result.ToString().ToLowerInvariant()} smtp.mailfrom={(session.MailFrom.Length > 0 ? session.MailFrom : session.Helo)}",
        };
        parts.AddRange(dkimResults.Count == 0
            ? ["dkim=none"]
            : dkimResults.Select(d => $"dkim={d.Result.ToString().ToLowerInvariant()} header.d={d.Domain} header.s={d.Selector}"));
        if (dmarcOutcome.FromDomain.Length > 0)
        {
            parts.Add($"dmarc={dmarcOutcome.Result.ToString().ToLowerInvariant()} (p={dmarcOutcome.Policy.ToString().ToLowerInvariant()}) header.from={dmarcOutcome.FromDomain}");
        }

        return $"Authentication-Results: {options.Value.Hostname};\r\n\t{string.Join(";\r\n\t", parts)}\r\n";
    }

    private bool IsTrusted(IPAddress ip) =>
        (Settings.TrustLoopback && IPAddress.IsLoopback(ip)) ||
        Settings.TrustedNetworks.Select(NetworkRange.Parse).Any(range => range.Contains(ip));
}
