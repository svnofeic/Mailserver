using System.Globalization;

namespace Mailserver.Web.Webmail;

/// <summary>The time choices for "Später senden" and "Wiedervorlage", in the server's local time.</summary>
public static class LaterChoices
{
    public static readonly IReadOnlyList<(string Key, string Label)> Send =
    [
        ("tomorrow8", "Morgen 8:00"), ("tomorrow13", "Morgen 13:00"), ("monday8", "Montag 8:00"), ("custom", "Datum und Uhrzeit …"),
    ];

    public static readonly IReadOnlyList<(string Key, string Label)> FollowUp =
    [
        ("evening", "Heute 18:00"), ("tomorrow8", "Morgen 8:00"), ("3days", "In 3 Tagen, 8:00"), ("monday8", "Nächsten Montag 8:00"),
        ("week", "In einer Woche, 8:00"), ("custom", "Datum und Uhrzeit …"),
    ];

    /// <summary>
    /// The chosen point in time, or null if the choice is unknown or the custom value unreadable. A filled-in date and time
    /// wins over the list.
    /// </summary>
    public static DateTimeOffset? Resolve(string? key, string? custom, DateTimeOffset localNow)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            key = "custom";
        }

        DateTimeOffset At(int days, int hour) => Local(localNow.Date.AddDays(days).AddHours(hour));
        switch (key)
        {
            case "evening":
                return localNow.Hour < 18 ? At(0, 18) : At(1, 8);
            case "tomorrow8":
                return At(1, 8);
            case "tomorrow13":
                return At(1, 13);
            case "3days":
                return At(3, 8);
            case "week":
                return At(7, 8);
            case "monday8":
                var days = ((int)DayOfWeek.Monday - (int)localNow.DayOfWeek + 7) % 7;
                return At(days == 0 ? 7 : days, 8);
            case "custom":
                return DateTime.TryParseExact(custom, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var value)
                    ? Local(value)
                    : null;
            default:
                return null;
        }
    }

    private static DateTimeOffset Local(DateTime local) => new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
        TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)));
}
