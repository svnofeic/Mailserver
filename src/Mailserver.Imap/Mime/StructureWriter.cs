using System.Text;
using Mailserver.Imap.Protocol;
using MimeKit;
using MimeKit.Utils;

namespace Mailserver.Imap.Mime;

/// <summary>ENVELOPE, BODY and BODYSTRUCTURE as defined in RFC 3501 section 7.4.2.</summary>
public static class StructureWriter
{
    public static void WriteEnvelope(ImapResponse response, MimeNode message)
    {
        var headers = message.Headers;
        var from = RawValue(headers, HeaderId.From);

        response.Raw("(");
        response.NString(RawValue(headers, HeaderId.Date)).Raw(" ");
        response.NString(RawValue(headers, HeaderId.Subject)).Raw(" ");
        WriteAddresses(response, from);
        response.Raw(" ");
        // Sender and Reply-To default to From (RFC 3501 7.4.2).
        WriteAddresses(response, RawValue(headers, HeaderId.Sender) ?? from);
        response.Raw(" ");
        WriteAddresses(response, RawValue(headers, HeaderId.ReplyTo) ?? from);
        response.Raw(" ");
        WriteAddresses(response, RawValue(headers, HeaderId.To));
        response.Raw(" ");
        WriteAddresses(response, RawValue(headers, HeaderId.Cc));
        response.Raw(" ");
        WriteAddresses(response, RawValue(headers, HeaderId.Bcc));
        response.Raw(" ");
        response.NString(RawValue(headers, HeaderId.InReplyTo)).Raw(" ");
        response.NString(RawValue(headers, HeaderId.MessageId));
        response.Raw(")");
    }

    public static void WriteBodyStructure(ImapResponse response, MimeNode node, bool extensible)
    {
        if (node.IsMultipart)
        {
            response.Raw("(");
            if (node.Children.Count == 0)
            {
                // An empty multipart cannot be expressed; present it as an empty text part like other servers do.
                response.Raw("(\"text\" \"plain\" (\"charset\" \"us-ascii\") NIL NIL \"7bit\" 0 0)");
            }

            foreach (var child in node.Children)
            {
                WriteBodyStructure(response, child, extensible);
            }

            response.Raw(" ").String(node.ContentType.MediaSubtype.ToLowerInvariant());
            if (extensible)
            {
                response.Raw(" ");
                WriteParameters(response, node.ContentType.Parameters);
                response.Raw(" ");
                WriteExtensions(response, node);
            }

            response.Raw(")");
            return;
        }

        var type = node.ContentType;
        response.Raw("(").String(type.MediaType.ToLowerInvariant()).Raw(" ").String(type.MediaSubtype.ToLowerInvariant()).Raw(" ");
        WriteParameters(response, type.Parameters);
        response.Raw(" ");
        response.NString(Unfold(node.Headers[HeaderId.ContentId])).Raw(" ");
        response.NString(Unfold(node.Headers[HeaderId.ContentDescription])).Raw(" ");
        response.String(node.ContentTransferEncoding).Raw(" ");
        response.Raw((node.End - node.BodyStart).ToString());

        if (node.IsMessage && node.Message is { } inner)
        {
            response.Raw(" ");
            WriteEnvelope(response, inner);
            response.Raw(" ");
            WriteBodyStructure(response, inner, extensible);
            response.Raw(" ").Raw(node.BodyLineCount.ToString());
        }
        else if (type.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            response.Raw(" ").Raw(node.BodyLineCount.ToString());
        }

        if (extensible)
        {
            response.Raw(" ").NString(Unfold(node.Headers[HeaderId.ContentMd5])).Raw(" ");
            WriteExtensions(response, node);
        }

        response.Raw(")");
    }

    private static void WriteExtensions(ImapResponse response, MimeNode node)
    {
        // body-fld-dsp body-fld-lang body-fld-loc
        if (node.Headers[HeaderId.ContentDisposition] is { } value && ContentDisposition.TryParse(value, out var disposition))
        {
            response.Raw("(").String(disposition.Disposition.ToLowerInvariant()).Raw(" ");
            WriteParameters(response, disposition.Parameters);
            response.Raw(")");
        }
        else
        {
            response.Raw("NIL");
        }

        response.Raw(" ");
        var languages = Unfold(node.Headers[HeaderId.ContentLanguage])?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (languages is { Length: > 0 })
        {
            response.Raw("(");
            for (var i = 0; i < languages.Length; i++)
            {
                response.Raw(i > 0 ? " " : "").String(languages[i]);
            }

            response.Raw(")");
        }
        else
        {
            response.Raw("NIL");
        }

        response.Raw(" ").NString(Unfold(node.Headers[HeaderId.ContentLocation]));
    }

    private static void WriteParameters(ImapResponse response, IEnumerable<Parameter> parameters)
    {
        var list = parameters.ToList();
        if (list.Count == 0)
        {
            response.Raw("NIL");
            return;
        }

        response.Raw("(");
        for (var i = 0; i < list.Count; i++)
        {
            response.Raw(i > 0 ? " " : "").String(list[i].Name.ToLowerInvariant()).Raw(" ").String(EncodeIfNeeded(list[i].Value));
        }

        response.Raw(")");
    }

    private static void WriteAddresses(ImapResponse response, string? rawValue)
    {
        if (rawValue is null || !InternetAddressList.TryParse(rawValue, out var addresses) || addresses.Count == 0)
        {
            response.Raw("NIL");
            return;
        }

        response.Raw("(");
        foreach (var address in addresses)
        {
            WriteAddress(response, address);
        }

        response.Raw(")");
    }

    private static void WriteAddress(ImapResponse response, InternetAddress address)
    {
        switch (address)
        {
            case MailboxAddress mailbox:
            {
                var at = mailbox.Address.LastIndexOf('@');
                var local = at < 0 ? mailbox.Address : mailbox.Address[..at];
                var domain = at < 0 ? null : mailbox.Address[(at + 1)..];
                response.Raw("(").NString(string.IsNullOrEmpty(mailbox.Name) ? null : EncodeIfNeeded(mailbox.Name)).Raw(" NIL ")
                    .String(local).Raw(" ").NString(domain).Raw(")");
                break;
            }
            case GroupAddress group:
                // Group start (NIL NIL "name" NIL), members, group end (NIL NIL NIL NIL).
                response.Raw("(NIL NIL ").String(EncodeIfNeeded(group.Name ?? "")).Raw(" NIL)");
                foreach (var member in group.Members)
                {
                    WriteAddress(response, member);
                }

                response.Raw("(NIL NIL NIL NIL)");
                break;
        }
    }

    /// <summary>Raw header value, unfolded; still RFC 2047-encoded as on the wire, which is what clients expect in ENVELOPE.</summary>
    private static string? RawValue(HeaderList headers, HeaderId id)
    {
        var index = headers.IndexOf(id);
        if (index < 0)
        {
            return null;
        }

        var raw = headers[index].RawValue;
        return Unfold(Encoding.UTF8.GetString(raw));
    }

    private static string? Unfold(string? value) =>
        value is null ? null : value.Replace("\r\n", "").Replace("\n", "").Trim();

    private static string EncodeIfNeeded(string value) =>
        value.All(c => c < 0x80) ? value : Encoding.ASCII.GetString(Rfc2047.EncodeText(Encoding.UTF8, value));
}
