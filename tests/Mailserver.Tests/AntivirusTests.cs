using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Antivirus;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

public sealed class AttachmentInspectorTests
{
    private static readonly IReadOnlySet<string> Blocked = new AntivirusOptions().EffectiveBlockedExtensions;

    private static IReadOnlyList<AttachmentFinding> Inspect(string fileName, byte[] content) =>
        AttachmentInspector.Inspect([new Attachment(fileName, content)], Blocked);

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("Rechnung.pdf.exe")]
    [InlineData("skript.JS")]
    [InlineData("bild.iso")]
    [InlineData("verknuepfung.lnk ")]
    public void Blocks_dangerous_file_types(string name)
    {
        var finding = Assert.Single(Inspect(name, [1, 2, 3]));
        Assert.Equal(FindingKind.Blocked, finding.Kind);
        Assert.Equal("ATTACH_BLOCKED_TYPE", finding.Code);
    }

    [Fact]
    public void Recognises_a_program_disguised_as_pdf()
    {
        var finding = Assert.Single(Inspect("Rechnung.pdf", PortableExecutable()));
        Assert.Equal("ATTACH_EXECUTABLE", finding.Code);
    }

    [Fact]
    public void Looks_into_zip_archives_also_nested()
    {
        Assert.Equal(FindingKind.Blocked, Assert.Single(Inspect("rechnung.zip", Zip(("rechnung.js", [1])))).Kind);
        var nested = Zip(("innen.zip", Zip(("programm.dat", PortableExecutable()))));
        Assert.Equal("ATTACH_EXECUTABLE", Assert.Single(Inspect("aussen.zip", nested)).Code);
        Assert.Empty(Inspect("fotos.zip", Zip(("bild.jpg", [1, 2]), ("text.txt", [3]))));
    }

    [Fact]
    public void Flags_office_macros_and_encrypted_archives_as_suspicious()
    {
        Assert.Equal("ATTACH_MACRO", Assert.Single(Inspect("Angebot.docm", [1])).Code);
        Assert.Equal("ATTACH_MACRO", Assert.Single(Inspect("Angebot.docx", Zip(("word/document.xml", [1]), ("word/vbaProject.bin", [2])))).Code);
        var legacy = Encoding.ASCII.GetBytes("OLE header ").Concat(Encoding.Unicode.GetBytes("_VBA_PROJECT")).ToArray();
        Assert.Equal("ATTACH_MACRO", Assert.Single(Inspect("alt.doc", legacy)).Code);
        Assert.Empty(Inspect("Angebot.docx", Zip(("word/document.xml", [1]))));

        var encrypted = Assert.Single(Inspect("geheim.zip", Encrypt(Zip(("daten.pdf", [1, 2, 3])))));
        Assert.Equal((FindingKind.Suspicious, "ATTACH_ENCRYPTED_ARCHIVE"), (encrypted.Kind, encrypted.Code));
    }

    [Fact]
    public void Clean_documents_pass() => Assert.Empty(Inspect("Bericht.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 ...")));

    [Fact]
    public void Extracts_attachments_of_attached_messages()
    {
        var inner = new MimeMessage { Subject = "innen" };
        var innerBody = new BodyBuilder { TextBody = "x" };
        innerBody.Attachments.Add("tool.exe", new byte[] { 1 });
        inner.Body = innerBody.ToMessageBody();
        var outer = new BodyBuilder { TextBody = "siehe Anhang" };
        outer.Attachments.Add(new MessagePart { Message = inner });
        var message = new MimeMessage { Body = outer.ToMessageBody() };

        Assert.Contains(AttachmentInspector.Extract(message), a => a.FileName == "tool.exe");
    }

    public static byte[] PortableExecutable()
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 0x80);
        return bytes;
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Sets the "encrypted" flag in the local and central headers (.NET cannot write encrypted ZIPs).</summary>
    private static byte[] Encrypt(byte[] zip)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            var signature = BitConverter.ToUInt32(zip, i);
            if (signature == 0x04034b50)
            {
                zip[i + 6] |= 1;
            }
            else if (signature == 0x02014b50)
            {
                zip[i + 8] |= 1;
            }
        }

        return zip;
    }
}

public sealed class ClamAvScannerTests
{
    [Fact]
    public async Task Reports_findings_from_clamd()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var command = new byte[10];
                await stream.ReadExactlyAsync(command);
                Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
                var data = new MemoryStream();
                var length = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(length);
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(length);
                    if (size == 0) break;
                    var chunk = new byte[size];
                    await stream.ReadExactlyAsync(chunk);
                    data.Write(chunk);
                }

                var infected = Encoding.ASCII.GetString(data.ToArray()).Contains("EICAR-STANDARD");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(infected ? "stream: Eicar-Signature FOUND\0" : "stream: OK\0"));
            }
        });

        var scanner = new ClamAvScanner("127.0.0.1", port);
        Assert.Equal(ScanOutcome.Clean, (await scanner.ScanAsync([new Attachment("a.txt", "harmlos"u8.ToArray())], default)).Outcome);
        var result = await scanner.ScanAsync([new Attachment("eicar.com", MalwareFilter.Eicar)], default);
        Assert.Equal((ScanOutcome.Infected, "Eicar-Signature"), (result.Outcome, result.Threat));
        await server;
        listener.Stop();

        Assert.Equal(ScanOutcome.Error, (await new ClamAvScanner("127.0.0.1", port).ScanAsync([new Attachment("a", [1])], default)).Outcome);
    }
}

