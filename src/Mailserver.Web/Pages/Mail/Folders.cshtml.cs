using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

public sealed class FoldersModel(WebmailStore store, FolderManager folders) : MailPageModel
{
    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];

    public void OnGet() => Folders = store.Folders(CurrentAccount.Id);

    public IActionResult OnPostCreate(string? name, string? parent) => Done(folders.Create(CurrentAccount, parent, name));

    public IActionResult OnPostRename(string? name, string? newName) => Done(folders.Rename(CurrentAccount, name, newName));

    public IActionResult OnPostDelete(string? name) => Done(folders.Delete(CurrentAccount, name));

    private IActionResult Done(MailActionResult result)
    {
        if (result.Error is not null)
        {
            ErrorMessage = result.Error;
        }
        else
        {
            Message = result.Message;
        }

        return RedirectToPage();
    }
}
