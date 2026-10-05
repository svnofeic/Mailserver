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

    public IActionResult OnPostTest()
    {
        if (subscriptions.ForAccount(CurrentAccount.Id).Count == 0)
        {
            ErrorMessage = "Noch kein Gerät eingeschaltet.";
        }
        else
        {
            notifier.Test(CurrentAccount);
            Message = "Testbenachrichtigung an alle Geräte geschickt – sie sollte in wenigen Sekunden erscheinen.";
        }

        return RedirectToPage();
    }

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
