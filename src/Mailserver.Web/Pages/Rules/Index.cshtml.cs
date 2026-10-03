using Mailserver.Core.Rules;

namespace Mailserver.Web.Pages.Rules;

public sealed class IndexModel(RuleStore rules) : MailPageModel
{
    public IReadOnlyList<MailRule> Own { get; private set; } = [];
    public IReadOnlyList<MailRule> Inherited { get; private set; } = [];

    public void OnGet()
    {
        Own = rules.List(CurrentAddress.ToString());
        Inherited = rules.List(RuleStore.GlobalScope).Concat(rules.List(CurrentAddress.Domain)).ToList();
    }
}
