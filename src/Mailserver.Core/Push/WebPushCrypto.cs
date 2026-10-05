using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mailserver.Core.Push;

/// <summary>
/// Message encryption for Web Push (RFC 8291 with the aes128gcm content coding of RFC 8188). Only the browser that created the
/// subscription can read the content; the push service of Google, Apple, Mozilla or Microsoft just forwards it.
/// </summary>
public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    /// <param name="payload">Plain text (at most about 3.9 KB).</param>
    /// <param name="userAgentPublicKey">"p256dh" of the subscription: uncompressed P-256 point (65 bytes).</param>
    /// <param name="authSecret">"auth" of the subscription (16 bytes).</param>
    /// <param name="serverKey">Ephemeral key of this message; only fixed in tests.</param>
    /// <param name="salt">16 random bytes; only fixed in tests.</param>
    public static byte[] Encrypt(byte[] payload, byte[] userAgentPublicKey, byte[] authSecret, ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        if (payload.Length > RecordSize - 103)
        {
            throw new ArgumentException("Push message too large.", nameof(payload));
        }

        using var ownKey = serverKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var key = serverKey ?? ownKey!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        var serverPublic = PublicPoint(key);

        using var userAgent = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] },
        });
        var sharedSecret = key.DeriveRawSecretAgreement(userAgent.PublicKey);

        // IKM = HKDF(auth secret, ECDH secret, "WebPush: info" || 0 || ua public || as public)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), userAgentPublicKey, serverPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // A single record: content followed by the delimiter 0x02 (last record), no padding.
        var plain = new byte[payload.Length + 1];
        payload.CopyTo(plain, 0);
        plain[^1] = 2;
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        // Header: salt (16) || record size (4) || key id length (1) || key id = server public key (65)
        var header = new byte[16 + 4 + 1 + serverPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>Uncompressed public point (0x04 || X || Y) of a P-256 key.</summary>
    public static byte[] PublicPoint(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        return Concat([4], parameters.Q.X!, parameters.Q.Y!);
    }

    public static byte[] FromBase64Url(string value)
    {
        var text = value.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    public static string ToBase64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
