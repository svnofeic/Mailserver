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
    public bool KeepAttachments { get; set; } = true;
}

public sealed class ComposeModel(
    WebmailStore store,
    WebmailSender sender,
    MailboxStore mailboxes,
    IOptions<MailserverOptions> options,
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

    public IActionResult OnGet(string? mode, string? folder, long? uid, string? to)
    {
        Form = new ComposeForm { Mode = mode, OriginalFolder = folder, OriginalUid = uid, From = CurrentAddress.ToString(), To = to };
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
            Form.InReplyTo, Form.References?.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
                SelectSenderMatching(original);
                break;
            case "draft":
                Form.From = original.From.Mailboxes.FirstOrDefault()?.Address ?? Form.From;
                Form.To = original.To.ToString();
                Form.Cc = original.Cc.ToString();
                Form.Bcc = original.Bcc.ToString();
                Form.Subject = subject;
                Form.Body = original.TextBody ?? MailRenderer.PlainText(original);
                Form.InReplyTo = original.InReplyTo;
                Form.References = string.Join(' ', original.References);
                break;
        }
    }

    /// <summary>When replying to mail sent to one of the user's aliases, answer from that alias.</summary>
    private void SelectSenderMatching(MimeMessage original)
    {
        var senders = sender.SenderAddresses(CurrentAccount);
        var match = original.To.Mailboxes.Concat(original.Cc.Mailboxes)
            .Select(m => m.Address)
            .FirstOrDefault(a => senders.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (match is not null)
        {
            Form.From = senders.First(s => s.Equals(match, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Prepare()
    {
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
