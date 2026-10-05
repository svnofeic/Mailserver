using Mailserver.Core.Push;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

/// <summary>Push notifications for new mail: devices of this mailbox, switching on, testing, removing.</summary>
public sealed class NotificationsModel(PushSubscriptionStore subscriptions, VapidKeys keys, PushNotifier notifier) : MailPageModel
{
    public IReadOnlyList<PushSubscription> Devices { get; private set; } = [];
    public string PublicKey => keys.PublicKey;

    public void OnGet() => Devices = subscriptions.ForAccount(CurrentAccount.Id);

    public IActionResult OnPostSubscribe(string endpoint, string p256dh, string auth)
    {
        try
        {
            var device = subscriptions.Save(CurrentAccount.Id, endpoint, p256dh, auth, DeviceName(Request.Headers.UserAgent.ToString()));
            Message = $"Benachrichtigungen auf „{device.Device}“ eingeschaltet.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostRemove(long id)
    {
        Message = subscriptions.Remove(CurrentAccount.Id, id) ? "Gerät entfernt." : null;
        return RedirectToPage();
    }

    public IActionResult OnPostUnsubscribe(string endpoint)
    {
        Message = subscriptions.RemoveEndpoint(CurrentAccount.Id, endpoint) ? "Benachrichtigungen auf diesem Gerät ausgeschaltet." : null;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var results = await notifier.TestAsync(CurrentAccount, HttpContext.RequestAborted);
        if (results.Count == 0)
        {
            ErrorMessage = "Noch kein Gerät eingeschaltet.";
        }
        else if (results.All(r => r.Outcome == PushOutcome.Sent))
        {
            Message = "Testbenachrichtigung vom Push-Dienst angenommen – sie sollte in wenigen Sekunden erscheinen.";
        }
        else
        {
            ErrorMessage = string.Join(" · ", results.Select(r => r.Outcome switch
            {
                PushOutcome.Sent => $"{r.Device.Device}: angenommen",
                PushOutcome.Gone => $"{r.Device.Device}: abgemeldet und entfernt – bitte auf dem Gerät neu einschalten",
                _ => $"{r.Device.Device}: {r.Detail}{Hint(r.Detail)}",
            }));
        }

        return RedirectToPage();
    }

    /// <summary>What a push service error usually means, in plain words.</summary>
    public static string Hint(string? detail) => detail switch
    {
        null => "",
        _ when detail.Contains("nicht erreichbar") => " (der Server kommt nicht ins Internet – ausgehende Verbindungen auf Port 443 in der Firewall erlauben)",
        _ when detail.Contains(" 401 ") || detail.Contains(" 403 ") => " (der Push-Dienst lehnt die Anmeldung des Servers ab – Uhrzeit des Servers prüfen; "
            + "hilft das nicht, das Gerät entfernen und neu einschalten)",
        _ when detail.Contains(" 400 ") => " (der Push-Dienst hält die Nachricht für ungültig)",
        _ when detail.Contains(" 413 ") => " (Nachricht zu groß)",
        _ when detail.Contains(" 429 ") => " (zu viele Nachrichten – später erneut versuchen)",
        _ => "",
    };

    /// <summary>"Chrome auf Android", "Safari auf iPhone" … from the user agent, so devices can be told apart.</summary>
    public static string DeviceName(string userAgent)
    {
        var os = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Mac OS X") => "Mac",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => "unbekanntes System",
        };
        var browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/") => "Edge",
            _ when userAgent.Contains("Firefox/") => "Firefox",
            _ when userAgent.Contains("SamsungBrowser/") => "Samsung Internet",
            _ when userAgent.Contains("Chrome/") || userAgent.Contains("CriOS/") => "Chrome",
            _ when userAgent.Contains("Safari/") => "Safari",
            _ => "Browser",
        };
        return $"{browser} auf {os}";
    }
}
