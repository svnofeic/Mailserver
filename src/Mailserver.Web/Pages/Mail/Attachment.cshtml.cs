using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>
/// Downloads an attachment or the raw message. Only a genuine PDF may be shown by the browser (<paramref name="view"/>), in its own
/// PDF viewer; everything else is always a download, so an attached HTML or SVG file is never executed in the context of this site.
/// </summary>
public sealed class AttachmentModel(WebmailStore store) : MailPageModel
{
    public IActionResult OnGet(string folder, long uid, int index = 0, int source = 0, int view = 0)
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
        var content = MailAttachments.Content(attachment);
        if (view == 1 && MailAttachments.CanView(attachment) && MailAttachments.IsPdf(content))
        {
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(attachment.Name);
            Response.Headers.ContentDisposition = disposition.ToString();
            // The page-wide policy (default-src 'none') would stop the browser's PDF viewer; scripts and forms stay forbidden.
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; object-src 'self'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'self'";
            Response.Headers.XFrameOptions = "SAMEORIGIN";
            return File(content, "application/pdf");
        }

        return File(content, "application/octet-stream", attachment.Name);
    }
}
