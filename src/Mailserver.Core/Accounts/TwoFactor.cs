using System.Security.Cryptography;
using System.Text;
using Mailserver.Core.Data;
using Mailserver.Core.Security;

namespace Mailserver.Core.Accounts;

public sealed record TrustedDevice(long Id, string Device, DateTimeOffset Created, DateTimeOffset LastUsed, DateTimeOffset Expires);

/// <summary>
/// Two-factor sign-in for the web interface with an authenticator app (TOTP, RFC 6238: 6 digits, 30 seconds, SHA-1 – what
/// Google/Microsoft Authenticator and the iPhone's Passwords app use). Mail programs (IMAP/SMTP) keep using the password.
/// Also: one-time recovery codes, and devices that may skip the code for <see cref="TrustDuration"/>.
/// </summary>
public sealed class TwoFactor(Database database, AccountStore accounts, SecretProtector secrets, TimeProvider timeProvider)
{
    public const int RecoveryCodeCount = 10;
    public static readonly TimeSpan TrustDuration = TimeSpan.FromDays(30);
    private const int Step = 30;

    public bool IsEnabled(long accountId) => EnabledSince(accountId) is not null;

    public DateTimeOffset? EnabledSince(long accountId)
    {
        using var connection = database.Open();
        return connection.Query("SELECT enabled_utc FROM totp WHERE account_id = $id", r => r.GetDbTime(0), ("$id", accountId))
            .Select(d => (DateTimeOffset?)d).SingleOrDefault();
    }

    /// <summary>A new random secret (Base32) for setting up the app; stored only once confirmed with a code.</summary>
    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    /// <summary>The link the authenticator app understands (also shown as QR code).</summary>
    public static string SetupUri(string issuer, string address, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(address)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}" +
        "&algorithm=SHA1&digits=6&period=30";

    /// <summary>Switches two-factor sign-in on if <paramref name="code"/> fits the secret; returns the recovery codes, or null.</summary>
    public IReadOnlyList<string>? Enable(long accountId, string secret, string code)
    {
        if (FindStep(FromBase32(secret), code, 0) is not { } step)
        {
            return null;
        }

        using (var connection = database.Open())
        {
            connection.Execute(
                """
                INSERT INTO totp (account_id, secret, enabled_utc, last_step) VALUES ($id, $secret, $now, $step)
                ON CONFLICT (account_id) DO UPDATE SET secret = $secret, enabled_utc = $now, last_step = $step
                """,
                ("$id", accountId), ("$secret", secrets.Protect(secret)), ("$now", timeProvider.GetUtcNow().ToDbTime()), ("$step", step));
        }

        return NewRecoveryCodes(accountId);
    }

    /// <summary>Switches it off and forgets recovery codes and trusted devices.</summary>
    public void Disable(long accountId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM totp WHERE account_id = $id", ("$id", accountId));
        connection.Execute("DELETE FROM recovery_codes WHERE account_id = $id", ("$id", accountId));
        connection.Execute("DELETE FROM trusted_devices WHERE account_id = $id", ("$id", accountId));
    }

    /// <summary>Checks a code from the app (each code only once) or a recovery code (used up).</summary>
    public bool Verify(long accountId, string? code)
    {
        code = (code ?? "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return false;
        }

        using var connection = database.Open();
        var row = connection.Query("SELECT secret, last_step FROM totp WHERE account_id = $id", r => (r.GetString(0), r.GetInt64(1)),
            ("$id", accountId)).SingleOrDefault();
        if (row == default)
        {
            return false;
        }

        if (code.Length == 6 && code.All(char.IsAsciiDigit))
        {
            if (secrets.Unprotect(row.Item1) is not { } secret || FindStep(FromBase32(secret), code, row.Item2) is not { } step)
            {
                return false;
            }

            // A code seen once (e.g. by someone looking over the shoulder) is not accepted again.
            return connection.Execute("UPDATE totp SET last_step = $step WHERE account_id = $id AND last_step < $step",
                ("$step", step), ("$id", accountId)) == 1;
        }

        return connection.Execute("DELETE FROM recovery_codes WHERE account_id = $id AND code_hash = $hash",
            ("$id", accountId), ("$hash", Hash(code))) == 1;
    }

