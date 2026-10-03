using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Rules;
using Mailserver.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Rules;

/// <summary>
/// Creates and edits rules. Users may only touch rules of their own mailbox; admins may edit any scope.
/// </summary>
public sealed class EditModel(RuleStore rules, AccountStore accounts, MailboxStore mailboxes) : MailPageModel
{
    public long? Id { get; private set; }

    [BindProperty]
    public RuleForm Form { get; set; } = new();

    public List<(string Value, string Label)> Scopes { get; } = [];
    public List<string> Folders { get; } = [];
    public string BackUrl => IsAdmin && Form.Scope != CurrentAddress.ToString() ? "/Admin/Rules" : "/Rules";

    public IActionResult OnGet(long? id, string? scope)
    {
        if (id is { } ruleId)
        {
            if (Load(ruleId) is not { } rule)
            {
                return NotFound();
            }

            Id = ruleId;
            Form = RuleForm.FromRule(rule);
        }
        else
        {
            Form = new RuleForm { Scope = IsAdmin && scope is not null ? scope : CurrentAddress.ToString() }.Padded();
        }

        Prepare();
        return Page();
    }

    public IActionResult OnPost(long? id)
    {
        if (id is { } ruleId && Load(ruleId) is not { } existing)
        {
            return NotFound();
        }

        var scope = IsAdmin ? Form.Scope : CurrentAddress.ToString();
        try
        {
            var conditions = Form.ToConditions();
            var argument = Form.Action == RuleAction.Move ? Form.Folder?.Trim() : null;
            var name = Form.ResolveName(conditions);
            if (id is { } updateId)
            {
                rules.Update(updateId, name, conditions, Form.Action, argument, Form.MatchAll, Form.Stop, Form.Priority, Form.Enabled);
            }
            else
            {
                CheckScope(scope);
                var rule = rules.Add(scope, name, conditions, Form.Action, argument, Form.MatchAll, Form.Stop, Form.Priority);
                if (!Form.Enabled)
                {
                    rules.SetEnabled(rule.Id, false);
                }
            }
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            Id = id;
            Form.Padded();
            Prepare();
            return Page();
        }

        Message = "Regel gespeichert.";
        return Redirect(BackUrl);
    }

    public IActionResult OnPostToggle(long id)
    {
        if (Load(id) is not { } rule)
        {
            return NotFound();
        }

        rules.SetEnabled(id, !rule.Enabled);
        Message = rule.Enabled ? "Regel deaktiviert." : "Regel aktiviert.";
        Form.Scope = rule.Scope;
        return Redirect(BackUrl);
    }

    public IActionResult OnPostDelete(long id)
    {
        if (Load(id) is not { } rule)
        {
            return NotFound();
        }

        rules.Remove(id);
        Message = "Regel gelöscht.";
        Form.Scope = rule.Scope;
        return Redirect(BackUrl);
    }

    /// <summary>The rule, if it exists and the current user may change it.</summary>
    private MailRule? Load(long id)
    {
        var rule = rules.Get(id);
        return rule is not null && (IsAdmin || string.Equals(rule.Scope, CurrentAddress.ToString(), StringComparison.OrdinalIgnoreCase))
            ? rule
            : null;
    }

    private void CheckScope(string scope)
    {
        var valid = scope == RuleStore.GlobalScope ||
                    (scope.Contains('@') ? EmailAddress.TryParse(scope, out var address) && accounts.FindAccount(address) is not null
                                         : accounts.IsLocalDomain(scope));
        if (!valid)
        {
            throw new ArgumentException("Ungültiger Geltungsbereich.");
        }
    }

    private void Prepare()
    {
        if (IsAdmin)
        {
            Scopes.Add((RuleStore.GlobalScope, "alle Postfächer"));
            Scopes.AddRange(accounts.ListDomains().Select(d => (d.Name, $"Domain {d.Name}")));
            Scopes.AddRange(accounts.ListAccounts().Select(a => (a.Address.ToString(), a.Address.ToString())));
        }

        var folderOwner = EmailAddress.TryParse(Form.Scope, out var owner) ? accounts.FindAccount(owner) : CurrentAccount;
        if (folderOwner is not null)
        {
            Folders.AddRange(mailboxes.ListFolders(folderOwner.Id).Select(f => f.Name));
        }
    }
}
