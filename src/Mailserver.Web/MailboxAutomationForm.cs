using Mailserver.Core.Accounts;

namespace Mailserver.Web;

/// <summary>Forwarding and out-of-office reply as edited in the web interface (own mailbox or, for admins, any mailbox).</summary>
public sealed class MailboxAutomationForm
{
    public string? ForwardTo { get; set; }
    public bool KeepCopy { get; set; } = true;

    public bool AutoReplyEnabled { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? Until { get; set; }
    public int IntervalDays { get; set; } = 7;

    public static MailboxAutomationForm Create(MailboxSettings settings) => new()
    {
        ForwardTo = string.Join("\n", settings.Forwarding.Targets),
        KeepCopy = settings.Forwarding.KeepCopy,
        AutoReplyEnabled = settings.AutoReply.Enabled,
        Subject = settings.AutoReply.Subject,
        Body = settings.AutoReply.Body,
        From = settings.AutoReply.From,
        Until = settings.AutoReply.Until,
        IntervalDays = settings.AutoReply.IntervalDays,
    };

    /// <summary>Validates and saves both parts; throws <see cref="ArgumentException"/> with a German message and saves nothing then.</summary>
    public void Save(MailboxSettingsStore store, Account account)
    {
        var forwarding = new Forwarding(MailboxSettingsStore.ParseTargets(ForwardTo), KeepCopy);
        var reply = new AutoReply(AutoReplyEnabled, Subject?.Trim() ?? "", Body ?? "", From, Until, IntervalDays);

        // Both parts are checked before anything is written, so a mistake never leaves a half-saved form.
        store.ValidateForwarding(account, forwarding);
        store.ValidateAutoReply(reply);
        store.SetForwarding(account, forwarding);
        store.SetAutoReply(account.Id, reply);
    }

    /// <summary>Short status for overviews, e.g. "Weiterleitung an a@b.de · Abwesenheitsnotiz bis 12.10.2026".</summary>
    public static string? Describe(MailboxSettings settings, DateOnly today)
    {
        var parts = new List<string>();
        if (settings.Forwarding.IsActive)
        {
            parts.Add($"Weiterleitung an {string.Join(", ", settings.Forwarding.Targets)}{(settings.Forwarding.KeepCopy ? "" : " (ohne Kopie)")}");
        }

        var reply = settings.AutoReply;
        if (reply.Enabled)
        {
            parts.Add(reply.IsActiveOn(today)
                ? $"Abwesenheitsnotiz aktiv{(reply.Until is { } until ? $" bis {until:dd.MM.yyyy}" : "")}"
                : $"Abwesenheitsnotiz ab {reply.From:dd.MM.yyyy}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
