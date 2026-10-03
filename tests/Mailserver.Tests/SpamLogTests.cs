using Mailserver.Core;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.Options;

namespace Mailserver.Tests;

public class SpamLogTests : TestData
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
    private readonly MailserverOptions _options = new();

    private SpamLog Log => new(Database, Options.Create(_options), _time);

    private static SpamLogEntry Data(string action, double score, string tests, string? messageId = null, string from = "x@spam.test") =>
        new() { Stage = SpamLogStage.Data, Action = action, Score = score, Tests = tests, MessageId = messageId, HeaderFrom = from, Subject = "S" };

    [Fact]
    public void Writes_queries_and_filters()
    {
        var log = Log;
        log.Write(Data(SpamLogAction.Spam, 7.5, "SPF_FAIL=3.5,NO_REVERSE_DNS=1.5") with { ClientIp = "198.51.100.1", Session = "s1" });
        log.Write(Data(SpamLogAction.Accepted, 0.3, "DKIM_NONE=0.3") with { Session = "s2" });
        log.Write(new SpamLogEntry { Stage = SpamLogStage.Delivery, Action = SpamLogAction.Delivered, Session = "s1", Folder = "Junk" });

        Assert.Equal(3, log.Query(new SpamLogQuery()).Count);
        Assert.Single(log.Query(new SpamLogQuery(Action: SpamLogAction.Spam)));
        Assert.Single(log.Query(new SpamLogQuery(MinScore: 5)));
        Assert.Equal(2, log.Query(new SpamLogQuery(Session: "s1")).Count);
        Assert.Single(log.Query(new SpamLogQuery(ClientIp: "198.51.100.1")));
        Assert.Equal(2, log.Query(new SpamLogQuery(Search: "spam.test")).Count);
    }

    [Fact]
    public void Respects_retention_subject_setting_and_switch()
    {
        _options.Spam.Log.RetentionDays = 30;
        _options.Spam.Log.IncludeSubject = false;
        var log = Log;
        log.Write(Data(SpamLogAction.Accepted, 0, "") with { Time = _time.Now.AddDays(-40) });
        log.Write(Data(SpamLogAction.Accepted, 0, ""));

        // Writing triggers the hourly cleanup, so the 40-day-old entry is already gone.
        Assert.Null(Assert.Single(log.Query(new SpamLogQuery())).Subject);
        _time.Now = _time.Now.AddDays(31);
        Assert.Equal(1, log.Cleanup());

        _options.Spam.Log.Enabled = false;
        log.Write(Data(SpamLogAction.Spam, 9, ""));
        Assert.Empty(log.Query(new SpamLogQuery()));
    }

    [Fact]
    public void Statistics_identify_false_positives_negatives_and_hint()
    {
        var entries = new List<SpamLogEntry>
        {
            Data(SpamLogAction.Accepted, 0.3, "DKIM_NONE=0.3"),
            Data(SpamLogAction.Accepted, 1.8, "SPF_SOFTFAIL=1.5,DKIM_NONE=0.3"),
            Data(SpamLogAction.Spam, 5.5, "SPF_SOFTFAIL=1.5,NO_REVERSE_DNS=1.5,DMARC_FAIL=1.5,HELO_NOT_FQDN=1"),
            Data(SpamLogAction.Spam, 13, "DNSBL_BL.SPAMCOP.NET=3,SPF_FAIL=3.5,FROM_LOCAL_SPOOF=5,NO_REVERSE_DNS=1.5"),
            new() { Stage = SpamLogStage.Data, Action = SpamLogAction.Rejected, Detail = "DMARC p=reject" },
            new() { Stage = SpamLogStage.Connect, Action = SpamLogAction.Rejected, ClientIp = "203.0.113.9" },
            new() { Stage = SpamLogStage.Delivery, Action = SpamLogAction.Delivered, Folder = "INBOX", Rules = "Newsletter-Regel" },
            new() { Stage = SpamLogStage.Delivery, Action = SpamLogAction.Delivered, Folder = "Junk" },
            new() { Stage = SpamLogStage.Delivery, Action = SpamLogAction.Discarded, Rules = "Gewinnspiel" },
            // User pulled the 5.5 message out of Junk (false positive) and moved a 3.8 message into Junk (missed).
            new() { Stage = SpamLogStage.Feedback, Action = SpamLogAction.MarkedHam, Score = 5.5, Tests = "SPF_SOFTFAIL,NO_REVERSE_DNS,DMARC_FAIL,HELO_NOT_FQDN" },
            new() { Stage = SpamLogStage.Feedback, Action = SpamLogAction.MarkedSpam, Score = 3.8, Tests = "SPF_SOFTFAIL,NO_REVERSE_DNS" },
            new() { Stage = SpamLogStage.Feedback, Action = SpamLogAction.MarkedHam, Score = 0.3, Tests = "DKIM_NONE" },
        };

        var stats = SpamLogReport.Build(entries, DateTimeOffset.UtcNow.AddDays(-7), junkThreshold: 5);

        Assert.Equal(4, stats.Messages);
        Assert.Equal(2, stats.Spam);
        Assert.Equal(1, stats.RejectedConnections);
        Assert.Equal(1, stats.RejectedMessages);
        Assert.Equal((1, 1, 1), (stats.DeliveredInbox, stats.DeliveredJunk, stats.Discarded));
        Assert.Single(stats.FalsePositives);    // the 0.3 one was not the filter's decision
        Assert.Single(stats.FalseNegatives);
        Assert.Equal([("< 1", 1), ("1–3", 1), ("3–5", 0), ("5–8", 1), ("8–12", 0), ("≥ 12", 1)], stats.ScoreBuckets);
        var softfail = stats.Tests.Single(t => t.Name == "SPF_SOFTFAIL");
        Assert.Equal((1, 1, 1, 1), (softfail.InHam, softfail.InSpam, softfail.InFalsePositives, softfail.InFalseNegatives));
        Assert.Equal([("Newsletter-Regel", 1), ("Gewinnspiel", 1)], stats.Rules);
        Assert.Contains(stats.Hints, h => h.Contains("knapp über der Schwelle"));
        Assert.Contains(stats.Hints, h => h.Contains("knapp unter der Schwelle"));
        Assert.Equal([("spam.test", 2)], stats.TopSpamSenders);
    }

    [Fact]
    public void Csv_quotes_separators_and_neutralises_formulas()
    {
        var csv = SpamLogReport.ToCsv([Data(SpamLogAction.Spam, 7.25, "A=1") with { Subject = "=HYPERLINK(\"x\");boom" }]);
        var lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.StartsWith("time_utc;session;stage;action", lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\");boom\"", lines[1]);
        Assert.Contains(";7.3;", lines[1]);
    }
}
