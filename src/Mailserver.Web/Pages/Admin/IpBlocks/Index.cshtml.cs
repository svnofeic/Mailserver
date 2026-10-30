using System.Net;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.IpBlocks;

public sealed record FailingAddress(string Ip, int Failures, IReadOnlyList<string> Users, DateTimeOffset Last, IpRule? Rule, bool LockedOut);

/// <summary>Current lockouts after failed logins, the addresses trying most, and permanent block/allow rules.</summary>
public sealed class IndexModel(IpRules rules, AuthThrottle throttle, SpamLog log) : MailPageModel
{
    public IReadOnlyList<Lockout> Lockouts { get; private set; } = [];
    public IReadOnlyList<FailingAddress> Failing { get; private set; } = [];
    public const int RulesPerPage = 50;

    /// <summary>The rules on the shown page (newest first), filtered by <see cref="Filter"/>.</summary>
    public IReadOnlyList<IpRule> Rules { get; private set; } = [];
    public int RulesTotal { get; private set; }
    public int RulesCount { get; private set; }
    public int RulePage { get; private set; } = 1;
    public int RulePages => Math.Max(1, (RulesTotal + RulesPerPage - 1) / RulesPerPage);
    public string? Filter { get; private set; }
    public string? OwnAddress => Normalize(HttpContext.Connection.RemoteIpAddress)?.ToString();

    /// <summary>Addresses with failed logins in the last 24 hours that are not blocked or allowed yet (all, not only the 30 shown).</summary>
    public int Blockable { get; private set; }

    /// <param name="seite">Page of the rules (from 1).</param>
    /// <param name="q">Part of an address, network or comment.</param>
    public void OnGet(int seite = 1, string? q = null)
    {
        Lockouts = throttle.ListLockouts();
        var allRules = rules.List();
        RulesCount = allRules.Count;
        Filter = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var matching = allRules
            .Where(r => Filter is null || r.NetworkText.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                        (r.Comment ?? "").Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Created).ThenByDescending(r => r.Id)
            .ToList();
        RulesTotal = matching.Count;
        RulePage = Math.Clamp(seite, 1, RulePages);
        Rules = matching.Skip((RulePage - 1) * RulesPerPage).Take(RulesPerPage).ToList();
        var all = FailingSinceYesterday(Lockouts);
        Failing = all.Take(30).ToList();
        Blockable = all.Count(f => CanBlock(f) && !IsOwn(f));
    }

    /// <summary>Blocks every address of the list at once (permanently, like the single "Sperren" buttons).</summary>
    public IActionResult OnPostBlockAll()
    {
        var blocked = 0;
        // Never one's own address: that would lock the administrator out of this page.
        foreach (var failing in FailingSinceYesterday(throttle.ListLockouts()).Where(f => CanBlock(f) && !IsOwn(f)))
        {
            try
            {
                rules.Add(failing.Ip, IpRuleKind.Block, $"{failing.Failures} Fehl-Logins (alle gesperrt)", null);
                blocked++;
            }
            catch (FormatException)
            {
            }
        }

        Message = blocked == 0 ? "Keine Adresse zu sperren." : $"{blocked} Adresse(n) dauerhaft gesperrt.";
        return RedirectToPage();
    }

    private static bool CanBlock(FailingAddress failing) => failing.Rule is null;

    private bool IsOwn(FailingAddress failing) =>
        !IPAddress.TryParse(failing.Ip, out var ip) || Normalize(HttpContext.Connection.RemoteIpAddress) is { } own && Normalize(ip)!.Equals(own);

    private List<FailingAddress> FailingSinceYesterday(IReadOnlyList<Lockout> lockouts)
    {
        var locked = lockouts.Select(l => l.Address.ToString()).ToHashSet();
        return log.Query(new SpamLogQuery(Since: DateTimeOffset.UtcNow.AddDays(-1), Stage: SpamLogStage.Auth))
            .Where(e => e.ClientIp is not null && e.Action is SpamLogAction.LoginFailed or SpamLogAction.LockedOut)
            .GroupBy(e => e.ClientIp!)
            .Select(g => new FailingAddress(g.Key, g.Count(), g.Select(e => e.Recipient ?? "").Where(u => u.Length > 0).Distinct().Take(5).ToList(),
                g.Max(e => e.Time), IPAddress.TryParse(g.Key, out var ip) ? rules.Find(ip) : null, locked.Contains(g.Key)))
            .OrderByDescending(f => f.Failures)
            .ToList();
    }

    public IActionResult OnPostAdd(string network, IpRuleKind kind, string? comment, int days)
    {
        try
        {
            var range = IpRules.Parse(network ?? "");
            if (kind == IpRuleKind.Block && Normalize(HttpContext.Connection.RemoteIpAddress) is { } own && range.Contains(own))
            {
                ErrorMessage = $"{network} enthält die eigene Adresse {own} – damit wäre die Weboberfläche für Sie gesperrt.";
                return RedirectToPage();
            }

            var rule = rules.Add(network!, kind, comment, days > 0 ? TimeSpan.FromDays(days) : null);
            if (kind == IpRuleKind.Allow)
            {
                throttle.Release(range.Network);
            }

            Message = kind == IpRuleKind.Block
                ? $"{rule.NetworkText} ist gesperrt{(rule.Expires is { } until ? $" bis {Format.Time(until)}" : "")}."
                : $"{rule.NetworkText} wird nie mehr wegen Fehl-Logins gesperrt.";
        }
        catch (FormatException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    /// <summary>Removes a rule; back to the same page of the list (and the same filter).</summary>
    public IActionResult OnPostRemove(long id, int seite = 1, string? q = null)
    {
        Message = rules.Remove(id) ? "Regel entfernt." : null;
        return RedirectToPage(new { seite, q });
    }

    public string RulesUrl(int page) =>
        $"/Admin/IpBlocks?seite={page}{(Filter is null ? "" : "&q=" + Uri.EscapeDataString(Filter))}#regeln";

    public IActionResult OnPostRelease(string ip)
    {
        Message = IPAddress.TryParse(ip, out var address) && throttle.Release(address) ? $"Sperre für {ip} aufgehoben." : null;
        return RedirectToPage();
    }

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
