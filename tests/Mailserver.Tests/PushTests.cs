using System.Security.Cryptography;
using System.Text;
using Mailserver.Core.Push;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

public sealed class WebPushCryptoTests
{
    // Produced with http_ece (the reference implementation by the author of RFC 8188/8291) from fixed keys and salt.
    private const string UserAgentPublic = "BB4YUy_UdUwC8wQdnHXOszuD_9gax85P6ILMscmLxYlupGwxHE4v9A3ZajZT5uRURdMt_khuztdcepDGoYiBwKM";
    private const string ServerPrivate = "CQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQk";
    private const string ServerPublic = "BHE1-k_ZOgnc6Yu_aBtL_PUOfA1jVOYq-wv_KjQpYXhl7UwfAt25Aj7lalV-UV1qncZsEfIglg3llDNN9Yh3ZyQ";
    private const string Auth = "AwMDAwMDAwMDAwMDAwMDAw";
    private const string Salt = "BQUFBQUFBQUFBQUFBQUFBQ";
    private const string Payload = """{"title":"Anna Weber","body":"Treffen nächste Woche"}""";
    private const string Expected =
        "BQUFBQUFBQUFBQUFBQUFBQAAEABBBHE1-k_ZOgnc6Yu_aBtL_PUOfA1jVOYq-wv_KjQpYXhl7UwfAt25Aj7lalV-UV1qncZsEfIglg3llDNN9Yh3ZyQ90jNoSEkYrtQ3I4RxachgkLSxpeNqqP6s2KPWb6atRhSvC63SIFUowM3lSWb9KTlzIKz1kv8SV-Pa2ZaRYV14aN4_0XT5Sw";

    [Fact]
    public void Encrypts_exactly_like_the_reference_implementation()
    {
        var serverPublic = WebPushCrypto.FromBase64Url(ServerPublic);
        using var serverKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebPushCrypto.FromBase64Url(ServerPrivate),
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });

        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(Payload), WebPushCrypto.FromBase64Url(UserAgentPublic),
            WebPushCrypto.FromBase64Url(Auth), serverKey, WebPushCrypto.FromBase64Url(Salt));

        Assert.Equal(Expected, WebPushCrypto.ToBase64Url(body));
    }

    [Fact]
    public void Every_message_gets_a_new_key_and_salt()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var first = WebPushCrypto.Encrypt("x"u8.ToArray(), WebPushCrypto.PublicPoint(userAgent), new byte[16]);
        var second = WebPushCrypto.Encrypt("x"u8.ToArray(), WebPushCrypto.PublicPoint(userAgent), new byte[16]);
        Assert.NotEqual(first[..16], second[..16]);
        Assert.Equal(16 + 4 + 1 + 65 + 2 + 16, first.Length);
    }
}

/// <summary>A browser as far as push is concerned: its key pair, auth secret and the decryption of incoming messages.</summary>
public sealed class FakeBrowserKeys : IDisposable
{
    public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
    public string P256dh => WebPushCrypto.ToBase64Url(WebPushCrypto.PublicPoint(Key));
    public string AuthText => WebPushCrypto.ToBase64Url(Auth);

    /// <summary>RFC 8291 decryption, written independently of the server code.</summary>
    public string Decrypt(byte[] body)
    {
        var salt = body[..16];
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var secret = Key.DeriveRawSecretAgreement(server.PublicKey);
        var prkKey = HMACSHA256.HashData(Auth, secret);
        var ikm = HMACSHA256.HashData(prkKey, (byte[])[.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. WebPushCrypto.PublicPoint(Key), .. serverPublic, 1])[..32];
        var prk = HMACSHA256.HashData(salt, ikm);
        var cek = HMACSHA256.HashData(prk, (byte[])[.. Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 1])[..16];
        var nonce = HMACSHA256.HashData(prk, (byte[])[.. Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 1])[..12];
        var record = body[(21 + idLength)..];
        var plain = new byte[record.Length - 16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Decrypt(nonce, record[..^16], record[^16..], plain);
        }

        Assert.Equal(2, plain[^1]); // last-record delimiter
        return Encoding.UTF8.GetString(plain[..^1]);
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Push service of a browser vendor: accepts messages, or answers with a configured status.</summary>
public sealed class FakePushService : FakeCloud
{
    public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.AspNetCore.Http.IHeaderDictionary Headers, byte[] Body)> Received { get; } = new();
    public int Status { get; set; } = 201;

    public static async Task<FakePushService> CreateAsync()
    {
        var service = new FakePushService();
        await service.StartAsync();
        return service;
    }

    protected override async Task HandleAsync(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value));
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        Received.Enqueue((headers, buffer.ToArray()));
        context.Response.StatusCode = Status;
    }
}

