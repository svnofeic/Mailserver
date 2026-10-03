using System.Globalization;
using System.Text;

namespace Mailserver.Core.SpamLogging;

public sealed record TestStatistic(string Name, int InHam, int InSpam, int InFalsePositives, int InFalseNegatives, double Weight);

public sealed record SpamLogStatistics(
    DateTimeOffset Since,
    int Messages,
    int Accepted,
    int Spam,
    int RejectedConnections,
    int RejectedSenders,
    int RejectedMessages,
    int GreylistDeferrals,
    int DeliveredInbox,
    int DeliveredJunk,
    int DeliveredOther,
    int Discarded,
    int NotForwarded,
    IReadOnlyList<(string Bucket, int Count)> ScoreBuckets,
    IReadOnlyList<TestStatistic> Tests,
    IReadOnlyList<(string Rule, int Count)> Rules,
    IReadOnlyList<(string Domain, int Count)> TopSpamSenders,
    IReadOnlyList<(string Ip, int Count)> TopRejectedIps,
    IReadOnlyList<SpamLogEntry> FalsePositives,
    IReadOnlyList<SpamLogEntry> FalseNegatives,
    IReadOnlyList<string> Hints);

/// <summary>Aggregates spam log entries into figures that show where the filter can be tuned.</summary>
public static class SpamLogReport
{
    private static readonly (string Label, double Low, double High)[] Buckets =
    [
        ("< 1", double.MinValue, 1), ("1–3", 1, 3), ("3–5", 3, 5), ("5–8", 5, 8), ("8–12", 8, 12), ("≥ 12", 12, double.MaxValue),
    ];

    public static SpamLogStatistics Build(IReadOnlyList<SpamLogEntry> entries, DateTimeOffset since, double junkThreshold)
    {
        var data = entries.Where(e => e.Stage == SpamLogStage.Data && e.Action != SpamLogAction.Rejected).ToList();
        var delivery = entries.Where(e => e.Stage == SpamLogStage.Delivery).ToList();
        var feedback = entries.Where(e => e.Stage == SpamLogStage.Feedback).ToList();

        // Messages moved out of Junk although the filter scored them as spam are false positives; moved into Junk although the score
        // was below the threshold are false negatives. Moves that only undo a rule decision are not the filter's fault.
        var falsePositives = feedback.Where(f => f.Action == SpamLogAction.MarkedHam && f.Score >= junkThreshold).ToList();
        var falseNegatives = feedback.Where(f => f.Action == SpamLogAction.MarkedSpam && (f.Score is null || f.Score < junkThreshold)).ToList();

        var testNames = data.SelectMany(e => ParseTests(e.Tests)).Select(t => t.Name)
            .Concat(feedback.SelectMany(f => ParseTestNames(f.Tests)))
            .Distinct().ToList();
        var tests = testNames.Select(name => new TestStatistic(
                name,
                data.Count(e => e.Action == SpamLogAction.Accepted && HasTest(e.Tests, name)),
                data.Count(e => e.Action == SpamLogAction.Spam && HasTest(e.Tests, name)),
                falsePositives.Count(f => HasTest(f.Tests, name)),
                falseNegatives.Count(f => HasTest(f.Tests, name)),
                data.SelectMany(e => ParseTests(e.Tests)).Where(t => t.Name == name).Select(t => t.Score).DefaultIfEmpty(0).Max()))
            .OrderByDescending(t => t.InHam + t.InSpam)
            .ToList();

        var hints = new List<string>();
        var nearMissesFp = falsePositives.Count(f => f.Score < junkThreshold + 2);
        if (nearMissesFp > 0)
        {
            hints.Add($"{nearMissesFp} Fehlalarm(e) knapp über der Schwelle ({junkThreshold:0.#}–{junkThreshold + 2:0.#} Punkte): " +
                      "die Spam-Schwelle (Einstellungen bzw. Spam:JunkThreshold) etwas anheben oder für bekannte Absender eine Regel \"nie als Spam\" anlegen.");
        }

        var nearMissesFn = falseNegatives.Count(f => f.Score >= junkThreshold - 2);
        if (nearMissesFn > 0)
        {
            hints.Add($"{nearMissesFn} übersehene Spam-Mail(s) knapp unter der Schwelle ({junkThreshold - 2:0.#}–{junkThreshold:0.#} Punkte): " +
                      "die Spam-Schwelle (Einstellungen bzw. Spam:JunkThreshold) etwas senken.");
        }

        foreach (var test in tests.Where(t => t.InFalsePositives >= 2 && t.InFalsePositives * 2 >= falsePositives.Count))
        {
            hints.Add($"Test {test.Name} ist an {test.InFalsePositives} von {falsePositives.Count} Fehlalarmen beteiligt – Gewichtung prüfen.");
        }

        if (falseNegatives.Count >= 3 && falseNegatives.Count(f => ParseTestNames(f.Tests).Count == 0 || f.Score < 1) * 2 >= falseNegatives.Count)
        {
            hints.Add("Ein Großteil des übersehenen Spams besteht alle technischen Prüfungen – hier helfen vor allem Inhalts-Regeln (Betreff/Text).");
        }

        var greylisted = entries.Where(e => e.Stage == SpamLogStage.Recipient && e.Action == SpamLogAction.Deferred).ToList();
        var greylistedSessions = greylisted.Select(e => (e.ClientIp, e.MailFrom, e.Recipient)).Distinct().Count();
        var retried = data.Count(d => greylisted.Any(g => g.MailFrom == d.MailFrom && d.Recipient?.Contains(g.Recipient ?? "\0") == true));
        if (greylistedSessions >= 20)
        {
            hints.Add($"Greylisting: {greylistedSessions} Absender zurückgestellt, {retried} davon kamen wieder ({Percent(retried, greylistedSessions)}). " +
                      "Der Rest war mit hoher Wahrscheinlichkeit Spam.");
        }

        return new SpamLogStatistics(
            since,
            data.Count,
            data.Count(e => e.Action == SpamLogAction.Accepted),
            data.Count(e => e.Action == SpamLogAction.Spam),
            entries.Count(e => e.Stage == SpamLogStage.Connect && e.Action == SpamLogAction.Rejected),
            entries.Count(e => e.Stage == SpamLogStage.Sender && e.Action == SpamLogAction.Rejected),
            entries.Count(e => e.Stage == SpamLogStage.Data && e.Action == SpamLogAction.Rejected),
            greylisted.Count,
            delivery.Count(e => e.Action == SpamLogAction.Delivered && e.Folder == "INBOX"),
            delivery.Count(e => e.Action == SpamLogAction.Delivered && e.Folder == "Junk"),
            delivery.Count(e => e.Action == SpamLogAction.Delivered && e.Folder is not ("INBOX" or "Junk")),
            delivery.Count(e => e.Action == SpamLogAction.Discarded),
            delivery.Count(e => e.Action == SpamLogAction.NotForwarded),
            Buckets.Select(b => (b.Label, data.Count(e => e.Score >= b.Low && e.Score < b.High))).ToList(),
            tests,
            delivery.Where(e => e.Rules is not null).SelectMany(e => e.Rules!.Split(" | "))
                .GroupBy(r => r).Select(g => (g.Key, g.Count())).OrderByDescending(r => r.Item2).ToList(),
            data.Where(e => e.Action == SpamLogAction.Spam && e.HeaderFrom is not null)
                .GroupBy(e => e.HeaderFrom![(e.HeaderFrom!.LastIndexOf('@') + 1)..].ToLowerInvariant())
                .Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(10).ToList(),
            entries.Where(e => e.Action == SpamLogAction.Rejected && e.ClientIp is not null)
                .GroupBy(e => e.ClientIp!).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(10).ToList(),
            falsePositives,
            falseNegatives,
            hints);
    }

