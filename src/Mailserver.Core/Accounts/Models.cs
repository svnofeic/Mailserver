namespace Mailserver.Core.Accounts;

/// <param name="CatchAll">Receives mail for addresses of the domain that do not exist (null: such mail is rejected).</param>
public sealed record Domain(long Id, string Name, string? DkimSelector, EmailAddress? CatchAll = null);

/// <param name="IsAdmin">May use the administration area of the web interface.</param>
public sealed record Account(long Id, EmailAddress Address, long QuotaBytes, bool Enabled, bool IsAdmin = false);

public sealed record Alias(EmailAddress Address, IReadOnlyList<EmailAddress> Targets);

/// <summary>Where a recipient address ends up after alias expansion.</summary>
public sealed record RecipientResolution(IReadOnlyList<Account> LocalAccounts, IReadOnlyList<EmailAddress> ExternalAddresses)
{
    public bool IsEmpty => LocalAccounts.Count == 0 && ExternalAddresses.Count == 0;
}
