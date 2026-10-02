using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace Mailserver.Core.Routing;

/// <summary>
/// Builds delivery status notifications (RFC 3464) for messages that could not be delivered.
/// </summary>
public static class BounceFactory
{
    public static async Task<byte[]> CreateAsync(string hostname, EmailAddress originalSender, Stream originalMessage,
        IReadOnlyList<(EmailAddress Recipient, string Error)> failures, CancellationToken cancellationToken)
    {
        var headers = await HeaderList.LoadAsync(originalMessage, cancellationToken);

        var text = new StringBuilder()
            .AppendLine("Ihre Nachricht konnte nicht zugestellt werden. / Your message could not be delivered.")
            .AppendLine();
        foreach (var (recipient, error) in failures)
        {
            text.AppendLine($"  {recipient}: {error}");
        }

        var status = new StringBuilder()
            .Append($"Reporting-MTA: dns; {hostname}\r\n")
            .Append($"Arrival-Date: {DateUtils.FormatDate(DateTimeOffset.Now)}\r\n");
        foreach (var (recipient, error) in failures)
        {
            status.Append("\r\n")
                .Append($"Final-Recipient: rfc822; {recipient}\r\n")
                .Append("Action: failed\r\n")
                .Append($"Status: {ExtractEnhancedStatus(error)}\r\n")
                .Append($"Diagnostic-Code: smtp; {error.ReplaceLineEndings(" ")}\r\n");
        }

        var headerPart = new MimePart("text", "rfc822-headers") { Content = new MimeContent(Serialize(headers)) };
        var report = new MultipartReport("delivery-status")
        {
            new TextPart("plain") { Text = text.ToString() },
            new MimePart("message", "delivery-status") { Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(status.ToString()))) },
            headerPart,
        };

        var bounce = new MimeMessage
        {
            Subject = "Unzustellbar: " + (headers[HeaderId.Subject] ?? "(kein Betreff)"),
            Date = DateTimeOffset.Now,
            MessageId = MimeUtils.GenerateMessageId(hostname),
            Body = report,
        };
        bounce.From.Add(new MailboxAddress("Mail Delivery System", $"MAILER-DAEMON@{hostname}"));
        bounce.To.Add(MailboxAddress.Parse(originalSender.ToString()));
        bounce.Headers.Add("Auto-Submitted", "auto-replied");

        using var output = new MemoryStream();
        await bounce.WriteToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static MemoryStream Serialize(HeaderList headers)
    {
        var stream = new MemoryStream();
        headers.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static string ExtractEnhancedStatus(string error)
    {
        var match = System.Text.RegularExpressions.Regex.Match(error, @"\b([245]\.\d{1,3}\.\d{1,3})\b");
        return match.Success ? match.Groups[1].Value : "5.0.0";
    }
}
