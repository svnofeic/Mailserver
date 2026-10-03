using Mailserver.AntiSpam.Dns;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;

namespace Mailserver.AntiSpam.Checks;

public enum DkimResult
{
    None,
    Pass,
    Fail,
    TempError,
    PermError,
}

public sealed record DkimSignatureResult(DkimResult Result, string Domain, string Selector);

/// <summary>Verifies DKIM signatures (RFC 6376) with MimeKit, fetching public keys through <see cref="IDnsResolver"/>.</summary>
public sealed class DkimChecker(IDnsResolver dns)
{
    private const int MaxSignatures = 5;

    public async Task<IReadOnlyList<DkimSignatureResult>> VerifyAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var results = new List<DkimSignatureResult>();
        var verifier = new DkimVerifier(new DnsKeyLocator(dns));
        foreach (var header in message.Headers.Where(h => h.Id == HeaderId.DkimSignature).Take(MaxSignatures))
        {
            var tags = ParseTags(header.Value);
            var domain = tags.GetValueOrDefault("d", "").ToLowerInvariant();
            var selector = tags.GetValueOrDefault("s", "");
            try
            {
                var valid = await verifier.VerifyAsync(message, header, cancellationToken);
                results.Add(new DkimSignatureResult(valid ? DkimResult.Pass : DkimResult.Fail, domain, selector));
            }
            catch (DnsUnavailableException)
            {
                results.Add(new DkimSignatureResult(DkimResult.TempError, domain, selector));
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or InvalidOperationException
                                           or KeyNotFoundException or CryptoException)
            {
                // Missing key, malformed header, unsupported algorithm.
                results.Add(new DkimSignatureResult(DkimResult.PermError, domain, selector));
            }
        }

        return results;
    }

    public static Dictionary<string, string> ParseTags(string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                tags[part[..eq].Trim()] = string.Concat(part[(eq + 1)..].Where(c => !char.IsWhiteSpace(c)));
            }
        }

        return tags;
    }

    private sealed class DnsUnavailableException : Exception;

    private sealed class DnsKeyLocator(IDnsResolver dns) : DkimPublicKeyLocatorBase
    {
        public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector,
            CancellationToken cancellationToken = default) =>
            LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

        public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector,
            CancellationToken cancellationToken = default)
        {
            var result = await dns.GetTxtAsync($"{selector}._domainkey.{domain}", cancellationToken);
            if (result.Status == DnsStatus.Error)
            {
                throw new DnsUnavailableException();
            }

            var record = result.Records.FirstOrDefault(r => r.Contains("p=", StringComparison.Ordinal))
                         ?? throw new KeyNotFoundException($"No DKIM key at {selector}._domainkey.{domain}");
            return GetPublicKey(record);
        }
    }
}
