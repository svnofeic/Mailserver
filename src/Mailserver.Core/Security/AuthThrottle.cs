using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security;

/// <summary>A temporary lockout after failed logins.</summary>
public sealed record Lockout(IPAddress Address, DateTimeOffset LockedUntil, int LockoutsToday);

/// <summary>
/// Locks out IP addresses after repeated failed logins (SMTP submission, IMAP, web). Addresses that are locked out again
/// and again are blocked for days (<see cref="SecurityOptions.AutoBlockAfterLockouts"/>); permanent rules come from
/// <see cref="IpRules"/>.
/// </summary>
public sealed class AuthThrottle(IOptions<MailserverOptions> options, TimeProvider timeProvider, IpRules? rules = null,
    ILogger<AuthThrottle>? logger = null)
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
        public List<DateTimeOffset> Lockouts = [];
    }

    private readonly ConcurrentDictionary<IPAddress, State> _states = new();
    private readonly ILogger _logger = logger ?? NullLogger<AuthThrottle>.Instance;

    /// <summary>True if the address is blocked by a rule: it must not use any service.</summary>
    public bool IsBlocked(IPAddress? address) => address is not null && rules?.IsBlocked(Normalize(address)) == true;

    public bool IsLockedOut(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IsBlocked(address))
        {
            return true;
        }

        if (!_states.TryGetValue(Normalize(address), out var state))
        {
            return false;
        }

        lock (state)
        {
            return state.LockedUntil > timeProvider.GetUtcNow();
        }
    }

    /// <summary>Counts a failed login; returns true when this failure locks the address out.</summary>
    public bool RecordFailure(IPAddress? address)
    {
        if (address is null || rules?.IsAllowed(Normalize(address)) == true)
        {
            return false;
        }

        var lockedNow = false;
        var autoBlock = false;
        var lockoutsToday = 0;

        var settings = options.Value.Security;
        var now = timeProvider.GetUtcNow();
        var ip = Normalize(address);
        var state = _states.GetOrAdd(ip, _ => new State { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > settings.AuthFailureWindow)
            {
                state.WindowStart = now;
                state.Failures = 0;
            }

            if (++state.Failures >= settings.MaxAuthFailuresPerIp)
            {
                lockedNow = true;
                state.LockedUntil = now + settings.AuthLockoutDuration;
                state.Failures = 0;
                state.WindowStart = now;
                state.Lockouts.RemoveAll(t => now - t > TimeSpan.FromDays(1));
                state.Lockouts.Add(now);
                lockoutsToday = state.Lockouts.Count;
                autoBlock = settings.AutoBlockAfterLockouts > 0 && lockoutsToday >= settings.AutoBlockAfterLockouts && rules is not null;
            }
        }

        if (autoBlock)
        {
            var days = settings.AutoBlockDuration.TotalDays;
            rules!.Add(ip.ToString(), IpRuleKind.Block,
                $"automatisch: {lockoutsToday}× wegen Fehl-Logins gesperrt innerhalb von 24 Stunden", settings.AutoBlockDuration);
            _logger.LogWarning("Blocked {Ip} for {Days} days after {Count} lockouts", ip, days, lockoutsToday);
        }

        Prune(now);
        return lockedNow;
    }

    public void RecordSuccess(IPAddress? address)
    {
        if (address is not null)
        {
            _states.TryRemove(Normalize(address), out _);
        }
    }

    /// <summary>Addresses that are locked out right now.</summary>
    public IReadOnlyList<Lockout> ListLockouts()
    {
        var now = timeProvider.GetUtcNow();
        var result = new List<Lockout>();
        foreach (var (ip, state) in _states)
        {
            lock (state)
            {
                if (state.LockedUntil > now)
                {
                    result.Add(new Lockout(ip, state.LockedUntil, state.Lockouts.Count(t => now - t <= TimeSpan.FromDays(1))));
                }
            }
        }

        return result.OrderByDescending(l => l.LockedUntil).ToList();
    }

    /// <summary>Lifts a temporary lockout (rules are not affected).</summary>
    public bool Release(IPAddress address) => _states.TryRemove(Normalize(address), out _);

    private void Prune(DateTimeOffset now)
    {
        if (_states.Count < 10_000)
        {
            return;
        }

        var window = options.Value.Security.AuthFailureWindow;
        foreach (var (key, state) in _states)
        {
            lock (state)
            {
                if (state.LockedUntil < now && now - state.WindowStart > window)
                {
                    _states.TryRemove(key, out _);
                }
            }
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
