using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using MimeKit;

namespace Mailserver.Web.Webmail;

public sealed record RenderedBody(string Html, bool HasRemoteContent);

/// <summary>
/// Turns a message body into a self-contained, sanitized HTML document for the webmail viewer. Scripts, event handlers, forms and
/// frames are removed; embedded images (cid:) are linked or inlined; remote images stay blocked by the page's Content-Security-Policy
/// until the user allows them.
/// </summary>
public static partial class MailRenderer
{
    private const long MaxInlineImageBytes = 3 * 1024 * 1024;
    private const long MaxInlineImagesTotal = 15 * 1024 * 1024;

    /// <param name="imageLinks">Content-ID → URL of the embedded images (see <see cref="Webmail.InlineImages"/>); without it they are
    /// copied in as data: URLs.</param>
    public static RenderedBody Render(MimeMessage message, IReadOnlyDictionary<string, string>? imageLinks = null)
    {
        string body;
        if (message.HtmlBody is { } html)
        {
            body = Sanitize(html, imageLinks ?? InlineImages(message));
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

    /// <summary>Whether the body loads images or other content from the internet – checked on the raw HTML, without rendering it.</summary>
    public static bool HasRemoteContent(MimeMessage message) => message.HtmlBody is { } html && RemoteContent().IsMatch(html);

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

    /// <summary>Images that may be embedded in a written mail (as data: URL in the editor, as cid: part when sent).</summary>
    public static readonly IReadOnlySet<string> EmbeddableImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    private static readonly Regex DataImage = new(@"^data:(image/[a-z+.-]+);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// HTML written in the webmail editor (or quoted from another mail) before it is sent: formatting only, no scripts, styles
    /// blocks or forms. Pictures in the text are allowed as data: URLs of common image types, only in &lt;img&gt;.
    /// </summary>
    public static string SanitizeComposed(string html)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("data");
        foreach (var tag in new[] { "form", "input", "button", "textarea", "select", "option", "style" })
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        sanitizer.AllowedAttributes.Remove("id");
        // Only the webmail's own marker for the signature (so it can be swapped when another sender is chosen).
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement element && element.HasAttribute("class") && element.GetAttribute("class") != "signature")
            {
                element.RemoveAttribute("class");
            }
        };
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;
            }
            else if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var match = DataImage.Match(e.OriginalUrl);
                var isImage = e.Tag?.TagName.Equals("img", StringComparison.OrdinalIgnoreCase) == true;
                e.SanitizedUrl = isImage && match.Success && EmbeddableImageTypes.Contains(match.Groups[1].Value) ? e.OriginalUrl : null;
            }
        };
        return sanitizer.Sanitize(html);
    }

    /// <summary>
    /// Replaces cid: references of <paramref name="html"/> with data: URLs of the message's embedded pictures, so they stay
    /// visible when a draft is opened again or a mail is quoted. Pictures beyond <paramref name="maxBytes"/> in total are left out.
    /// </summary>
    public static string EmbedPictures(MimeMessage message, string html, long maxBytes = 8 * 1024 * 1024)
    {
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId) && EmbeddableImageTypes.Contains(p.ContentType.MimeType) && p.Content is not null)
            .GroupBy(p => p.ContentId!.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (parts.Count == 0)
        {
            return html;
        }

        var used = 0L;
        var urls = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return Regex.Replace(html, @"cid:([^""'\s>)]+)", match =>
        {
            var id = Uri.UnescapeDataString(match.Groups[1].Value).Trim('<', '>');
            if (!urls.TryGetValue(id, out var url))
            {
                url = null;
                if (parts.TryGetValue(id, out var part))
                {
                    using var decoded = new MemoryStream();
                    part.Content!.DecodeTo(decoded);
                    if (used + decoded.Length <= maxBytes)
                    {
                        used += decoded.Length;
                        url = $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(decoded.ToArray())}";
                    }
                }

                urls[id] = url;
            }

            return url ?? match.Value;
        }, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// For sending: the pictures written into the text (data: URLs) become parts of the message, referenced as cid: – the way
    /// mail programs show them, and much smaller than base64 in the HTML.
    /// </summary>
    public static string ExtractPictures(string html, BodyBuilder builder, string hostname)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        var number = 0;
        return Regex.Replace(html, @"(<img\b[^>]*?\ssrc=)(""|')(data:(image/[a-z+.-]+);base64,([A-Za-z0-9+/=\s]+))\2", match =>
        {
            var type = match.Groups[4].Value.ToLowerInvariant();
            if (!EmbeddableImageTypes.Contains(type))
            {
                return match.Value;
            }

            var data = match.Groups[5].Value;
            if (!known.TryGetValue(data, out var id))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    return match.Value;
                }

                number++;
                var extension = type.Split('/')[1].Replace("jpeg", "jpg");
                var part = builder.LinkedResources.Add($"bild{number}.{extension}", bytes, ContentType.Parse(type));
                id = MimeKit.Utils.MimeUtils.GenerateMessageId(hostname);
                part.ContentId = id;
                known[data] = id;
            }

            return $"{match.Groups[1].Value}{match.Groups[2].Value}cid:{id}{match.Groups[2].Value}";
        }, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
    }

    /// <summary>Plain text for an HTML fragment (used for the text part of HTML mails).</summary>
    public static string HtmlToText(string html) => PlainText(new MimeMessage { Body = new TextPart("html") { Text = html } });

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
            if (part.ContentId is not { Length: > 0 } id || !Webmail.InlineImages.Types.Contains(part.ContentType.MimeType) || part.Content is null)
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
