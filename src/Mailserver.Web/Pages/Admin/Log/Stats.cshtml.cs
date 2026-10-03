using Mailserver.Core;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Log;

public sealed class StatsModel(SpamLog log, IOptions<MailserverOptions> options) : MailPageModel
{
    public int Days { get; private set; }
    public double Threshold => options.Value.Spam.JunkThreshold;
    public SpamLogStatistics Stats { get; private set; } = null!;
    public IReadOnlyList<SpamLogEntry> Feedback { get; private set; } = [];

    public void OnGet(int days = 7)
    {
        Days = Math.Clamp(days, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-Days);
        Stats = SpamLogReport.Build(log.Query(new SpamLogQuery(Since: since)), since, Threshold);
        Feedback = Stats.FalsePositives.Concat(Stats.FalseNegatives).OrderByDescending(e => e.Time).ToList();
    }
}
