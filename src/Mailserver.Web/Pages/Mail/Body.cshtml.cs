using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>
/// The message body as a standalone document, shown in a sandboxed iframe. Its own Content-Security-Policy forbids scripts,
/// forms, fonts and frames; remote images only load when the user asked for them.
/// </summary>
public sealed class BodyModel(WebmailStore store) : MailPageModel
{
    public IActionResult OnGet(string folder, long uid, int images = 0)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is not { } stored)
        {
            return NotFound();
        }

        var rendered = MailRenderer.Render(store.Load(stored));
        var headers = Response.Headers;
        headers["Content-Security-Policy"] =
            $"default-src 'none'; img-src data:{(images == 1 ? " https: http:" : "")}; style-src 'unsafe-inline'; " +
            "frame-ancestors 'self'; form-action 'none'; sandbox allow-popups allow-popups-to-escape-sandbox";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        return Content(rendered.Html, "text/html; charset=utf-8");
    }
}
