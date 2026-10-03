using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using MimeKit;

namespace Mailserver.Web.Webmail;

public sealed record RenderedBody(string Html, bool HasRemoteContent);

/// <summary>
/// Turns a message body into a self-contained, sanitized HTML document for the webmail viewer. Scripts, event handlers, forms and
/// frames are removed; embedded images (cid:) are inlined as data: URLs; remote images stay blocked by the page's Content-Security-Policy
/// until the user allows them.
/// </summary>
public static partial class MailRenderer
{
    private const long MaxInlineImageBytes = 3 * 1024 * 1024;
    private const long MaxInlineImagesTotal = 15 * 1024 * 1024;

    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
    };

    public static RenderedBody Render(MimeMessage message)
    {
        string body;
        if (message.HtmlBody is { } html)
        {
            body = Sanitize(html, InlineImages(message));
        }
        else
        {
            body = $"<pre class=\"text\">{Linkify(WebUtility.HtmlEncode(message.TextBody ?? ""))}</pre>";
        }

        var document = new StringBuilder()
            .Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><base target=\"_blank\">")
            .Append("<style>html{background:#fff;color:#1d2330}body{margin:12px;font:15px/1.5 system-ui,-apple-system,\"Segoe UI\",Roboto,sans-serif;overflow-wrap:anywhere}")
            .Append("img{max-width:100%;height:auto}pre.text{white-space:pre-wrap;font:inherit;margin:0}table{max-width:100%}</style></head><body>")
            .Append(body)
            .Append("</body></html>")
            .ToString();
        return new RenderedBody(document, RemoteContent().IsMatch(body));
    }

    /// <summary>Plain text of the body for quoting in replies and forwards.</summary>
    public static string PlainText(MimeMessage message)
    {
        if (message.TextBody is { } text)
        {
            return text;
        }

        var html = message.HtmlBody ?? "";
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>|</li>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(2));
        return Regex.Replace(WebUtility.HtmlDecode(html), @"\n{3,}", "\n\n", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim();
    }

    public static string Sanitize(string html, IReadOnlyDictionary<string, string> inlineImages)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedTags.Remove("form");
        sanitizer.AllowedTags.Remove("input");
        sanitizer.AllowedTags.Remove("button");
        sanitizer.AllowedTags.Remove("textarea");
        sanitizer.AllowedTags.Remove("select");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Remove("id");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = inlineImages.TryGetValue(e.OriginalUrl[4..].Trim('<', '>'), out var dataUrl) ? dataUrl : "";
            }
        };
        // data: URLs produced above are trusted; the sanitizer itself must not accept data: from the mail.
        sanitizer.AllowDataAttributes = false;
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;
            }
        };

        return sanitizer.Sanitize(html);
    }

    /// <summary>Content-ID → data: URL for embedded images, within size limits.</summary>
    private static Dictionary<string, string> InlineImages(MimeMessage message)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.ContentId is not { Length: > 0 } id || !InlineImageTypes.Contains(part.ContentType.MimeType) || part.Content is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            if (buffer.Length > MaxInlineImageBytes || total + buffer.Length > MaxInlineImagesTotal)
            {
                continue;
            }

            total += buffer.Length;
            result[id.Trim('<', '>')] = $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)}";
        }

        return result;
    }

    private static string Linkify(string encodedText) =>
        UrlPattern().Replace(encodedText, m => $"<a href=\"{m.Value}\" rel=\"noopener noreferrer\">{m.Value}</a>");

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:src|background|poster)\s*=\s*[""']?\s*(?:https?:)?//|url\(\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteContent();
}
