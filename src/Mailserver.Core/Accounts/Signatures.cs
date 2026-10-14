using Mailserver.Core.Data;

namespace Mailserver.Core.Accounts;

/// <param name="InReplies">Also below replies and forwards, not only in new messages.</param>
public sealed record Signature(string Address, string Text, bool InReplies);

/// <summary>Plain-text signatures the webmail adds when writing from a sender address.</summary>
public sealed class Signatures(Database database)
{
    public const int MaxLength = 2000;

    public IReadOnlyDictionary<string, Signature> List(long accountId)
    {
        using var connection = database.Open();
        return connection.Query("SELECT address, text, in_replies FROM signatures WHERE account_id = $id",
                r => new Signature(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0), ("$id", accountId))
            .ToDictionary(s => s.Address, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stores the signature for <paramref name="address"/>; an empty text removes it.</summary>
    public void Set(long accountId, string address, string? text, bool inReplies)
    {
        text = (text ?? "").Replace("\r\n", "\n").TrimEnd();
        if (text.Length > MaxLength)
        {
            throw new ArgumentException($"Die Signatur ist zu lang (höchstens {MaxLength} Zeichen).");
        }

        // The usual separator is added when inserting; one typed by the user is not doubled.
        if (text.StartsWith("-- \n", StringComparison.Ordinal) || text.StartsWith("--\n", StringComparison.Ordinal))
        {
            text = text[(text.IndexOf('\n') + 1)..];
        }

        using var connection = database.Open();
        if (text.Trim().Length == 0)
        {
            connection.Execute("DELETE FROM signatures WHERE account_id = $id AND address = $address", ("$id", accountId), ("$address", address));
            return;
        }

        connection.Execute(
            """
            INSERT INTO signatures (account_id, address, text, in_replies) VALUES ($id, $address, $text, $replies)
            ON CONFLICT (account_id, address) DO UPDATE SET text = $text, in_replies = $replies
            """,
            ("$id", accountId), ("$address", address), ("$text", text), ("$replies", inReplies ? 1 : 0));
    }
}
