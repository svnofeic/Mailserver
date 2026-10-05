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
        try
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

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (PushOutcome.Sent, null);
            }

            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            var detail = $"{endpoint.Host} antwortet {(int)response.StatusCode} {response.ReasonPhrase}{(text.Length > 0 ? ": " + text : "")}";
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? (PushOutcome.Gone, detail)
                : (PushOutcome.Failed, detail.Length > 400 ? detail[..400] : detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network, TLS, timeout – or a broken subscription key: never let one device stop the others.
            var reason = ex is TaskCanceledException ? "keine Antwort nach 30 Sekunden" : ex.GetBaseException().Message;
            return (PushOutcome.Failed, $"{Host(subscription.Endpoint)} nicht erreichbar: {reason}");
        }
    }

    private static string Host(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "Push-Dienst";
}
