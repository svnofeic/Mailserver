using Mailserver.Core;
using Mailserver.Core.Rules;
using MimeKit;

namespace Mailserver.Tests;

public class RuleTests : TestData
{
    private static readonly EmailAddress Max = EmailAddress.Parse("max@example.test");
    private readonly RuleStore _rules;

    public RuleTests()
    {
        Accounts.AddDomain("example.test");
        Accounts.AddAccount(Max, "password-max-123");
        _rules = new RuleStore(Database);
    }

    private static RuleSubject Mail(string subject, string from = "shop@sender.test", string body = "Hallo", double score = 0)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = body } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("max@example.test"));
        return new RuleSubject(message, score);
    }

    private DeliveryDecision Decide(RuleSubject mail, bool isSpam = false) => RuleEngine.Decide(_rules.GetRulesFor(Max), mail, isSpam);

    private void Add(string scope, params string[] args)
    {
        var d = RuleParser.Parse(args);
        _rules.Add(scope, d.Name, d.Conditions, d.Action, d.Argument, d.MatchAll, d.Stop, d.Priority);
    }

    [Fact]
    public void Subject_word_group_moves_to_junk_case_insensitively_across_whitespace()
    {
        Add("max@example.test", "--if", "betreff", "enthält", "Sie haben gewonnen", "--then", "spam");

        Assert.Equal("Junk", Decide(Mail("Glückwunsch, SIE HABEN\r\n  GEWONNEN!")).Folder);
        Assert.Equal("INBOX", Decide(Mail("Sie haben eine Rechnung")).Folder);
    }

    [Fact]
    public void Delete_discards_the_message()
    {
        Add("*", "--if", "betreff", "enthält", "Viagra", "--then", "löschen");

        var decision = Decide(Mail("Billig Viagra kaufen"));
        Assert.True(decision.Discard);
        Assert.Null(decision.Folder);
    }

    [Fact]
    public void Move_with_any_condition_and_header_field()
    {
        Add("example.test", "--if", "von", "endet", "@newsletter.test", "--if", "header:List-Id", "enthält", "angebote", "--any",
            "--then", "verschieben", "Newsletter");

        Assert.Equal("Newsletter", Decide(Mail("Woche 12", from: "info@newsletter.test")).Folder);
        Assert.Equal("INBOX", Decide(Mail("Woche 12")).Folder);
    }

    [Fact]
    public void All_conditions_must_match_by_default()
    {
        Add("max@example.test", "--if", "von", "ist", "boss@firma.test", "--if", "text", "enthält", "dringend", "--then", "markieren");

        Assert.Equal(["\\Flagged"], Decide(Mail("x", from: "boss@firma.test", body: "Bitte DRINGEND erledigen")).Flags);
        Assert.Empty(Decide(Mail("x", from: "boss@firma.test", body: "Kein Stress")).Flags);
    }

    [Fact]
    public void Allow_rule_overrides_spam_verdict_and_global_runs_first()
    {
        Add("max@example.test", "--if", "von", "endet", "@kunde.test", "--then", "kein-spam");

        var decision = Decide(Mail("Angebot", from: "chef@kunde.test"), isSpam: true);
        Assert.Equal("INBOX", decision.Folder);
        Assert.True(decision.NotSpam);
        Assert.Equal("Junk", Decide(Mail("Angebot"), isSpam: true).Folder);

        // A global delete rule wins because it is evaluated before the mailbox's own rules and stops.
        Add("*", "--if", "betreff", "beginnt", "[SPAM]", "--then", "löschen");
        Assert.True(Decide(Mail("[SPAM] Angebot", from: "chef@kunde.test")).Discard);
    }

    [Fact]
    public void Continue_combines_actions_and_priority_orders_rules()
    {
        Add("max@example.test", "--if", "betreff", "enthält", "Rechnung", "--then", "verschieben", "Rechnungen", "--priority", "20");
        Add("max@example.test", "--if", "betreff", "enthält", "Rechnung", "--then", "gelesen", "--continue", "--priority", "10");

        var decision = Decide(Mail("Ihre Rechnung 2026-01"));
        Assert.Equal("Rechnungen", decision.Folder);
        Assert.Equal(["\\Seen"], decision.Flags);
    }

    [Fact]
    public void Score_and_regex_conditions()
    {
        Add("*", "--if", "score", "über", "12", "--then", "löschen");
        Add("*", "--if", "betreff", "regex", @"^\[ticket #\d+\]", "--then", "verschieben", "Support");

        Assert.True(Decide(Mail("x", score: 15)).Discard);
        Assert.False(Decide(Mail("x", score: 8)).Discard);
        Assert.Equal("Support", Decide(Mail("[Ticket #4711] Drucker")).Folder);
    }

    [Fact]
    public void Disabled_rules_and_other_mailboxes_are_ignored()
    {
        Add("other@example.test", "--if", "betreff", "enthält", "x", "--then", "löschen");
        Add("max@example.test", "--if", "betreff", "enthält", "x", "--then", "spam");
        _rules.SetEnabled(_rules.List("max@example.test")[0].Id, false);

        Assert.Equal("INBOX", Decide(Mail("x")).Folder);
    }

    [Theory]
    [InlineData("--then", "spam")]
    [InlineData("--if", "betreff", "enthält", "x")]
    [InlineData("--if", "farbe", "enthält", "x", "--then", "spam")]
    [InlineData("--if", "betreff", "ähnelt", "x", "--then", "spam")]
    [InlineData("--if", "betreff", "enthält", "x", "--then", "verschieben")]
    [InlineData("--if", "score", "über", "viel", "--then", "löschen")]
    public void Rejects_invalid_definitions(params string[] args) => Assert.Throws<ArgumentException>(() => RuleParser.Parse(args));

    [Fact]
    public void Invalid_regex_is_rejected_when_saving()
    {
        var d = RuleParser.Parse(["--if", "betreff", "regex", "([a-z", "--then", "spam"]);
        Assert.ThrowsAny<ArgumentException>(() => _rules.Add("*", d.Name, d.Conditions, d.Action));
    }

    [Fact]
    public void Generates_readable_german_names()
    {
        var d = RuleParser.Parse(["--if", "betreff", "enthält", "Gewinnspiel", "--then", "löschen"]);
        Assert.Equal("Betreff enthält \"Gewinnspiel\" → endgültig löschen", d.Name);
    }
}
