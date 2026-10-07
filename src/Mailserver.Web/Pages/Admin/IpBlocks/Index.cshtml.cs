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
    public IReadOnlyList<IpRule> Rules { get; private set; } = [];
    public string? OwnAddress => Normalize(HttpContext.Connection.RemoteIpAddress)?.ToString();

    public void OnGet()
    {
        Lockouts = throttle.ListLockouts();
        Rules = rules.List();
        var locked = Lockouts.Select(l => l.Address.ToString()).ToHashSet();
        Failing = log.Query(new SpamLogQuery(Since: DateTimeOffset.UtcNow.AddDays(-1), Stage: SpamLogStage.Auth))
            .Where(e => e.ClientIp is not null && e.Action is SpamLogAction.LoginFailed or SpamLogAction.LockedOut)
            .GroupBy(e => e.ClientIp!)
            .Select(g => new FailingAddress(g.Key, g.Count(), g.Select(e => e.Recipient ?? "").Where(u => u.Length > 0).Distinct().Take(5).ToList(),
                g.Max(e => e.Time), IPAddress.TryParse(g.Key, out var ip) ? rules.Find(ip) : null, locked.Contains(g.Key)))
            .OrderByDescending(f => f.Failures)
            .Take(30)
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

    public IActionResult OnPostRemove(long id)
    {
        Message = rules.Remove(id) ? "Regel entfernt." : null;
        return RedirectToPage();
    }

    public IActionResult OnPostRelease(string ip)
    {
        Message = IPAddress.TryParse(ip, out var address) && throttle.Release(address) ? $"Sperre für {ip} aufgehoben." : null;
        return RedirectToPage();
    }

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
