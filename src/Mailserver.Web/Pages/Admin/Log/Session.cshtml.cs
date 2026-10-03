using Mailserver.Core.SpamLogging;

namespace Mailserver.Web.Pages.Admin.Log;

public sealed class SessionModel(SpamLog log) : MailPageModel
{
    public IReadOnlyList<SpamLogEntry> Entries { get; private set; } = [];

    public void OnGet(string id)
    {
        var bySession = log.Query(new SpamLogQuery(Session: id));
        // Feedback entries have no session; they are found through the Message-ID of the received mail.
        var messageIds = bySession.Select(e => e.MessageId).OfType<string>().Distinct().ToList();
        var related = messageIds.SelectMany(m => log.Query(new SpamLogQuery(Search: m))).Where(e => e.Session != id);
        Entries = bySession.Concat(related).DistinctBy(e => e.Id).OrderBy(e => e.Time).ThenBy(e => e.Id).ToList();
    }

    public IEnumerable<(string Label, string Value)> Fields(SpamLogEntry e) => new (string, string?)[]
    {
        ("IP", e.ClientIp), ("Reverse DNS", e.ReverseDns), ("HELO", e.Helo), ("MAIL FROM", e.MailFrom), ("Empfänger", e.Recipient),
        ("From", e.HeaderFrom), ("Betreff", e.Subject), ("Message-ID", e.MessageId), ("Score", Format.Score(e.Score)),
        ("Tests", e.Tests?.Replace(",", ", ")), ("SPF", e.Spf), ("DKIM", e.Dkim), ("DMARC", e.Dmarc), ("Ordner", e.Folder is null ? null : Format.FolderName(e.Folder)),
        ("Regeln", e.Rules), ("Hinweis", e.Detail),
    }.Where(f => !string.IsNullOrEmpty(f.Item2)).Select(f => (f.Item1, f.Item2!));
}