public sealed class PushNotificationTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private FakePushService _push = null!;
    private readonly FakeBrowserKeys _browser = new();

    public async Task InitializeAsync()
    {
        _push = await FakePushService.CreateAsync();
        _server = await TestServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _push.DisposeAsync();
        _browser.Dispose();
    }

    private PushSubscriptionStore Store => _server.Services.GetRequiredService<PushSubscriptionStore>();

    private PushSubscription Subscribe(string path = "/push/alice") =>
        Store.Save(_server.User("alice").Id, _push.Url + path, _browser.P256dh, _browser.AuthText, "Chrome auf Android");

    private async Task SendMailAsync(string subject, string from = "Anna Weber <anna@remote.test>")
    {
        var message = new MimeKit.MimeMessage { Subject = subject, Body = new MimeKit.TextPart("plain") { Text = "Hallo" } };
        message.From.Add(MimeKit.MailboxAddress.Parse(from));
        message.To.Add(MimeKit.MailboxAddress.Parse("alice@example.test"));
        using var client = new MailKit.Net.Smtp.SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, MailKit.Security.SecureSocketOptions.StartTls);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task New_mail_in_the_inbox_is_pushed_encrypted_and_signed()
    {
        Subscribe();

        await SendMailAsync("Treffen nächste Woche");

        await TestServer.WaitUntilAsync(() => !_push.Received.IsEmpty, "push message");
        var (headers, body) = Assert.Single(_push.Received);
        Assert.Equal("aes128gcm", headers.ContentEncoding.ToString());
        Assert.Equal("86400", headers["TTL"].ToString());
        Assert.False(headers.ContainsKey("Topic")); // Apple refuses it
        Assert.StartsWith("vapid t=", headers.Authorization.ToString());

        using var json = System.Text.Json.JsonDocument.Parse(_browser.Decrypt(body));
        Assert.Equal("Anna Weber", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("Treffen nächste Woche", json.RootElement.GetProperty("body").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("unread").GetInt32());
        var uid = _server.Inbox("alice").Single().Uid;
        Assert.Equal($"/Mail/Read?folder=INBOX&uid={uid}", json.RootElement.GetProperty("url").GetString());
        await TestServer.WaitUntilAsync(() => Store.ForAccount(_server.User("alice").Id).Single().LastSent is not null, "recorded");
    }

    [Fact]
    public void Vapid_token_is_signed_with_the_server_key()
    {
        var keys = _server.Services.GetRequiredService<VapidKeys>();
        var header = keys.Authorization(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "https://mail.example.test");

        var token = header["vapid t=".Length..header.IndexOf(',')];
        Assert.EndsWith($"k={keys.PublicKey}", header);
        var parts = token.Split('.');
        using var claims = System.Text.Json.JsonDocument.Parse(WebPushCrypto.FromBase64Url(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("https://mail.example.test", claims.RootElement.GetProperty("sub").GetString());
        Assert.True(claims.RootElement.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        var point = WebPushCrypto.FromBase64Url(keys.PublicKey);
        using var verify = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
        Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), WebPushCrypto.FromBase64Url(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Spam_is_not_pushed_and_unsubscribed_devices_are_removed()
    {
        Subscribe();
        _server.Services.GetRequiredService<Mailserver.Core.Rules.RuleStore>().Add("alice@example.test", "Werbung",
            [new Mailserver.Core.Rules.RuleCondition(Mailserver.Core.Rules.RuleField.Subject, Mailserver.Core.Rules.RuleOperator.Contains, "Gewinnspiel")],
            Mailserver.Core.Rules.RuleAction.Junk);

        await SendMailAsync("Ihr Gewinnspiel");
        await TestServer.WaitUntilAsync(() => _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Junk")!.Id).Count == 1, "spam delivered");
        await Task.Delay(300);
        Assert.Empty(_push.Received);

        // The browser was uninstalled: the push service answers 410 Gone.
        _push.Status = 410;
        await SendMailAsync("Wichtig");
        await TestServer.WaitUntilAsync(() => Store.ForAccount(_server.User("alice").Id).Count == 0, "subscription removed");
    }

    [Fact]
    public async Task User_switches_notifications_on_tests_and_removes_a_device()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Account/Notifications");
        Assert.Contains("data-key=\"" + _server.Services.GetRequiredService<VapidKeys>().PublicKey, web.LastPage);

        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Subscribe",
            ("endpoint", _push.Url + "/push/web"), ("p256dh", _browser.P256dh), ("auth", _browser.AuthText));
        Assert.Contains("Benachrichtigungen auf", web.LastPage);
        var device = Assert.Single(Store.ForAccount(_server.User("alice").Id));

        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Test");
        Assert.Contains("vom Push-Dienst angenommen", web.LastPage);
        Assert.Contains("funktionieren", _browser.Decrypt(_push.Received.Single().Body));

        // A refusal is shown right away with the push service's answer, and stays visible at the device.
        _push.Status = 403;
        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Test");
        Assert.Contains("antwortet 403", web.LastPage);
        Assert.Contains("Uhrzeit des Servers", web.LastPage);
        Assert.Contains("Letzter Fehler", web.LastPage);
        Assert.Equal(1, Store.ForAccount(_server.User("alice").Id).Single().Failures);
        _push.Status = 201;
        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Test");
        Assert.DoesNotContain("Letzter Fehler", web.LastPage);

        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Remove", ("id", device.Id.ToString()));
        Assert.Empty(Store.ForAccount(_server.User("alice").Id));

        await web.PostAsync("/Account/Notifications", "/Account/Notifications?handler=Subscribe",
            ("endpoint", "http://evil.example/push"), ("p256dh", _browser.P256dh), ("auth", _browser.AuthText));
        Assert.Contains("Ungültige Push-Adresse", web.LastPage);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36", "Chrome auf Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari auf iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0", "Edge auf Windows")]
    public void Device_names(string userAgent, string name) => Assert.Equal(name, Mailserver.Web.Pages.Account.NotificationsModel.DeviceName(userAgent));
}
