using System.Net;
using Mailserver.Core.Data;

namespace Mailserver.Core.Security;

public enum IpRuleKind
{
    /// <summary>No SMTP, IMAP or web access at all.</summary>
    Block,
    /// <summary>Never locked out after failed logins (e.g. the office).</summary>
    Allow,
}

public sealed record IpRule(long Id, NetworkRange Network, IpRuleKind Kind, string? Comment, DateTimeOffset Created, DateTimeOffset? Expires)
{
    public string NetworkText => Network.PrefixLength == (Network.Network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
        ? Network.Network.ToString()
        : $"{Network.Network}/{Network.PrefixLength}";
}

/// <summary>
/// Permanent (or time-limited) block and allow rules for addresses and networks. Kept in memory and re-read every few
/// seconds, so changes made with mailadmin take effect without a restart.
/// </summary>
public sealed class IpRules(Database database, TimeProvider timeProvider)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private readonly Lock _lock = new();
    private IReadOnlyList<IpRule> _rules = [];
    private DateTimeOffset _loaded = DateTimeOffset.MinValue;

    public bool IsBlocked(IPAddress? address) => Find(address) is { Kind: IpRuleKind.Block };

    public bool IsAllowed(IPAddress? address) => Find(address) is { Kind: IpRuleKind.Allow };

    /// <summary>The rule that applies to an address; "allow" wins over "block", a narrower network over a wider one.</summary>
    public IpRule? Find(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        return Current().Where(r => (r.Expires is null || r.Expires > now) && r.Network.Contains(address))
            .OrderBy(r => r.Kind == IpRuleKind.Allow ? 0 : 1)
            .ThenByDescending(r => r.Network.PrefixLength)
            .FirstOrDefault();
    }

    /// <summary>All rules that have not expired.</summary>
    public IReadOnlyList<IpRule> List()
    {
        var now = timeProvider.GetUtcNow();
        return Current().Where(r => r.Expires is null || r.Expires > now).OrderBy(r => r.Kind).ThenByDescending(r => r.Created).ToList();
    }

    /// <summary>Adds a rule; an existing rule of the same kind for the same network is replaced.</summary>
    /// <exception cref="FormatException">Not an address or network.</exception>
    public IpRule Add(string network, IpRuleKind kind, string? comment, TimeSpan? duration = null)
    {
        var range = Parse(network);
        var now = timeProvider.GetUtcNow();
        var text = new IpRule(0, range, kind, null, now, null).NetworkText;
        using var connection = database.Open();
        connection.Execute("DELETE FROM ip_rules WHERE network = $network AND kind = $kind", ("$network", text), ("$kind", kind.ToString()));
        connection.Execute("DELETE FROM ip_rules WHERE expires_utc IS NOT NULL AND expires_utc < $now", ("$now", now.ToDbTime()));
        var id = (long)connection.Scalar(
            "INSERT INTO ip_rules (network, kind, comment, created_utc, expires_utc) VALUES ($network, $kind, $comment, $now, $expires) RETURNING id",
            ("$network", text), ("$kind", kind.ToString()), ("$comment", string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()),
            ("$now", now.ToDbTime()), ("$expires", duration is { } d ? (now + d).ToDbTime() : null))!;
        Invalidate();
        return new IpRule(id, range, kind, comment, now, duration is { } span ? now + span : null);
    }

    public bool Remove(long id)
    {
        using var connection = database.Open();
        var removed = connection.Execute("DELETE FROM ip_rules WHERE id = $id", ("$id", id)) > 0;
        Invalidate();
        return removed;
    }

    /// <summary>Removes all rules for exactly this address or network.</summary>
    public int Remove(string network)
    {
        var text = new IpRule(0, Parse(network), IpRuleKind.Block, null, default, null).NetworkText;
        using var connection = database.Open();
        var removed = connection.Execute("DELETE FROM ip_rules WHERE network = $network", ("$network", text));
        Invalidate();
        return removed;
    }

    private void Invalidate()
    {
        lock (_lock)
        {
            _loaded = DateTimeOffset.MinValue;
        }
    }

    private IReadOnlyList<IpRule> Current()
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (now - _loaded < RefreshInterval && now >= _loaded)
            {
                return _rules;
            }

            using var connection = database.Open();
            _rules = connection.Query("SELECT id, network, kind, comment, created_utc, expires_utc FROM ip_rules", r =>
                    (Id: r.GetInt64(0), Network: r.GetString(1), Kind: r.GetString(2), Comment: r.IsDBNull(3) ? null : r.GetString(3),
                        Created: r.GetDbTime(4), Expires: r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetDbTime(5)))
                .Select(r => (Row: r, Parsed: TryParse(r.Network), Kind: Enum.TryParse<IpRuleKind>(r.Kind, out var kind) ? kind : (IpRuleKind?)null))
                .Where(r => r.Parsed is not null && r.Kind is not null)
                .Select(r => new IpRule(r.Row.Id, r.Parsed!.Value, r.Kind!.Value, r.Row.Comment, r.Row.Created, r.Row.Expires))
                .ToList();
            _loaded = now;
            return _rules;
        }
    }

    /// <exception cref="FormatException">With a message for the user.</exception>
    public static NetworkRange Parse(string network)
    {
        try
        {
            return NetworkRange.Parse(network.Trim());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"„{network}“ ist keine IP-Adresse und kein Netz (z. B. 203.0.113.7 oder 203.0.113.0/24).", ex);
        }
    }

    private static NetworkRange? TryParse(string value)
    {
        try
        {
            return NetworkRange.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
