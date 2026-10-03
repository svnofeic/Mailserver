using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>Downloads an attachment or the raw message. Always as download, never rendered by the browser.</summary>
public sealed class AttachmentModel(WebmailStore store) : MailPageModel
{
    public IActionResult OnGet(string folder, long uid, int index = 0, int source = 0)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is not { } stored)
        {
            return NotFound();
        }

        var message = store.Load(stored);
        if (source == 1)
        {
            var name = MailAttachments.SafeName(string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject + ".eml", "nachricht.eml");
            return PhysicalFile(store.Path(stored), "application/octet-stream", name);
        }

        var attachments = MailAttachments.List(message);
        if (index < 0 || index >= attachments.Count)
        {
            return NotFound();
        }

        var attachment = attachments[index];
        // application/octet-stream: an attached HTML or SVG file must not be executed in the context of this site.
        return File(MailAttachments.Content(attachment), "application/octet-stream", attachment.Name);
    }
}
