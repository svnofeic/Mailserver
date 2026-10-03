using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Raw protocol exchanges for behaviour MailKit does not exercise (literals, non-PEEK fetches, error responses).</summary>
public sealed class ImapProtocolTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Login_with_literals_and_body_fetch_sets_seen()
    {
        await DeliverAsync("Hallo Welt");
        await using var imap = await RawImap.ConnectAsync(_server.ImapsPort);

        await imap.SendAsync("a1 LOGIN {18}\r\n");
        Assert.StartsWith("+", await imap.ReadLineAsync());
        await imap.SendAsync("alice@example.test {21+}\r\ncorrect horse battery\r\n");
        Assert.StartsWith("a1 OK", (await imap.ReadUntilTaggedAsync("a1")).Last());

        var select = await imap.CommandAsync("a2 SELECT inbox");
        Assert.Contains("* 1 EXISTS", select);
        Assert.Contains(select, l => l.StartsWith("* OK [UNSEEN 1]"));
        Assert.Contains(select, l => l.StartsWith("a2 OK [READ-WRITE]"));

        var fetch = string.Join("\n", await imap.CommandAsync("a3 FETCH 1 (BODY[TEXT] BODY[HEADER.FIELDS (subject)])"));
        Assert.Contains(@"FLAGS (\Seen)", fetch);
        Assert.Contains("Hallo Welt", fetch);
        Assert.Contains("BODY[HEADER.FIELDS (SUBJECT)]", fetch);
        Assert.Contains("Subject: Test", fetch);

        var status = await imap.CommandAsync("a4 STATUS INBOX (MESSAGES UNSEEN UIDNEXT)");
        Assert.Contains("* STATUS \"INBOX\" (MESSAGES 1 UNSEEN 0 UIDNEXT 2)", status);
    }

    [Fact]
    public async Task Outlook_style_folder_names_map_to_the_special_folders()
    {
        await using var imap = await RawImap.LoginAsync(_server.ImapsPort);
        var alice = _server.User("alice").Id;
        var foldersBefore = _server.HostMailboxes.ListFolders(alice).Count;

        // Creating "Gesendete Elemente" succeeds without a second Sent folder …
        Assert.StartsWith("a1 OK", (await imap.CommandAsync("a1 CREATE \"Gesendete Elemente\"")).Last());
        Assert.Equal(foldersBefore, _server.HostMailboxes.ListFolders(alice).Count);

        // … is found when asked for by name, but not listed twice …
        Assert.Contains(await imap.CommandAsync("a2 LIST \"\" \"Gesendete Elemente\""), l => l.Contains("\\Sent") && l.Contains("\"Gesendete Elemente\""));
        Assert.DoesNotContain(await imap.CommandAsync("a3 LIST \"\" \"*\""), l => l.Contains("Gesendete Elemente"));

        // … and what the program stores there lands in Sent, visible to webmail and every other device.
        const string message = "From: alice@example.test\r\nSubject: Outlook\r\n\r\nHallo\r\n";
        Assert.StartsWith("a4 OK", (await imap.CommandAsync($"a4 APPEND \"Gesendete Elemente\" (\\Seen) {{{message.Length}+}}\r\n{message}")).Last());
        Assert.Single(_server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(alice, "Sent")!.Id));
        Assert.Contains("* STATUS \"Gesendete Elemente\" (MESSAGES 1)", await imap.CommandAsync("a5 STATUS \"Gesendete Elemente\" (MESSAGES)"));

        // Deleting in such a program moves to "Gelöschte Elemente" = Trash.
        await DeliverAsync("Weg damit");
        await imap.CommandAsync("a6 SELECT INBOX");
        Assert.StartsWith("a7 OK", (await imap.CommandAsync("a7 MOVE 1 \"Deleted Items\"")).Last());
        Assert.Single(_server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(alice, "Trash")!.Id));
        Assert.Contains(await imap.CommandAsync("a8 SELECT Papierkorb"), l => l == "* 1 EXISTS");
    }

    [Fact]
    public async Task Existing_folder_with_an_alias_name_is_used_as_is()
    {
        var alice = _server.User("alice").Id;
        _server.HostMailboxes.CreateFolder(alice, "Spam");
        await using var imap = await RawImap.LoginAsync(_server.ImapsPort);

        Assert.Contains(await imap.CommandAsync("a1 LIST \"\" \"*\""), l => l.EndsWith("\"Spam\""));
        Assert.Contains(await imap.CommandAsync("a2 SELECT Spam"), l => l == "* 0 EXISTS");
        Assert.Single(await imap.CommandAsync("a3 LIST \"\" Spam"), l => l.Contains("\"Spam\""));
    }

    [Fact]
    public async Task Examine_is_read_only()
    {
        await DeliverAsync("Nur lesen");
        await using var imap = await RawImap.LoginAsync(_server.ImapsPort);

        Assert.Contains((await imap.CommandAsync("a1 EXAMINE INBOX")), l => l.StartsWith("a1 OK [READ-ONLY]"));
        var fetch = string.Join("\n", await imap.CommandAsync("a2 FETCH 1 (BODY[])"));
        Assert.DoesNotContain(@"\Seen", fetch);
        Assert.StartsWith("a3 NO", (await imap.CommandAsync(@"a3 STORE 1 +FLAGS (\Deleted)")).Last());
    }

    [Fact]
    public async Task Rejects_invalid_commands_with_bad()
    {
        await using var imap = await RawImap.LoginAsync(_server.ImapsPort);

        Assert.StartsWith("a1 BAD", (await imap.CommandAsync("a1 FETCH 1 BODY[]")).Last());
        Assert.StartsWith("a2 BAD", (await imap.CommandAsync("a2 FROBNICATE")).Last());
        Assert.StartsWith("a3 NO [NONEXISTENT]", (await imap.CommandAsync("a3 SELECT Gibtsnicht")).Last());
        Assert.StartsWith("a4 BAD", (await imap.CommandAsync("a4 SELECT \"unterminated")).Last());
        Assert.StartsWith("a5 OK", (await imap.CommandAsync("a5 NOOP")).Last());
    }

    [Fact]
    public async Task Unauthenticated_clients_cannot_send_large_literals()
    {
        await using var imap = await RawImap.ConnectAsync(_server.ImapsPort);

        Assert.StartsWith("a1 BAD", (await imap.CommandAsync("a1 LOGIN {999999}")).Last());
    }

    [Fact]
    public async Task Locks_out_after_repeated_failures()
    {
        await using var imap = await RawImap.ConnectAsync(_server.ImapsPort);
        for (var i = 0; i < 10; i++)
        {
            Assert.Contains("AUTHENTICATIONFAILED", (await imap.CommandAsync($"f{i} LOGIN alice@example.test wrong{i}")).Last());
        }

        Assert.Contains("UNAVAILABLE", (await imap.CommandAsync("g LOGIN alice@example.test \"correct horse battery\"")).Last());
    }

    private async Task DeliverAsync(string body)
    {
        var message = new MimeMessage { Subject = "Test", Body = new TextPart("plain") { Text = body } };
        message.From.Add(MailboxAddress.Parse("sender@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    private sealed class RawImap : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly SslStream _stream;
        private readonly StreamReader _reader;

        private RawImap(TcpClient client, SslStream stream)
        {
            _client = client;
            _stream = stream;
            _reader = new StreamReader(stream, Encoding.UTF8);
        }

        public static async Task<RawImap> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("mail.example.test");
            var imap = new RawImap(client, ssl);
            Assert.StartsWith("* OK", await imap.ReadLineAsync());
            return imap;
        }

        public static async Task<RawImap> LoginAsync(int port)
        {
            var imap = await ConnectAsync(port);
            Assert.StartsWith("l OK", (await imap.CommandAsync("l LOGIN alice@example.test \"correct horse battery\"")).Last());
            return imap;
        }

        public async Task SendAsync(string text)
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _stream.FlushAsync();
        }

        public async Task<string> ReadLineAsync() =>
            await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("closed");

        public async Task<List<string>> CommandAsync(string command)
        {
            await SendAsync(command + "\r\n");
            return await ReadUntilTaggedAsync(command.Split(' ')[0]);
        }

        public async Task<List<string>> ReadUntilTaggedAsync(string tag)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync();
                lines.Add(line);
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    return lines;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _client.Dispose();
        }
    }
}
