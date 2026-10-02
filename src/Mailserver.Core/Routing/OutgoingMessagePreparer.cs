using Mailserver.Core.Accounts;
using Mailserver.Core.Dkim;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;

namespace Mailserver.Core.Routing;

/// <summary>
/// Completes messages from authenticated clients (Message-ID, Date) and signs them with DKIM.
/// </summary>
public sealed class OutgoingMessagePreparer(
    AccountStore accounts,
    DkimKeyStore dkimKeys,
    IOptions<MailserverOptions> options,
    ILogger<OutgoingMessagePreparer> logger)
{
    private static readonly HeaderId[] SignedHeaders =
    [
        HeaderId.From, HeaderId.Sender, HeaderId.ReplyTo, HeaderId.To, HeaderId.Cc, HeaderId.Subject, HeaderId.Date,
        HeaderId.MessageId, HeaderId.InReplyTo, HeaderId.References, HeaderId.MimeVersion, HeaderId.ContentType,
        HeaderId.ContentTransferEncoding,
    ];

    public async Task<byte[]> PrepareAsync(byte[] rawMessage, CancellationToken cancellationToken)
    {
        MimeMessage message;
        using (var input = new MemoryStream(rawMessage, writable: false))
        {
            message = await MimeMessage.LoadAsync(input, cancellationToken);
        }

        if (string.IsNullOrEmpty(message.MessageId))
        {
            message.MessageId = MimeUtils.GenerateMessageId(options.Value.Hostname);
        }

        if (!message.Headers.Contains(HeaderId.Date))
        {
            message.Date = DateTimeOffset.Now;
        }

        TrySign(message);

        using var output = new MemoryStream(rawMessage.Length + 1024);
        await message.WriteToAsync(FormatOptions.Default, output, cancellationToken);
        return output.ToArray();
    }

    private void TrySign(MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        if (from is null || !EmailAddress.TryParse(from.Address, out var fromAddress))
        {
            return;
        }

        var domain = accounts.GetDomain(fromAddress.Domain);
        if (domain is null)
        {
            return;
        }

        if (domain.DkimSelector is not { Length: > 0 } selector || !dkimKeys.HasKey(domain.Name, selector))
        {
            logger.LogWarning("No DKIM key for {Domain}; message is sent unsigned", fromAddress.Domain);
            return;
        }

        // Signing must happen on the final transfer encoding, otherwise a relay that re-encodes would break the signature.
        message.Prepare(EncodingConstraint.SevenBit);

        var signer = new DkimSigner(dkimKeys.GetKeyPath(domain.Name, selector), domain.Name, selector)
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        };
        signer.Sign(message, SignedHeaders.Where(h => h == HeaderId.From || message.Headers.Contains(h)).ToList());
    }
}