/// <summary>Stand-in scanner: anything containing "VIRUS-MARKER" is infected, "SCAN-ERROR" makes the scan fail.</summary>
public sealed class FakeScanner : IVirusScanner
{
    public string Name => "Testscanner";

    public Task<ScanResult> ScanAsync(IReadOnlyList<Attachment> files, CancellationToken cancellationToken)
    {
        var text = string.Concat(files.Select(f => Encoding.ASCII.GetString(f.Content)));
        return Task.FromResult(text.Contains("SCAN-ERROR") ? new ScanResult(ScanOutcome.Error, Detail: "Testfehler")
            : text.Contains("VIRUS-MARKER") || text.Contains("EICAR-STANDARD") ? new ScanResult(ScanOutcome.Infected, "Test.Virus")
            : ScanResult.Clean);
    }
}

public sealed class AntivirusIntegrationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static Task<TestServer> StartAsync(Dictionary<string, string?>? settings = null) =>
        TestServer.StartAsync(settings, services => services.AddSingleton<IVirusScanner, FakeScanner>());

    [Fact]
    public async Task Rejects_dangerous_attachments_from_outside()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendInboundAsync(Message("Rechnung", ("Rechnung.pdf.exe", [1, 2, 3]))));

        Assert.Equal(554, (int)ex.StatusCode);
        Assert.Contains("Rechnung.pdf.exe", ex.Message);
        Assert.Empty(_server.Inbox("alice"));
        var entry = Assert.Single(Log(SpamLogAction.Rejected));
        Assert.Equal("ATTACH_BLOCKED_TYPE", entry.Tests);
    }

    [Fact]
    public async Task Rejects_viruses()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() =>
            SendInboundAsync(Message("Hallo", ("bild.jpg", Encoding.ASCII.GetBytes("...VIRUS-MARKER...")))));

        Assert.Contains("Virus gefunden: Test.Virus", ex.Message);
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Delivers_macro_documents_into_junk_with_a_warning()
    {
        await SendInboundAsync(Message("Angebot", ("Angebot.docm", [1, 2])));

        Assert.Empty(_server.Inbox("alice"));
        var junk = _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Junk")!.Id);
        var content = await _server.ReadAsync(Assert.Single(junk));
        Assert.Contains("X-Mailserver-Warning:", content);
        Assert.Contains("X-Virus-Scanned: Testscanner (sauber)", content);
    }

    [Fact]
    public async Task Clean_mail_is_marked_as_scanned()
    {
        await SendInboundAsync(Message("Bericht", ("Bericht.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"))));

        Assert.Contains("X-Virus-Scanned: Testscanner (sauber)", await _server.ReadAsync(Assert.Single(_server.Inbox("alice"))));
    }

    [Fact]
    public async Task Scanner_failure_delivers_by_default_or_defers_if_configured()
    {
        await SendInboundAsync(Message("SCAN-ERROR"));
        Assert.Contains("(Fehler beim Scan)", await _server.ReadAsync(Assert.Single(_server.Inbox("alice"))));

        await _server.DisposeAsync();
        _server = await StartAsync(new Dictionary<string, string?> { ["Mailserver:Antivirus:OnScanError"] = "Defer" });
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendInboundAsync(Message("SCAN-ERROR")));
        Assert.Equal(451, (int)ex.StatusCode);
    }

    [Fact]
    public async Task Outgoing_mail_may_contain_programs_but_no_viruses()
    {
        await SendSubmissionAsync(Message("Tool", ("tool.exe", AttachmentInspectorTests.PortableExecutable())));
        Assert.Single(_server.Inbox("bob"));

        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendSubmissionAsync(Message("Virus", ("x.txt", Encoding.ASCII.GetBytes("VIRUS-MARKER")))));
        Assert.Equal(554, (int)ex.StatusCode);
        Assert.Single(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Antivirus_can_be_switched_off()
    {
        await _server.DisposeAsync();
        _server = await StartAsync(new Dictionary<string, string?> { ["Mailserver:Antivirus:Enabled"] = "false" });

        await SendInboundAsync(Message("Programm", ("setup.exe", [1])));

        Assert.DoesNotContain("X-Virus-Scanned", await _server.ReadAsync(Assert.Single(_server.Inbox("alice"))));
    }

    private IReadOnlyList<SpamLogEntry> Log(string action) =>
        _server.Services.GetRequiredService<SpamLog>().Query(new SpamLogQuery(Action: action));

    private static MimeMessage Message(string subject, params (string Name, byte[] Content)[] attachments)
    {
        var body = new BodyBuilder { TextBody = subject };
        foreach (var (name, content) in attachments)
        {
            body.Attachments.Add(name, content);
        }

        var message = new MimeMessage { Subject = subject, Body = body.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("sender@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        return message;
    }

    private async Task SendInboundAsync(MimeMessage message)
    {
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.StartTls);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private async Task SendSubmissionAsync(MimeMessage message)
    {
        message.From.Clear();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Clear();
        message.To.Add(MailboxAddress.Parse("bob@example.test"));
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.SubmissionPort, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync("alice@example.test", TestServer.Password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
