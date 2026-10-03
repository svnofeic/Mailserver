using Mailserver.Core.Accounts;
using Mailserver.Core.Rules;

namespace Mailserver.Web.Pages.Admin.Rules;

public sealed class IndexModel(RuleStore rules, AccountStore accounts) : MailPageModel
{
    public string? Scope { get; private set; }
    public IReadOnlyList<MailRule> Rules { get; private set; } = [];
    public List<(string Value, string Label)> Scopes { get; } = [];

    public void OnGet(string? scope)
    {
        Scope = string.IsNullOrEmpty(scope) ? null : scope;
        Rules = rules.List(Scope);
        Scopes.Add((RuleStore.GlobalScope, "alle Postfächer"));
        Scopes.AddRange(accounts.ListDomains().Select(d => (d.Name, $"Domain {d.Name}")));
        Scopes.AddRange(accounts.ListAccounts().Select(a => (a.Address.ToString(), a.Address.ToString())));
    }
}
