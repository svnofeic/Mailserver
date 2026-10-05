using System.Net;
using System.Text;

namespace Mailserver.Core.Push;

public enum PushOutcome
{
    Sent,
    /// <summary>The browser unsubscribed or the app was removed (404/410): the subscription is deleted.</summary>
    Gone,
    Failed,
}

/// <summary>Sends one encrypted, signed message to the push service of a subscription.</summary>
public sealed class WebPushSender(VapidKeys keys, HttpClient? client = null)
{
    private readonly HttpClient _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(PushOutcome Outcome, string? Detail)> SendAsync(PushSubscription subscription, string json, string subject,
        CancellationToken cancellationToken)
    {
        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(json), WebPushCrypto.FromBase64Url(subscription.P256dh),
            WebPushCrypto.FromBase64Url(subscription.Auth));
        var endpoint = new Uri(subscription.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", keys.Authorization(endpoint, subject));
        // Kept a day if the device is offline; a newer message with the same topic replaces an undelivered older one.
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", "normal");
        request.Headers.Add("Topic", "inbox");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (PushOutcome.Sent, null);
            }

            var detail = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}".Trim();
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? (PushOutcome.Gone, detail)
                : (PushOutcome.Failed, detail.Length > 300 ? detail[..300] : detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (PushOutcome.Failed, ex.Message);
        }
    }
}
