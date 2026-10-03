using MailKit.Net.Imap;
using MailKit.Security;
using Mailserver.Imap.Protocol;

namespace Mailserver.Tests;

public sealed class ImapTraceTests
{
    [Fact]
    public async Task Trace_records_commands_and_responses_without_passwords()
    {
        await using var server = await TestServer.StartAsync(new Dictionary<string, string?> { ["Mailserver:Imap:Trace"] = "true" });
        using (var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", server.ImapsPort, SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync("alice@example.test", TestServer.Password);
            await client.GetFoldersAsync(client.PersonalNamespaces[0]);
            await client.DisconnectAsync(true);
        }

        var file = Assert.Single(Directory.GetFiles(Path.Combine(server.DataDirectory, "logs"), "imap-trace-*.log"));
        var log = await File.ReadAllTextAsync(file);
        Assert.Contains("angemeldet als alice@example.test", log);
        Assert.Contains(" LIST ", log);
        Assert.Contains("S: * LIST (\\HasNoChildren \\Sent) \"/\" \"Sent\"", log);
        Assert.DoesNotContain(TestServer.Password, log);
        Assert.DoesNotContain(Convert.ToBase64String("\0alice@example.test\0correct horse battery"u8.ToArray()), log);
    }

    [Fact]
    public async Task Trace_is_off_by_default()
    {
        await using var server = await TestServer.StartAsync();
        using (var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", server.ImapsPort, SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync("alice@example.test", TestServer.Password);
        }

        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "logs")));
    }

    [Theory]
    [InlineData("a1 LOGIN alice@example.test geheim", "a1 LOGIN alice@example.test ***")]
    [InlineData("a1 login \"alice@example.test\" \"ge heim\"", "a1 login \"alice@example.test\" ***")]
    [InlineData("a2 AUTHENTICATE PLAIN AGFsaWNlAGdlaGVpbQ==", "a2 AUTHENTICATE PLAIN ***")]
    [InlineData("a3 LIST \"\" \"*\"", "a3 LIST \"\" \"*\"")]
    public void Masks_credentials(string line, string expected) => Assert.Equal(expected, ImapTrace.Mask(line));
}
