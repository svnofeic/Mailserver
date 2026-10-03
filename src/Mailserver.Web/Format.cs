using System.Globalization;

namespace Mailserver.Web;

/// <summary>Display helpers in German conventions; times in the server's local time zone.</summary>
public static class Format
{
    private static readonly CultureInfo German = CultureInfo.InvariantCulture;

    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace('.', ','),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB".Replace('.', ','),
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB".Replace('.', ','),
    };

    public static string Score(double? score) => score is { } value ? value.ToString("0.0", German).Replace('.', ',') : "";

    public static string Number(double value) => value.ToString("0.##", German).Replace('.', ',');

    public static string FolderName(string? folder) => folder switch
    {
        null => "–",
        "INBOX" => "Posteingang",
        "Junk" => "Spam",
        "Sent" => "Gesendet",
        "Drafts" => "Entwürfe",
        "Trash" => "Papierkorb",
        _ => folder,
    };

    public static string Action(string action) => action switch
    {
        "rejected" => "abgelehnt",
        "deferred" => "zurückgestellt",
        "accepted" => "angenommen",
        "spam" => "Spam",
        "delivered" => "zugestellt",
        "discarded" => "gelöscht",
        "not-forwarded" => "nicht weitergeleitet",
        "marked-spam" => "als Spam markiert",
        "marked-ham" => "aus Spam geholt",
        "sent" => "versendet",
        "failed" => "fehlgeschlagen",
        "login-failed" => "Anmeldung fehlgeschlagen",
        "locked-out" => "IP gesperrt",
        _ => action,
    };

    public static string Stage(string stage) => stage switch
    {
        "connect" => "Verbindung",
        "sender" => "Absender",
        "recipient" => "Empfänger",
        "data" => "Prüfung",
        "delivery" => "Zustellung",
        "feedback" => "Feedback",
        "submission" => "Versand",
        "outbound" => "Ausgang",
        "auth" => "Anmeldung",
        _ => stage,
    };

    /// <summary>CSS class for a log action (good / bad / neutral).</summary>
    public static string Tone(string action) => action switch
    {
        "rejected" or "spam" or "discarded" or "failed" or "login-failed" or "locked-out" or "marked-spam" => "bad",
        "deferred" or "not-forwarded" => "warn",
        _ => "good",
    };
}
