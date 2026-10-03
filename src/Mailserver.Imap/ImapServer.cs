using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Imap.Protocol;
using Mailserver.Imap.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Imap;

/// <summary>
/// Accepts IMAP connections on port 143 (STARTTLS) and 993 (implicit TLS).
/// </summary>
public sealed class ImapServer(
    IOptions<MailserverOptions> options,
    AccountStore accounts,
    MailboxStore mailboxes,
    SentCopies sentCopies,
    AuthThrottle throttle,
    FolderWatcher watcher,
    CertificateProvider certificates,
    SpamLog spamLog,
    SpamFeedback spamFeedback,
    ImapTrace trace,
    ILogger<ImapServer> logger) : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerIp = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var hasCertificate = certificates.GetCertificate() is not null;
        var listeners = new List<Task>();

        foreach (var address in settings.Imap.EffectiveListenAddresses.Select(IPAddress.Parse))
        {
            if (settings.Imap.Port > 0)
            {
                listeners.Add(ListenAsync(new IPEndPoint(address, settings.Imap.Port), implicitTls: false, hasCertificate, stoppingToken));
            }

            if (settings.Imap.TlsPort > 0)
            {
                if (hasCertificate)
                {
                    listeners.Add(ListenAsync(new IPEndPoint(address, settings.Imap.TlsPort), implicitTls: true, hasCertificate, stoppingToken));
                }
                else
                {
                    logger.LogError("IMAPS port {Port} is disabled: no TLS certificate configured", settings.Imap.TlsPort);
                }
            }
        }

        await Task.WhenAll(listeners);
    }

    private async Task ListenAsync(IPEndPoint endpoint, bool implicitTls, bool hasCertificate, CancellationToken stoppingToken)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        logger.LogInformation("IMAP listening on {Endpoint} ({Mode})", endpoint,
            implicitTls ? "TLS" : hasCertificate ? "STARTTLS" : "plaintext, login disabled");

        var sessions = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                sessions.Add(HandleAsync(client, implicitTls, hasCertificate, stoppingToken));
                sessions.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(sessions);
        }
    }

    private async Task HandleAsync(TcpClient client, bool implicitTls, bool hasCertificate, CancellationToken stoppingToken)
    {
        await Task.Yield();
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is { IsIPv4MappedToIPv6: true })
        {
            remote = remote.MapToIPv4();
        }

        var counted = remote is not null && Enter(remote);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                Stream stream = client.GetStream();

                if (!counted)
                {
                    await stream.WriteAsync("* BYE Too many connections from your address\r\n"u8.ToArray(), stoppingToken);
                    return;
                }

                if (implicitTls)
                {
                    stream = await AuthenticateTlsAsync(stream, stoppingToken);
                }

                await using var connection = new ImapConnection(stream);
                if (options.Value.Imap.Trace)
                {
                    connection.Trace = trace.Start(remote);
                }

                var session = new ImapSession(connection, remote, implicitTls, hasCertificate && !implicitTls ? AuthenticateTlsAsync : null,
                    accounts, mailboxes, sentCopies, throttle, watcher, spamLog, spamFeedback, options.Value, logger);
                await session.RunAsync(stoppingToken);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or ObjectDisposedException
                                       or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            logger.LogDebug(ex, "IMAP connection from {Ip} ended", remote);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IMAP connection from {Ip} failed", remote);
        }
        finally
        {
            if (counted)
            {
                Leave(remote!);
            }
        }
    }

    private async Task<Stream> AuthenticateTlsAsync(Stream inner, CancellationToken cancellationToken)
    {
        var certificate = certificates.GetCertificate() ?? throw new InvalidOperationException("TLS certificate is no longer available.");
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        // EnabledSslProtocols = None lets the operating system choose (no TLS 1.3 on Windows Server 2016).
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
        return ssl;
    }

    private bool Enter(IPAddress address)
    {
        var count = _connectionsPerIp.AddOrUpdate(address, 1, (_, c) => c + 1);
        if (count <= options.Value.Imap.MaxConnectionsPerIp)
        {
            return true;
        }

        Leave(address);
        return false;
    }

    private void Leave(IPAddress address)
    {
        if (_connectionsPerIp.AddOrUpdate(address, 0, (_, c) => c - 1) <= 0)
        {
            _connectionsPerIp.TryRemove(new KeyValuePair<IPAddress, int>(address, 0));
        }
    }
}
