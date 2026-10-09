using System.Text;
using Mailserver.Core;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Web.Pages.Mail;

public sealed class ComposeForm
{
    /// <summary>"", "reply", "replyall", "forward" or "draft".</summary>
    public string? Mode { get; set; }
    public string? OriginalFolder { get; set; }
    public long? OriginalUid { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public string From { get; set; } = "";
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }

    /// <summary>Filled by the editor script; empty when JavaScript is off (then the plain text field is used).</summary>
    public string? BodyHtml { get; set; }
    public bool KeepAttachments { get; set; } = true;
}

// The token is checked in the handlers: a mail written for minutes must not end in an empty error response when the token
// no longer fits (e.g. the app was in the background, or the server restarted); the form comes back to be sent again.
[IgnoreAntiforgeryToken]
public sealed class ComposeModel(
    Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery,
    WebmailStore store,
    WebmailSender sender,
    MailboxStore mailboxes,
    IOptions<MailserverOptions> options,
    Mailserver.Core.External.ExternalAccountStore external,
    ILogger<ComposeModel> logger) : MailPageModel
{
    [BindProperty]
    public ComposeForm Form { get; set; } = new();

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    public IReadOnlyList<string> Senders { get; private set; } = [];
    public List<string> CarriedAttachments { get; } = [];
    public long MaxAttachmentBytes => options.Value.MaxMessageSizeBytes * 3 / 4;

    public string Title => Form.Mode switch
    {
        "reply" or "replyall" => "Antworten",
        "forward" => "Weiterleiten",
        "draft" => "Entwurf bearbeiten",
        _ => "Neue Mail",
    };

    /// <summary>
    /// Fills the form from a mailto: link (RFC 6068), e.g. when the installed web app is the mail program of the device:
    /// mailto:anna@example.com,bob@example.com?cc=…&amp;subject=…&amp;body=…
    /// </summary>
    private void ApplyMailto(string link)
    {
        var text = link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? link[7..] : link;
        var question = text.IndexOf('?');
        var addresses = Uri.UnescapeDataString(question < 0 ? text : text[..question]);
        var fields = (question < 0 ? "" : text[(question + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(pair => pair[0].ToLowerInvariant(), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "")
            .ToDictionary(g => g.Key, g => string.Join(", ", g));
        if (fields.TryGetValue("to", out var more))
        {
            addresses = string.Join(", ", new[] { addresses, more }.Where(a => a.Length > 0));
        }

        static string List(string value) => string.Join(", ", value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Form.To = addresses.Length > 0 ? List(addresses) : Form.To;
        Form.Cc = fields.TryGetValue("cc", out var cc) ? List(cc) : null;
        Form.Bcc = fields.TryGetValue("bcc", out var bcc) ? List(bcc) : null;
        Form.Subject = fields.GetValueOrDefault("subject");
        Form.Body = fields.GetValueOrDefault("body");
    }

    public IActionResult OnGet(string? mode, string? folder, long? uid, string? to, string? mailto)
    {
        Form = new ComposeForm { Mode = mode, OriginalFolder = folder, OriginalUid = uid, From = CurrentAddress.ToString(), To = to };
        if (mailto is not null)
        {
            ApplyMailto(mailto);
        }

        if (mode is "reply" or "replyall" or "forward" or "draft")
        {
            if (Original() is not { } original)
            {
                return NotFound();
            }

            Prefill(mode, original);
        }

        Prepare();
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        if (await RefusedAsync() is { } refused)
        {
            return refused;
        }

        try
        {
            await sender.SendAsync(CurrentAccount, await BuildDraftAsync(cancellationToken), HttpContext.Connection.RemoteIpAddress, cancellationToken);
        }
        catch (ComposeException ex)
        {
            ErrorMessage = ex.Message;
            Prepare();
            return Page();
        }

        if (Form.Mode is "reply" or "replyall" && OriginalLocation() is var (folder, uid))
        {
            mailboxes.UpdateFlags(folder.Id, [uid], FlagOperation.Add, [MessageFlags.Answered]);
        }

        RemoveEditedDraft();
        logger.LogInformation("Webmail message sent by {User}", CurrentAddress);
        Message = "Nachricht gesendet.";
        return Redirect("/Mail");
    }

    public async Task<IActionResult> OnPostDraftAsync(CancellationToken cancellationToken)
    {
        if (await RefusedAsync() is { } refused)
        {
            return refused;
        }

        try
        {
            var saved = await sender.SaveDraftAsync(CurrentAccount, await BuildDraftAsync(cancellationToken), cancellationToken);
            RemoveEditedDraft();
            Message = "Entwurf gespeichert.";
            return Redirect($"/Mail/Compose?mode=draft&folder=Drafts&uid={saved.Uid}");
        }
        catch (ComposeException ex)
        {
            ErrorMessage = ex.Message;
            Prepare();
            return Page();
        }
    }

    /// <summary>The form again with a notice if the antiforgery token is missing or expired; null if it is valid.</summary>
    private async Task<IActionResult?> RefusedAsync()
    {
        if (await antiforgery.IsRequestValidAsync(HttpContext))
        {
            return null;
        }

        logger.LogWarning("Webmail form of {User} had no valid antiforgery token; shown again", CurrentAddress);
        ErrorMessage = "Nicht gesendet: Die Seite war zu lange geöffnet. Bitte noch einmal auf „Senden“ tippen" +
                       (Files.Any(f => f.Length > 0) ? " und die Anhänge erneut auswählen." : ".");
        Prepare();
        return Page();
    }

    private async Task<Draft> BuildDraftAsync(CancellationToken cancellationToken)
    {
        var attachments = new List<OutgoingAttachment>();
        if (Form.KeepAttachments && Form.Mode is "forward" or "draft" && Original() is { } original)
        {
            foreach (var attachment in MailAttachments.List(original))
            {
                attachments.Add(new OutgoingAttachment(attachment.Name, attachment.ContentType, MailAttachments.Content(attachment)));
            }
        }

        foreach (var file in Files.Where(f => f.Length > 0))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) || !ContentType.TryParse(file.ContentType, out _)
                ? "application/octet-stream"
                : file.ContentType;
            attachments.Add(new OutgoingAttachment(MailAttachments.SafeName(file.FileName, "anhang"), contentType, buffer.ToArray()));
        }

        return new Draft(Form.From, Form.To ?? "", Form.Cc, Form.Bcc, Form.Subject ?? "", Form.Body ?? "", attachments,
            Form.InReplyTo, Form.References?.Split(' ', StringSplitOptions.RemoveEmptyEntries), Form.BodyHtml);
    }

    private void Prefill(string mode, MimeMessage original)
    {
        var subject = original.Subject ?? "";
        var quoted = MailRenderer.PlainText(original);
        var date = Format.Time(original.Date == default ? DateTimeOffset.Now : original.Date);
        switch (mode)
        {
            case "reply":
            case "replyall":
            {
                var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo : original.From;
                Form.To = string.Join(", ", replyTo.Mailboxes.Select(m => m.ToString()));
                if (mode == "replyall")
                {
                    var own = CurrentAddress.ToString();
                    var others = original.To.Mailboxes.Concat(original.Cc.Mailboxes)
                        .Where(m => !m.Address.Equals(own, StringComparison.OrdinalIgnoreCase) && !replyTo.Mailboxes.Any(r => r.Address == m.Address))
                        .Select(m => m.ToString());
                    Form.Cc = string.Join(", ", others);
                }

                Form.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) || subject.StartsWith("AW:", StringComparison.OrdinalIgnoreCase)
                    ? subject
                    : "Re: " + subject;
                Form.Body = $"\n\nAm {date} schrieb {original.From}:\n" + string.Join('\n', quoted.Split('\n').Select(l => "> " + l.TrimEnd('\r')));
                Form.InReplyTo = original.MessageId;
                Form.References = string.Join(' ', original.References.Append(original.MessageId ?? "").Where(r => r.Length > 0));
                Form.BodyHtml = $"<p><br></p><p>Am {Encode(date)} schrieb {Encode(original.From.ToString())}:</p>{Quote(original)}";
                SelectSenderMatching(original);
                break;
            }
            case "forward":
                Form.Subject = subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) || subject.StartsWith("WG:", StringComparison.OrdinalIgnoreCase)
                    ? subject
                    : "WG: " + subject;
                Form.Body = new StringBuilder("\n\n---------- Weitergeleitete Nachricht ----------\n")
                    .Append($"Von: {original.From}\nDatum: {date}\nBetreff: {subject}\nAn: {original.To}\n")
                    .Append(original.Cc.Count > 0 ? $"Cc: {original.Cc}\n" : "")
                    .Append('\n').Append(quoted).ToString();
                Form.BodyHtml = "<p><br></p><p>---------- Weitergeleitete Nachricht ----------<br>" +
                                $"Von: {Encode(original.From.ToString())}<br>Datum: {Encode(date)}<br>Betreff: {Encode(subject)}<br>An: {Encode(original.To.ToString())}" +
                                (original.Cc.Count > 0 ? $"<br>Cc: {Encode(original.Cc.ToString())}" : "") + "</p>" + Quote(original);
                SelectSenderMatching(original);
                break;
            case "draft":
                Form.From = original.From.Mailboxes.FirstOrDefault()?.Address ?? Form.From;
                Form.To = original.To.ToString();
                Form.Cc = original.Cc.ToString();
                Form.Bcc = original.Bcc.ToString();
                Form.Subject = subject;
                Form.Body = original.TextBody ?? MailRenderer.PlainText(original);
                Form.BodyHtml = original.HtmlBody is { } draftHtml ? MailRenderer.SanitizeComposed(BodyContent(draftHtml)) : null;
                Form.InReplyTo = original.InReplyTo;
                Form.References = string.Join(' ', original.References);
                break;
        }
    }

    private static string Encode(string text) => System.Net.WebUtility.HtmlEncode(text);

    /// <summary>The original message as a quoted block: its formatted HTML if it has one, otherwise its text.</summary>
    private static string Quote(MimeMessage original)
    {
        var content = original.HtmlBody is { } html
            ? MailRenderer.SanitizeComposed(BodyContent(html))
            : Encode(original.TextBody ?? "").Replace("\r\n", "\n").Replace("\n", "<br>");
        return $"<blockquote style=\"margin:0 0 0 .8ex;border-left:2px solid #ccc;padding-left:1ex\">{content}</blockquote>";
    }

    /// <summary>The part inside &lt;body&gt;, so a quoted document does not bring its own head and styles.</summary>
    private static string BodyContent(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, @"<body[^>]*>(.*)</body>", System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups[1].Value : html;
    }

    /// <summary>
    /// When replying to mail sent to one of the user's aliases, answer from that alias; mail fetched from another provider
    /// is answered from that address (also when it arrived as Bcc or through a mailing list).
    /// </summary>
    private void SelectSenderMatching(MimeMessage original)
    {
        var senders = sender.SenderAddresses(CurrentAccount);
        var match = original.To.Mailboxes.Concat(original.Cc.Mailboxes)
            .Select(m => m.Address)
            .FirstOrDefault(a => senders.Contains(a, StringComparer.OrdinalIgnoreCase))
            ?? external.List(CurrentAccount.Id)
                .FirstOrDefault(a => a.CanSend && !a.Settings.Folder.Equals(MailboxStore.Inbox, StringComparison.OrdinalIgnoreCase) &&
                                     a.Settings.Folder.Equals(Form.OriginalFolder, StringComparison.OrdinalIgnoreCase))?.Address;
        if (match is not null)
        {
            Form.From = senders.First(s => s.Equals(match, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Prepare()
    {
        // Posted editor content is shown again after an error; it goes back into the page only after sanitizing.
        Form.BodyHtml = string.IsNullOrWhiteSpace(Form.BodyHtml) ? null : MailRenderer.SanitizeComposed(Form.BodyHtml);
        Senders = sender.SenderAddresses(CurrentAccount);
        if (Form.Mode is "forward" or "draft" && Original() is { } original)
        {
            CarriedAttachments.AddRange(MailAttachments.List(original).Select(a => a.Name));
        }
    }

    private (Folder Folder, long Uid)? OriginalLocation() =>
        Form.OriginalUid is { } uid && store.Folder(CurrentAccount.Id, Form.OriginalFolder) is { } folder ? (folder, uid) : null;

    private MimeMessage? Original() =>
        OriginalLocation() is var (folder, uid) && store.Message(folder, uid) is { } stored ? store.Load(stored) : null;

    /// <summary>A draft that was edited is replaced by the sent message or the newly saved draft.</summary>
    private void RemoveEditedDraft()
    {
        if (Form.Mode == "draft" && OriginalLocation() is var (folder, uid) && folder.Name == "Drafts")
        {
            mailboxes.UpdateFlags(folder.Id, [uid], FlagOperation.Add, [MessageFlags.Deleted]);
            mailboxes.Expunge(folder.Id, [uid]);
        }
    }
}
