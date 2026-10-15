using System.Text;
using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Contacts;

/// <summary>The address book: saved contacts and everyone mail was sent to; also the source of the recipient suggestions.</summary>
public sealed class IndexModel(Mailserver.Core.Accounts.Contacts contacts, Mailserver.Core.Dav.ContactCards cards) : MailPageModel
{
    private const long MaxImportBytes = 5 * 1024 * 1024;

    public IReadOnlyList<Contact> Items { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool SavedOnly { get; set; }
    public Contact? Editing { get; private set; }

    public void OnGet(long? edit)
    {
        contacts.EnsureSeeded(CurrentAccount.Id);
        cards.EnsureCards(CurrentAccount.Id);
        Items = contacts.List(CurrentAccount.Id, Q).Where(c => !SavedOnly || c.Saved).ToList();
        Editing = edit is { } id ? contacts.Get(CurrentAccount.Id, id) : null;
    }

    /// <summary>Suggestions for the recipient fields while typing (JSON).</summary>
    public IActionResult OnGetSuggest(string? q) =>
        new JsonResult(contacts.Suggest(CurrentAccount.Id, q ?? "").Select(c => new { name = c.Name, address = c.Address, display = c.Display }));

    public IActionResult OnPostSave(string address, string? name, long? id)
    {
        try
        {
            var saved = cards.Save(CurrentAccount.Id, address, name, id);
            Message = $"{saved.Display} gespeichert.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (cards.Delete(CurrentAccount.Id, id))
        {
            Message = "Kontakt gelöscht. (Schreiben Sie ihm wieder, wird die Adresse erneut vorgeschlagen.)";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0 || file.Length > MaxImportBytes)
        {
            ErrorMessage = "Bitte eine vCard-Datei (.vcf, höchstens 5 MB) auswählen.";
            return RedirectToPage();
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        var addresses = VCards.Read(text).Count;
        var imported = cards.Import(CurrentAccount.Id, text);
        if (imported == 0)
        {
            ErrorMessage = "In der Datei wurden keine Kontakte gefunden.";
        }
        else
        {
            // Full contacts (with phone numbers etc.) for the phone; the e-mail addresses for the suggestions.
            Message = $"{imported} Kontakt(e) übernommen, {addresses} Adresse(n) für die Vorschläge.";
        }

        return RedirectToPage();
    }

    public IActionResult OnGetExport() =>
        File(Encoding.UTF8.GetBytes(cards.Export(CurrentAccount.Id)), "text/vcard; charset=utf-8", $"Adressbuch {CurrentAddress}.vcf");
}