    public int RecoveryCodesLeft(long accountId)
    {
        using var connection = database.Open();
        return Convert.ToInt32(connection.Scalar("SELECT COUNT(*) FROM recovery_codes WHERE account_id = $id", ("$id", accountId)));
    }

    /// <summary>Replaces all recovery codes; the plain codes are shown once.</summary>
    public IReadOnlyList<string> NewRecoveryCodes(long accountId)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var codes = Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => new string(RandomNumberGenerator.GetItems<char>(alphabet, 10)))
            .ToList();
        using var connection = database.Open();
        connection.Execute("DELETE FROM recovery_codes WHERE account_id = $id", ("$id", accountId));
        foreach (var code in codes)
        {
            connection.Execute("INSERT INTO recovery_codes (account_id, code_hash) VALUES ($id, $hash)", ("$id", accountId), ("$hash", Hash(code)));
        }

        return codes.Select(c => $"{c[..5]}-{c[5..]}").ToList();
    }

    // ---- Trusted devices ----

    /// <summary>Remembers this device; returns the token for its cookie. It stays valid until the password changes.</summary>
    public string TrustDevice(long accountId, string device)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = timeProvider.GetUtcNow();
        using var connection = database.Open();
        connection.Execute("DELETE FROM trusted_devices WHERE expires_utc < $now", ("$now", now.ToDbTime()));
        connection.Execute(
            """
            INSERT INTO trusted_devices (account_id, token_hash, stamp, device, created_utc, last_used_utc, expires_utc)
            VALUES ($id, $hash, $stamp, $device, $now, $now, $expires)
            """,
            ("$id", accountId), ("$hash", Hash(token)), ("$stamp", accounts.GetSecurityStamp(accountId) ?? ""), ("$device", device),
            ("$now", now.ToDbTime()), ("$expires", now.Add(TrustDuration).ToDbTime()));
        return token;
    }

    public bool IsTrusted(long accountId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var connection = database.Open();
        return connection.Execute(
            "UPDATE trusted_devices SET last_used_utc = $now WHERE account_id = $id AND token_hash = $hash AND expires_utc > $now AND stamp = $stamp",
            ("$now", now.ToDbTime()), ("$id", accountId), ("$hash", Hash(token)), ("$stamp", accounts.GetSecurityStamp(accountId) ?? "-")) == 1;
    }

    public IReadOnlyList<TrustedDevice> Devices(long accountId)
    {
        var now = timeProvider.GetUtcNow().ToDbTime();
        using var connection = database.Open();
        return connection.Query(
            "SELECT id, device, created_utc, last_used_utc, expires_utc FROM trusted_devices WHERE account_id = $id AND expires_utc > $now AND stamp = $stamp ORDER BY last_used_utc DESC",
            r => new TrustedDevice(r.GetInt64(0), r.GetString(1), r.GetDbTime(2), r.GetDbTime(3), r.GetDbTime(4)),
            ("$id", accountId), ("$now", now), ("$stamp", accounts.GetSecurityStamp(accountId) ?? "-"));
    }

    public bool ForgetDevice(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM trusted_devices WHERE account_id = $account AND id = $id", ("$account", accountId), ("$id", id)) > 0;
    }

    public void ForgetDevices(long accountId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM trusted_devices WHERE account_id = $id", ("$id", accountId));
    }

    // ---- TOTP ----

    /// <summary>The time step at which <paramref name="code"/> is valid (now ± 30 seconds), newer than <paramref name="after"/>.</summary>
    private long? FindStep(byte[] key, string code, long after)
    {
        if (key.Length == 0 || code.Length != 6)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds() / Step;
        for (var step = now - 1; step <= now + 1; step++)
        {
            if (step > after && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step)), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32(byte[] data)
    {
        var result = new StringBuilder();
        for (var bit = 0; bit < data.Length * 8; bit += 5)
        {
            var value = 0;
            for (var i = 0; i < 5; i++)
            {
                var index = bit + i;
                value = (value << 1) | (index < data.Length * 8 ? (data[index / 8] >> (7 - index % 8)) & 1 : 0);
            }

            result.Append(Base32Alphabet[value]);
        }

        return result.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        text = text.Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                return [];
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
