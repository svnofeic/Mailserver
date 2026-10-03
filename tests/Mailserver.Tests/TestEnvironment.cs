using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.Dkim;
using Mailserver.Core.Storage;
using Mailserver.Imap;
using Mailserver.Smtp;
using Mailserver.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Mailserver.Tests;

/// <summary>A data directory with database, plus helpers. Disposed by deleting the directory.</summary>
public class TestData : IDisposable
{
    public TestData()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mailserver-tests", Guid.NewGuid().ToString("N"));
        Paths = new DataPaths(Directory);
        Paths.EnsureCreated();
        Database = new Database(Paths);
        Database.Migrate();
        Accounts = new AccountStore(Database);
        Mailboxes = new MailboxStore(Database, Paths);
    }

    public string Directory { get; }
    public DataPaths Paths { get; }
    public Database Database { get; }
    public AccountStore Accounts { get; }
    public MailboxStore Mailboxes { get; }

    public virtual void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Runs the complete mail server host on free local ports, with a fake remote MX as smart host.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string Domain = "example.test";
    public const string Password = "correct horse battery";

    private readonly WebApplication _host;

    private TestServer(WebApplication host, int inboundPort, int submissionPort, int imapPort, int imapsPort, int webPort,
        FakeRemoteServer remote, string directory)
    {
        _host = host;
        WebPort = webPort;
        InboundPort = inboundPort;
        SubmissionPort = submissionPort;
        ImapPort = imapPort;
        ImapsPort = imapsPort;
        Remote = remote;
        HostDirectory = directory;
    }

    public int InboundPort { get; }
    public int SubmissionPort { get; }
    public int ImapPort { get; }
    public int ImapsPort { get; }
    public int WebPort { get; }
    public string DataDirectory => Path.Combine(HostDirectory, "data");
    public FakeRemoteServer Remote { get; }
    public string HostDirectory { get; }
    public IServiceProvider Services => _host.Services;

    /// <param name="settings">Additional configuration, e.g. spam filter settings.</param>
    /// <param name="services">Service overrides registered before the defaults (e.g. a fake DNS resolver).</param>
    public static async Task<TestServer> StartAsync(IDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mailserver-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, "server.pfx");
        await File.WriteAllBytesAsync(pfxPath, CreateSelfSignedPfx("mail.example.test"));

        var remote = await FakeRemoteServer.StartAsync();
        var inboundPort = GetFreePort();
        var submissionPort = GetFreePort();
        var imapPort = GetFreePort();
        var imapsPort = GetFreePort();
        var webPort = GetFreePort();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mailserver:Hostname"] = "mail.example.test",
            ["Mailserver:DataDirectory"] = Path.Combine(directory, "data"),
            ["Mailserver:Tls:PfxPath"] = pfxPath,
            ["Mailserver:Smtp:ListenAddresses:0"] = "127.0.0.1",
            ["Mailserver:Smtp:InboundPort"] = inboundPort.ToString(),
            ["Mailserver:Smtp:SubmissionPort"] = submissionPort.ToString(),
            ["Mailserver:Smtp:SubmissionTlsPort"] = "0",
            ["Mailserver:Imap:ListenAddresses:0"] = "127.0.0.1",
            ["Mailserver:Imap:Port"] = imapPort.ToString(),
            ["Mailserver:Imap:TlsPort"] = imapsPort.ToString(),
            ["Mailserver:Web:ListenAddresses:0"] = "127.0.0.1",
            ["Mailserver:Web:HttpsPort"] = webPort.ToString(),
            ["Mailserver:Delivery:PollInterval"] = "00:00:01",
            ["Mailserver:Delivery:SmartHost:Host"] = "127.0.0.1",
            ["Mailserver:Delivery:SmartHost:Port"] = remote.Port.ToString(),
            ["Mailserver:Delivery:SmartHost:Security"] = "None",
        });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Configuration.AddJsonFile(Path.Combine(directory, "data", "settings.json"), optional: true, reloadOnChange: true);
        services?.Invoke(builder.Services);
        builder.Services.AddMailserver(builder.Configuration);
        builder.Services.AddImapServer();
        builder.AddMailserverWeb();
        var host = builder.Build();
        host.UseMailserverWeb();

        var server = new TestServer(host, inboundPort, submissionPort, imapPort, imapsPort, webPort, remote, directory);
        server.Seed();
        await host.StartAsync();
        foreach (var port in new[] { inboundPort, submissionPort, imapPort, imapsPort, webPort })
        {
            await WaitForPortAsync(port);
        }
        return server;
    }

    public AccountStore HostAccounts => Services.GetRequiredService<AccountStore>();
    public MailboxStore HostMailboxes => Services.GetRequiredService<MailboxStore>();

    public Account User(string localPart) => HostAccounts.FindAccount(EmailAddress.Create(localPart, Domain))!;

    public IReadOnlyList<StoredMessage> Inbox(string localPart)
    {
        var account = User(localPart);
        var folder = HostMailboxes.GetFolder(account.Id, MailboxStore.Inbox);
        return folder is null ? [] : HostMailboxes.ListMessages(folder.Id);
    }

    public async Task<string> ReadAsync(StoredMessage message)
    {
        await using var stream = HostMailboxes.OpenMessage(message);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    private void Seed()
    {
        var accounts = HostAccounts;
        var domain = accounts.AddDomain(Domain, "test");
        Services.GetRequiredService<DkimKeyStore>().GenerateKey(domain.Name, "test");
        var mailboxes = HostMailboxes;
        mailboxes.EnsureDefaultFolders(accounts.AddAccount(EmailAddress.Create("alice", Domain), Password).Id);
        mailboxes.EnsureDefaultFolders(accounts.AddAccount(EmailAddress.Create("bob", Domain), Password).Id);
        accounts.AddAlias(EmailAddress.Create("info", Domain), [EmailAddress.Create("alice", Domain), EmailAddress.Create("bob", Domain)]);
        accounts.AddAlias(EmailAddress.Create("forward", Domain), [EmailAddress.Parse("someone@remote.test")]);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        await _host.DisposeAsync();
        await Remote.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(HostDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            await Task.Delay(100);
        }
    }

    private static int _nextPort = 20000 + Random.Shared.Next(0, 5000);

    /// <summary>
    /// Hands out each port once per test run, from a range below the OS ephemeral range (32768+). Asking the OS for port 0
    /// is racy here: test classes run in parallel, and a client socket or another test server could take the port between
    /// probing and binding.
    /// </summary>
    internal static int GetFreePort()
    {
        while (true)
        {
            var port = Interlocked.Increment(ref _nextPort);
            if (port > 32000)
            {
                throw new InvalidOperationException("No free test ports left");
            }

            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // In use by something else on this machine; try the next one.
            }
        }
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        throw new TimeoutException($"Port {port} did not open");
    }

    private static byte[] CreateSelfSignedPfx(string hostname)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.Export(X509ContentType.Pfx);
    }
}

public sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Content);

/// <summary>Stands in for a remote MX. Rejects recipients whose local part starts with "reject" (550) or "later" (451).</summary>
public sealed class FakeRemoteServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _running;

    public int Port { get; private init; }
    public ConcurrentQueue<ReceivedMail> Messages { get; } = new();

    public static async Task<FakeRemoteServer> StartAsync()
    {
        var server = new FakeRemoteServer { Port = TestServer.GetFreePort() };
        var options = new SmtpServerOptionsBuilder().ServerName("remote.test").Port(server.Port).Build();
        var provider = new SmtpServer.ComponentModel.ServiceProvider();
        provider.Add(new Filter());
        provider.Add(new Store(server.Messages));
        server._running = new SmtpServer.SmtpServer(options, provider).StartAsync(server._cts.Token);
        await Task.Delay(100);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_running is not null)
            {
                await _running;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Filter : IMailboxFilter
    {
        public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
        {
            if (to.User.StartsWith("reject", StringComparison.OrdinalIgnoreCase))
            {
                throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "5.1.1 No such user"));
            }

            if (to.User.StartsWith("later", StringComparison.OrdinalIgnoreCase))
            {
                throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.Unavailable, "4.2.0 Try again later"));
            }

            return Task.FromResult(true);
        }
    }

    private sealed class Store(ConcurrentQueue<ReceivedMail> messages) : MessageStore
    {
        public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken)
        {
            messages.Enqueue(new ReceivedMail(
                string.IsNullOrEmpty(transaction.From?.User) ? "" : transaction.From.AsAddress(),
                transaction.To.Select(t => t.AsAddress()).ToList(),
                System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
            return Task.FromResult(SmtpResponse.Ok);
        }
    }
}