    public static IEnumerable<(string Name, double Score)> ParseTests(string? tests) =>
        (tests ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t =>
        {
            var eq = t.IndexOf('=');
            return eq < 0
                ? (t, 0d)
                : (t[..eq], double.TryParse(t[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0d);
        });

    private static List<string> ParseTestNames(string? tests) =>
        ParseTests(tests).Select(t => t.Name).Where(n => n != "NONE").ToList();

    private static bool HasTest(string? tests, string name) => ParseTestNames(tests).Contains(name);

    private static string Percent(int part, int total) => total == 0 ? "–" : $"{100.0 * part / total:0}%";

    /// <summary>CSV with semicolons (opens directly in Excel) and invariant decimals (for scripts).</summary>
    public static string ToCsv(IEnumerable<SpamLogEntry> entries)
    {
        var csv = new StringBuilder();
        csv.AppendLine("time_utc;session;stage;action;client_ip;reverse_dns;helo;mail_from;recipient;header_from;subject;message_id;score;tests;spf;dkim;dmarc;folder;rules;detail");
        foreach (var e in entries.OrderBy(e => e.Time))
        {
            csv.AppendLine(string.Join(';', new[]
            {
                e.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), e.Session, e.Stage, e.Action, e.ClientIp,
                e.ReverseDns, e.Helo, e.MailFrom, e.Recipient, e.HeaderFrom, e.Subject, e.MessageId,
                e.Score?.ToString("0.0", CultureInfo.InvariantCulture), e.Tests, e.Spf, e.Dkim, e.Dmarc, e.Folder, e.Rules, e.Detail,
            }.Select(Quote)));
        }

        return csv.ToString();
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        value = value.Replace("\r", " ").Replace("\n", " ");
        // Leading =, +, -, @ would be evaluated as formulas by Excel (CSV injection via crafted subjects).
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([';', '"']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
