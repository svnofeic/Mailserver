using System.Globalization;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.Log;

public sealed class IndexModel(SpamLog log) : MailPageModel
{
    public static readonly (string Value, string Label)[] Views =
    [
        ("", "alles"), ("inbound", "eingehende Mails"), ("spam", "Spam-Prüfung"), ("rejected", "Ablehnungen"),
        ("outbound", "Versand"), ("feedback", "Benutzer-Feedback"), ("auth", "Anmeldungen"),
    ];

    public static readonly string[] Actions =
    [
        SpamLogAction.Accepted, SpamLogAction.Spam, SpamLogAction.Rejected, SpamLogAction.Deferred, SpamLogAction.Delivered,
        SpamLogAction.Discarded, SpamLogAction.Forwarded, SpamLogAction.AutoReplied, SpamLogAction.NotForwarded, SpamLogAction.MarkedSpam, SpamLogAction.MarkedHam, SpamLogAction.Sent,
        SpamLogAction.Failed, SpamLogAction.LoginFailed, SpamLogAction.LockedOut,
    ];

    public string Since { get; private set; } = "24h";
    public string View { get; private set; } = "";
    public string? ActionFilter { get; private set; }
    public string? Search { get; private set; }
    public string? Ip { get; private set; }
    public string? MinScore { get; private set; }
    public int Limit { get; private set; } = 250;
    public IReadOnlyList<SpamLogEntry> Entries { get; private set; } = [];
    public string ExportUrl => "/Admin/Log" + Microsoft.AspNetCore.Http.QueryString.Create(new Dictionary<string, string?>
    {
        ["handler"] = "Export", ["since"] = Since, ["view"] = View, ["action"] = ActionFilter, ["search"] = Search, ["ip"] = Ip, ["minScore"] = MinScore,
    }).ToUriComponent();

    public void OnGet(string? since, string? view, string? action, string? search, string? ip, string? minScore, int limit = 250)
    {
        Entries = Load(since, view, action, search, ip, minScore, Math.Clamp(limit, 1, 5000));
    }

    public IActionResult OnGetExport(string? since, string? view, string? action, string? search, string? ip, string? minScore)
    {
        var entries = Load(since, view, action, search, ip, minScore, 100_000);
        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(SpamLogReport.ToCsv(entries))).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"mailserver-verlauf-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    private IReadOnlyList<SpamLogEntry> Load(string? since, string? view, string? action, string? search, string? ip, string? minScore, int limit)
    {
        Since = since is "1h" or "24h" or "7d" or "30d" or "90d" ? since : "24h";
        View = Views.Any(v => v.Value == view) ? view! : "";
        ActionFilter = Actions.Contains(action) ? action : null;
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Ip = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        MinScore = minScore;
        Limit = limit;
        var score = double.TryParse(minScore?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null;
        var from = DateTimeOffset.UtcNow - (Since[^1] == 'h' ? TimeSpan.FromHours(int.Parse(Since[..^1])) : TimeSpan.FromDays(int.Parse(Since[..^1])));

        var stages = View switch
        {
            "inbound" => new[] { SpamLogStage.Data, SpamLogStage.Delivery },
            "spam" => [SpamLogStage.Connect, SpamLogStage.Sender, SpamLogStage.Recipient, SpamLogStage.Data],
            "rejected" => [SpamLogStage.Connect, SpamLogStage.Sender, SpamLogStage.Data],
            "outbound" => [SpamLogStage.Submission, SpamLogStage.Outbound],
            "feedback" => [SpamLogStage.Feedback],
            "auth" => [SpamLogStage.Auth],
            _ => null,
        };
        var effectiveAction = View == "rejected" ? SpamLogAction.Rejected : ActionFilter;

        var entries = log.Query(new SpamLogQuery(Since: from, Action: effectiveAction, ClientIp: Ip, Search: Search, MinScore: score,
            Limit: stages is null ? limit : limit * 4));
        return (stages is null ? entries : entries.Where(e => stages.Contains(e.Stage))).Take(limit).ToList();
    }
}
