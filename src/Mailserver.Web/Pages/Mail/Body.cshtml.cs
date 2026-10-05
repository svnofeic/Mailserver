using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>
/// The message body as a standalone document, shown in a sandboxed iframe. Its own Content-Security-Policy forbids scripts,
/// forms, fonts and frames; remote images only load when the user asked for them.
/// </summary>
public sealed class BodyModel(WebmailStore store, InlineImages images) : MailPageModel
{
    public IActionResult OnGet(string folder, long uid, [FromQuery(Name = "images")] int showImages = 0)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is not { } stored)
        {
            return NotFound();
        }

        var message = store.Load(stored);
        var rendered = MailRenderer.Render(message, images.Links(stored, message));
        var headers = Response.Headers;
        // Embedded images come from this server (signed links); the sandboxed document has no origin of its own, so it is named.
        headers["Content-Security-Policy"] =
            $"default-src 'none'; img-src data: {Request.Scheme}://{Request.Host}{InlineImages.Path}{(showImages == 1 ? " https: http:" : "")}; style-src 'unsafe-inline'; " +
            "frame-ancestors 'self'; form-action 'none'; sandbox allow-popups allow-popups-to-escape-sandbox";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        return Content(rendered.Html, "text/html; charset=utf-8");
    }
}
