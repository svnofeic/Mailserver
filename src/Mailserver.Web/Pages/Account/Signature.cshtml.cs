using Mailserver.Core.Accounts;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

/// <summary>One signature per sender address, added by the webmail when writing.</summary>
public sealed class SignatureModel(Signatures signatures, WebmailSender sender) : MailPageModel
{
    public IReadOnlyList<string> Senders { get; private set; } = [];
    public IReadOnlyDictionary<string, Signature> Current { get; private set; } = new Dictionary<string, Signature>();

    public void OnGet()
    {
        Senders = sender.SenderAddresses(CurrentAccount);
        Current = signatures.List(CurrentAccount.Id);
    }

    public IActionResult OnPost(string address, string? text, bool inReplies = false)
    {
        // Only addresses this mailbox may send from.
        var match = sender.SenderAddresses(CurrentAccount).FirstOrDefault(a => a.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            ErrorMessage = "Von dieser Adresse kann das Postfach nicht senden.";
            return RedirectToPage();
        }

        try
        {
            signatures.Set(CurrentAccount.Id, match, text, inReplies);
            Message = string.IsNullOrWhiteSpace(text) ? $"Signatur für {match} entfernt." : $"Signatur für {match} gespeichert.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }
}
