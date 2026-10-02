using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security;

/// <summary>
/// Locks out IP addresses after repeated failed logins. Shared by SMTP submission and (later) IMAP.
/// </summary>
public sealed class AuthThrottle(IOptions<MailserverOptions> options, TimeProvider timeProvider)
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
    }

    private readonly ConcurrentDictionary<IPAddress, State> _states = new();

    public bool IsLockedOut(IPAddress? address)
    {
        if (address is null || !_states.TryGetValue(Normalize(address), out var state))
        {
            return false;
        }

        lock (state)
        {
            return state.LockedUntil > timeProvider.GetUtcNow();
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        if (address is null)
        {
            return;
        }

        var settings = options.Value.Security;
        var now = timeProvider.GetUtcNow();
        var state = _states.GetOrAdd(Normalize(address), _ => new State { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > settings.AuthFailureWindow)
            {
                state.WindowStart = now;
                state.Failures = 0;
            }

            if (++state.Failures >= settings.MaxAuthFailuresPerIp)
            {
                state.LockedUntil = now + settings.AuthLockoutDuration;
                state.Failures = 0;
                state.WindowStart = now;
            }
        }

        Prune(now);
    }

    public void RecordSuccess(IPAddress? address)
    {
        if (address is not null)
        {
            _states.TryRemove(Normalize(address), out _);
        }
    }

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
