using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailserver.Core.Storage;
using Microsoft.AspNetCore.Http;
using MimeKit;

namespace Mailserver.Web.Webmail;

/// <summary>
/// Embedded images (cid:) of a message, loaded by the message view as separate requests instead of being copied into the HTML
/// as data: URLs – a newsletter that shows the same 1 MB picture twenty times would otherwise become 27 MB of HTML. The view
/// runs in a sandboxed iframe without cookies, so each link carries its own signature: valid for this one image for 12 hours.
/// </summary>
public sealed class InlineImages(MailboxStore mailboxes, TimeProvider timeProvider)
{
    public const string Path = "/Mail/Inline";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
    };

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Content-ID → link for every embedded image of the message.</summary>
    public IReadOnlyDictionary<string, string> Links(StoredMessage stored, MimeMessage message)
    {
        var expires = timeProvider.GetUtcNow().AddHours(12).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.ContentId is { Length: > 0 } id && Types.Contains(part.ContentType.MimeType))
            {
                var cid = id.Trim('<', '>');
                result.TryAdd(cid, $"{Path}?m={stored.Id}&c={Uri.EscapeDataString(cid)}&e={expires}&s={Sign(stored.Id, cid, expires)}");
            }
        }

        return result;
    }

    /// <summary>Serves one image if the link is genuine and not expired.</summary>
    public async Task<IResult> ServeAsync(HttpContext context, long m, string? c, string? e, string? s)
    {
        if (c is null || e is null || s is null || !long.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            expires < timeProvider.GetUtcNow().ToUnixTimeSeconds() ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(m, c, e)), Encoding.ASCII.GetBytes(s)) ||
            mailboxes.GetMessageById(m) is not { } stored)
        {
            return Results.NotFound();
        }

        MimeMessage message;
        await using (var stream = mailboxes.OpenMessage(stored))
        {
            message = await MimeMessage.LoadAsync(stream, context.RequestAborted);
        }

        var part = message.BodyParts.OfType<MimePart>().FirstOrDefault(p =>
            p.ContentId is { } id && string.Equals(id.Trim('<', '>'), c, StringComparison.OrdinalIgnoreCase) && Types.Contains(p.ContentType.MimeType));
        if (part?.Content is null)
        {
            return Results.NotFound();
        }

        var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, context.RequestAborted);
        buffer.Position = 0;
        var headers = context.Response.Headers;
        headers.CacheControl = "private, max-age=43200";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.Stream(buffer, part.ContentType.MimeType.ToLowerInvariant());
    }

    private string Sign(long messageId, string cid, string expires) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{messageId}\n{cid}\n{expires}")));
}
