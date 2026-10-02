using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Smtp.Delivery;

public enum DeliveryOutcome
{
    Delivered,
    TemporaryFailure,
    PermanentFailure,
}

public sealed record RecipientResult(QueueEntry Entry, DeliveryOutcome Outcome, string? Error);

/// <summary>
/// Delivers one batch (one message, one destination domain) via MX lookup or the configured smart host.
/// </summary>
public sealed class RemoteDeliveryClient(IOptions<MailserverOptions> options, ILookupClient dns, ILogger<RemoteDeliveryClient> logger)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<RecipientResult>> DeliverAsync(DeliveryBatch batch, MimeMessage message, CancellationToken cancellationToken)
    {
        var smartHost = options.Value.Delivery.SmartHost;
        IReadOnlyList<string> hosts;
        if (smartHost is { Host.Length: > 0 })
        {
            hosts = [smartHost.Host];
        }
        else
        {
            var resolution = await ResolveMailHostsAsync(batch.Domain, cancellationToken);
            if (resolution.Error is not null)
            {
                return AllFailed(batch, resolution.Permanent ? DeliveryOutcome.PermanentFailure : DeliveryOutcome.TemporaryFailure, resolution.Error);
            }

            hosts = resolution.Hosts;
        }

        var lastError = "No mail host reachable";
        foreach (var host in hosts)
        {
            try
            {
                return await DeliverToHostAsync(host, batch, message, cancellationToken);
            }
            catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
            {
                // A permanent rejection is authoritative; other MX hosts of the same domain would answer the same.
                return AllFailed(batch, DeliveryOutcome.PermanentFailure, $"{host} said: {(int)ex.StatusCode} {ex.Message}");
            }
            catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or SslHandshakeException or SocketException
                                           or IOException or TimeoutException or ServiceNotConnectedException or AuthenticationException)
            {
                lastError = ex is SmtpCommandException command ? $"{host} said: {(int)command.StatusCode} {command.Message}" : $"{host}: {ex.Message}";
                logger.LogInformation("Delivery to {Host} for {Domain} failed: {Error}", host, batch.Domain, lastError);
            }
        }

        return AllFailed(batch, DeliveryOutcome.TemporaryFailure, lastError);
    }

    private async Task<IReadOnlyList<RecipientResult>> DeliverToHostAsync(string host, DeliveryBatch batch, MimeMessage message,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var smartHost = settings.Delivery.SmartHost is { Host.Length: > 0 } configured ? configured : null;

        using var client = new CollectingSmtpClient();
        client.LocalDomain = settings.Hostname;
        client.Timeout = (int)CommandTimeout.TotalMilliseconds;
        if (smartHost is null)
        {
            // MX servers commonly present certificates that do not match their name; opportunistic TLS still protects against
            // passive eavesdropping, which is the state of the art for server-to-server SMTP without DANE/MTA-STS.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        var port = smartHost?.Port ?? settings.Delivery.RemotePort;
        var security = smartHost is null ? SecureSocketOptions.StartTlsWhenAvailable : ParseSecurity(smartHost.Security);
        await client.ConnectAsync(host, port, security, cancellationToken);

        if (smartHost?.Username is { Length: > 0 } username)
        {
            await client.AuthenticateAsync(username, smartHost.Password ?? "", cancellationToken);
        }

        var sender = new MailboxAddress("", batch.Sender);
        var recipients = batch.Entries.Select(e => new MailboxAddress("", e.Recipient.ToString())).ToList();
        try
        {
            await client.SendAsync(message, sender, recipients, cancellationToken);
        }
        catch (SmtpCommandException) when (client.Rejected.Count == recipients.Count)
        {
            // Every recipient was refused; the per-recipient answers below carry the details.
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None);
            }
        }

        logger.LogInformation("Delivered message {File} to {Host} for {Count} recipient(s), {Rejected} rejected", batch.MessageFile, host,
            recipients.Count - client.Rejected.Count, client.Rejected.Count);

        return batch.Entries.Select(entry =>
        {
            if (!client.Rejected.TryGetValue(entry.Recipient.ToString().ToLowerInvariant(), out var response))
            {
                return new RecipientResult(entry, DeliveryOutcome.Delivered, null);
            }

            var outcome = (int)response.StatusCode >= 500 ? DeliveryOutcome.PermanentFailure : DeliveryOutcome.TemporaryFailure;
            return new RecipientResult(entry, outcome, $"{host} said: {(int)response.StatusCode} {response.Response}");
        }).ToList();
    }

    private sealed record HostResolution(IReadOnlyList<string> Hosts, string? Error, bool Permanent);

    private async Task<HostResolution> ResolveMailHostsAsync(string domain, CancellationToken cancellationToken)
    {
        IDnsQueryResponse response;
        try
        {
            response = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);
        }
        catch (DnsResponseException ex)
        {
            return new HostResolution([], $"DNS lookup for {domain} failed: {ex.Message}", Permanent: false);
        }

        if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
        {
            return new HostResolution([], $"Domain {domain} does not exist", Permanent: true);
        }

        if (response.HasError)
        {
            return new HostResolution([], $"DNS lookup for {domain} failed: {response.ErrorMessage}", Permanent: false);
        }

        var mx = response.Answers.MxRecords().ToList();
        if (mx.Count == 1 && mx[0].Exchange.Value is "." or "")
        {
            return new HostResolution([], $"Domain {domain} does not accept mail (null MX)", Permanent: true);
        }

        if (mx.Count > 0)
        {
            // Lowest preference first; hosts with equal preference are tried in random order to spread load.
            var hosts = mx
                .OrderBy(r => r.Preference)
                .ThenBy(_ => Random.Shared.Next())
                .Select(r => r.Exchange.Value.TrimEnd('.'))
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new HostResolution(hosts, null, false);
        }

        // RFC 5321 5.1: without MX records the domain itself is the mail host.
        return new HostResolution([domain], null, false);
    }

    private static IReadOnlyList<RecipientResult> AllFailed(DeliveryBatch batch, DeliveryOutcome outcome, string error) =>
        batch.Entries.Select(e => new RecipientResult(e, outcome, error)).ToList();

    private static SecureSocketOptions ParseSecurity(string value) => value.ToLowerInvariant() switch
    {
        "starttls" => SecureSocketOptions.StartTls,
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    /// <summary>Records refused recipients instead of aborting the whole transaction.</summary>
    private sealed class CollectingSmtpClient : SmtpClient
    {
        public Dictionary<string, SmtpResponse> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
            Rejected[mailbox.Address.ToLowerInvariant()] = response;
    }
}
