using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core.Accounts;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Address book: contacts collected from sent mail, saved ones, suggestions and vCard import/export.</summary>
public sealed class ContactsTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Contacts Contacts => _server.Services.GetRequiredService<Contacts>();

    [Fact]
    public async Task Recipients_of_sent_mail_become_suggestions()
    {
        // Already in "Sent" before the address book existed.
        await _server.HostMailboxes.AppendAsync(_server.User("alice"),
            Encoding.UTF8.GetBytes("From: alice@example.test\r\nTo: \"Max Mustermann\" <max@remote.test>\r\nSubject: Alt\r\n\r\nText"), "Sent");

        // Sent from a mail program.
        using (var client = new SmtpClient())
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync("127.0.0.1", _server.SubmissionPort, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync("alice@example.test", TestServer.Password);
            var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "Hallo" } };
            message.From.Add(MailboxAddress.Parse("alice@example.test"));
            message.To.Add(new MailboxAddress("Erika Musterfrau", "erika@remote.test"));
            message.Bcc.Add(MailboxAddress.Parse("geheim@remote.test"));
            message.Cc.Add(MailboxAddress.Parse("bob@example.test"));
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        async Task<List<(string? Name, string Address)>> SuggestAsync(string q)
        {
            await web.GetAsync("/Contacts?handler=Suggest&q=" + Uri.EscapeDataString(q));
            return JsonDocument.Parse(web.LastPage).RootElement.EnumerateArray()
                .Select(e => (e.GetProperty("name").GetString(), e.GetProperty("address").GetString()!)).ToList();
        }

        Assert.Equal([("Erika Musterfrau", "erika@remote.test")], await SuggestAsync("erika"));
        Assert.Equal([("Erika Musterfrau", "erika@remote.test")], await SuggestAsync("musterfrau"));
        Assert.Equal([("Max Mustermann", "max@remote.test")], await SuggestAsync("Max"));
        Assert.Equal([(null, "geheim@remote.test")], await SuggestAsync("geheim"));
        Assert.Contains(("Max Mustermann", "max@remote.test"), await SuggestAsync("remote"));
        Assert.Single(await SuggestAsync("bob"));
        // Never one's own address, and nothing of another mailbox.
        Assert.Empty(await SuggestAsync("alice"));
        Assert.Empty(Contacts.List(_server.User("bob").Id));

        // Webmail sending counts as well; the more often used comes first.
        await web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "max@remote.test"), ("Form.Subject", "Noch mal"), ("Form.Body", "Text")]);
        await web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "max@remote.test"), ("Form.Subject", "Und noch mal"), ("Form.Body", "Text")]);
        Assert.Equal("max@remote.test", (await SuggestAsync("muster"))[0].Address);
        Assert.Equal(3, Contacts.List(_server.User("alice").Id, "max@").Single().TimesUsed);

        // Saved contacts: own name wins over the collected one.
        await web.PostAsync("/Contacts", "/Contacts?handler=Save", ("address", "MAX@remote.test"), ("name", "Max (Büro)"));
        Assert.Equal("Max (Büro)", (await SuggestAsync("max"))[0].Name);
        await web.PostAsync("/Contacts", "/Contacts?handler=Save", ("address", "kein-mail"), ("name", "x"));
        Assert.Contains("keine gültige E-Mail-Adresse", web.LastPage);
        await web.GetAsync("/Contacts?q=büro");
        Assert.Contains("max@remote.test", web.LastPage);
        Assert.DoesNotContain("erika@remote.test", web.LastPage);

        // Compose fields get the suggestions script hook.
        await web.GetAsync("/Mail/Compose");
        Assert.Contains("name=\"Form.To\"", web.LastPage);
    }

    [Fact]
    public async Task Imports_and_exports_vcards()
    {
        var vcf = """
            BEGIN:VCARD
            VERSION:3.0
            N:Mustermann;Max;;;
            FN:Max Mustermann
            item1.EMAIL;type=INTERNET;type=pref:max@remote.test
            EMAIL;TYPE=WORK:max.work@
             remote.test
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            N:Musterfrau;Erika;;;
            EMAIL:erika@remote.test
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Ohne Mail
            END:VCARD
            """;
        var cards = VCards.Read(vcf);
        Assert.Equal([("Max Mustermann", "max@remote.test"), ("Max Mustermann", "max.work@remote.test"), ("Erika Musterfrau", "erika@remote.test")], cards);

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.PostMultipartAsync("/Contacts", "/Contacts?handler=Import", [], ("file", "kontakte.vcf", Encoding.UTF8.GetBytes(vcf)));
        Assert.Contains("3 Adresse(n)", web.LastPage);

        var exported = Encoding.UTF8.GetString(await web.GetBytesAsync("/Contacts?handler=Export"));
        Assert.Equal(3, VCards.Read(exported).Count);
        Assert.Contains(("Erika Musterfrau", "erika@remote.test"), VCards.Read(exported));
    }
}
