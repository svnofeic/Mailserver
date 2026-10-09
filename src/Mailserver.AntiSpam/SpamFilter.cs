using Mailserver.Core.Security;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mailserver.AntiSpam.Checks;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.AntiSpam;

/// <summary>State of one inbound SMTP session, collected across MAIL FROM, RCPT TO and DATA.</summary>
public sealed class InboundSession(IPAddress? ip, string? helo, string? sessionId = null)
{
    /// <summary>Identifies the SMTP session in the spam log.</summary>
    public string SessionId { get; } = sessionId ?? Guid.NewGuid().ToString("N");

    public IPAddress? Ip { get; } = ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    public string Helo { get; } = helo ?? "";
    public bool ConnectionChecked { get; internal set; }
    public bool Trusted { get; internal set; }
    public string? ReverseDns { get; internal set; }
    public List<(string Zone, double Score)> Listings { get; } = [];
    public string MailFrom { get; internal set; } = "";
    public SpfOutcome? Spf { get; internal set; }

    /// <summary>
    /// MAIL FROM names one of this server's domains. Decided only after DATA: a message this server signed itself and that
    /// comes back through a forwarding at another provider is fine; anything else is refused.
    /// </summary>
    public bool ClaimsLocalSender { get; internal set; }
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
    SpamLog spamLog,
    ILogger<SpamFilter> logger,
    Mailserver.Core.Security.IpRules? ipRules = null)
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

        // "Nie sperren" under IP-Sperren (e.g. the servers of a provider that forwards mail here): no blocklist lookups.
        var checks = ipRules?.IsAllowed(session.Ip) == true ? [] : Settings.EffectiveDnsBlocklists.Where(b => b.Zone.Length > 0)
            .Select(async b => (List: b, Codes: await blocklists.LookupAsync(session.Ip!, b.Zone, cancellationToken)));
        var found = new List<string>();
        foreach (var (list, codes) in await Task.WhenAll(checks))
        {
            if (codes.Count > 0)
            {
                var reject = list.Action.Equals("Reject", StringComparison.OrdinalIgnoreCase) &&
                             !codes.All(c => DnsBlocklistChecker.IsWeakSpamhausListing(list.Zone, c));
                // A weak listing in a list meant for rejection still counts clearly.
                var score = list.Action.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? 4.0 : list.Score;
                session.Listings.Add((list.Zone, reject ? double.PositiveInfinity : score));
                found.Add($"{list.Zone} ({string.Join(", ", codes.Select(c => DnsBlocklistChecker.SpamhausList(c) is { } name && list.Zone.Contains("spamhaus", StringComparison.OrdinalIgnoreCase) ? $"{c} {name}" : c.ToString()))})");
            }
        }

        if (session.Listings.Any(l => double.IsPositiveInfinity(l.Score)))
        {
            logger.LogInformation("Rejecting {Ip}: listed in {Zones}", session.Ip, string.Join(", ", found));
            Log(session, SpamLogStage.Connect, SpamLogAction.Rejected, detail: "listed in " + string.Join(", ", found));
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
        // Also from trusted networks and with the spam filter switched off; only relay networks (Smtp:RelayNetworks) never get here.
        session.ClaimsLocalSender = options.Value.Security.RejectUnauthenticatedLocalSender &&
                                    Domain(mailFrom) is { } senderDomain && accounts.IsLocalDomain(senderDomain);
        if (session.Trusted)
        {
            return null;
        }

        var sender = mailFrom.Length > 0 ? mailFrom : session.Helo;
        session.Spf = await spf.CheckAsync(session.Ip!, sender, session.Helo, cancellationToken);
        if (Settings.RejectSpfFail && session.Spf.Result == SpfResult.Fail)
        {
            Log(session, SpamLogStage.Sender, SpamLogAction.Rejected, detail: $"SPF fail ({session.Spf.Mechanism})");
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

        if (greylist.Check(session.Ip!, session.MailFrom, recipient))
        {
            return true;
        }

        Log(session, SpamLogStage.Recipient, SpamLogAction.Deferred, recipient: recipient, detail: "greylisted");
        return false;
    }

    /// <summary>Content checks on DATA: DKIM, DMARC and scoring. Produces the headers to add.</summary>
    public async Task<FilterResult> CheckMessageAsync(InboundSession session, byte[] rawMessage, IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        if (session.Trusted && !session.ClaimsLocalSender)
        {
            return new FilterResult(null, InboundVerdict.Clean, "");
        }

        var message = await MimeMessage.LoadAsync(new MemoryStream(rawMessage, writable: false), cancellationToken);
        var dkimResults = await dkim.VerifyAsync(message, cancellationToken);
        if (session.ClaimsLocalSender && !dkimResults.Any(d => d.Result == DkimResult.Pass && Aligned(d.Domain, Domain(session.MailFrom))))
        {
            Log(session, SpamLogStage.Data, SpamLogAction.Rejected, recipient: string.Join(", ", recipients), message: message,
                detail: "eigene Domain als Absender ohne Anmeldung und ohne gültige DKIM-Signatur");
            return new FilterResult(LocalSenderRejection, InboundVerdict.Clean, "");
        }

        if (session.Trusted)
        {
            return new FilterResult(null, InboundVerdict.Clean, "");
        }

        var spfOutcome = session.Spf ?? new SpfOutcome(SpfResult.None, "");
        var fromDomain = message.From.Mailboxes.FirstOrDefault()?.Domain?.ToLowerInvariant();
        var dmarcOutcome = string.IsNullOrEmpty(fromDomain)
            ? new DmarcOutcome(DmarcResult.None, DmarcPolicy.None, "")
            : await dmarc.CheckAsync(fromDomain, spfOutcome, dkimResults, cancellationToken);

        var tests = new List<(string Name, double Score)>();
        void Add(string name, double score) => tests.Add((name, score));

        void LogMessage(string action, double? score, IReadOnlyList<SpamTest> scored, string? detail) =>
            Log(session, SpamLogStage.Data, action, recipient: string.Join(", ", recipients), message: message, score: score,
                tests: scored.Count == 0 ? null : string.Join(',', scored),
                dkim: dkimResults.Count == 0 ? "none" : string.Join(',', dkimResults.Select(d => $"{d.Result.ToString().ToLowerInvariant()}:{d.Domain}")),
                dmarc: dmarcOutcome.FromDomain.Length == 0 ? null : $"{dmarcOutcome.Result.ToString().ToLowerInvariant()} p={dmarcOutcome.Policy.ToString().ToLowerInvariant()}",
                detail: detail);

        // SPF fails at every plain forwarding (the forwarder is not in the original sender's SPF); a signature of the sender
        // domain that survived the trip shows the mail is genuine (DMARC passes through DKIM).
        var forwarded = spfOutcome.Result is SpfResult.Fail or SpfResult.SoftFail && dmarcOutcome.Result == DmarcResult.Pass;
        switch (spfOutcome.Result)
        {
            case SpfResult.Fail or SpfResult.SoftFail when forwarded: Add("SPF_FAIL_DKIM_PASS", 0.3); break;
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
                LogMessage(SpamLogAction.Rejected, null, [], "DMARC p=reject");
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
        if (fromDomain is not null && accounts.IsLocalDomain(fromDomain) && dmarcOutcome.Result != DmarcResult.Pass &&
            !dkimResults.Any(d => d.Result == DkimResult.Pass && Aligned(d.Domain, fromDomain)))
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
        var verdict = new InboundVerdict(total, isSpam, discard, tests.Select(t => new SpamTest(t.Name, t.Score)).ToList(), session.SessionId);
        LogMessage(isSpam ? SpamLogAction.Spam : SpamLogAction.Accepted, total, verdict.Tests, discard ? "above delete threshold" : null);

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

    public const string LocalSenderRejection = "5.7.1 Use the submission port with authentication to send as a local domain";

    private static string? Domain(string address) =>
        address.LastIndexOf('@') is var at and >= 0 && at < address.Length - 1 ? address[(at + 1)..].ToLowerInvariant() : null;

    /// <summary>Relaxed alignment as in DMARC: the same domain or a subdomain of it.</summary>
    private static bool Aligned(string signingDomain, string? domain) =>
        domain is not null && (signingDomain.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                               domain.EndsWith("." + signingDomain, StringComparison.OrdinalIgnoreCase) ||
                               signingDomain.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Headers a sender must not be able to set: spam headers (rules could match them) and Authentication-Results claiming
    /// to come from this server (RFC 8601 section 5).
    /// </summary>
    public bool IsSpoofableHeader(string name, string value) =>
        name.StartsWith("X-Spam-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Virus-Scanned", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Mailserver-Warning", StringComparison.OrdinalIgnoreCase) ||
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

    private void Log(InboundSession session, string stage, string action, string? recipient = null, MimeMessage? message = null,
        double? score = null, string? tests = null, string? dkim = null, string? dmarc = null, string? detail = null) =>
        spamLog.Write(new SpamLogEntry
        {
            Session = session.SessionId,
            Stage = stage,
            Action = action,
            ClientIp = session.Ip?.ToString(),
            ReverseDns = session.ReverseDns,
            Helo = session.Helo,
            MailFrom = session.MailFrom,
            Recipient = recipient,
            HeaderFrom = message?.From.Mailboxes.FirstOrDefault()?.Address,
            Subject = message?.Subject,
            MessageId = message?.MessageId,
            Score = score,
            Tests = tests,
            Spf = session.Spf is { } spfOutcome ? $"{spfOutcome.Result.ToString().ToLowerInvariant()}:{spfOutcome.Domain}" : null,
            Dkim = dkim,
            Dmarc = dmarc,
            Detail = detail,
        });

    private bool IsTrusted(IPAddress ip) =>
        (Settings.TrustLoopback && IPAddress.IsLoopback(ip)) ||
        Settings.TrustedNetworks.Select(NetworkRange.Parse).Any(range => range.Contains(ip));
}
