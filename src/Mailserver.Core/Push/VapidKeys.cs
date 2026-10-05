using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mailserver.Core.Push;

/// <summary>
/// The server's own key pair for Web Push (VAPID, RFC 8292): browsers bind their subscription to the public key, and every
/// push message is signed with the private key. Created once in data\push and backed up with the other keys.
/// </summary>
public sealed class VapidKeys
{
    private sealed record Stored(string PublicKey, string PrivateKey);

    private readonly ECDsa _key;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = [];

    public VapidKeys(DataPaths paths)
    {
        var folder = Path.Combine(paths.Root, "push");
        var file = Path.Combine(folder, "vapid.json");
        Stored stored;
        if (File.Exists(file))
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file))!;
        }
        else
        {
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = created.ExportParameters(true);
            stored = new Stored(WebPushCrypto.ToBase64Url([4, .. parameters.Q.X!, .. parameters.Q.Y!]), WebPushCrypto.ToBase64Url(parameters.D!));
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, JsonSerializer.Serialize(stored));
        }

        var point = WebPushCrypto.FromBase64Url(stored.PublicKey);
        _key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebPushCrypto.FromBase64Url(stored.PrivateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
        PublicKey = stored.PublicKey;
    }

    /// <summary>Base64url of the uncompressed public key: the "applicationServerKey" for pushManager.subscribe().</summary>
    public string PublicKey { get; }

    /// <summary>The Authorization header value for a push service (one signed token per service, reused for 12 hours).</summary>
    public string Authorization(Uri endpoint, string subject)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_tokens.TryGetValue(audience, out var cached) || cached.Expires - now < TimeSpan.FromHours(1))
            {
                var expires = now.AddHours(12);
                var header = WebPushCrypto.ToBase64Url("""{"typ":"JWT","alg":"ES256"}"""u8);
                var claims = WebPushCrypto.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = expires.ToUnixTimeSeconds(), sub = subject }));
                // ES256: the signature is r || s (IEEE P1363), which is what .NET produces by default.
                var signature = _key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256);
                cached = ($"{header}.{claims}.{WebPushCrypto.ToBase64Url(signature)}", expires);
                _tokens[audience] = cached;
            }

            return $"vapid t={cached.Token}, k={PublicKey}";
        }
    }
}
