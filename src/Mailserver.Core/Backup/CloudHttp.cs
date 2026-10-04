using System.Net;

namespace Mailserver.Core.Backup;

/// <summary>HTTP for the cloud storages: retries on throttling (429), server errors and dropped connections.</summary>
internal static class CloudHttp
{
    private const int Attempts = 5;

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Mailserver-Backup" } },
    };

    /// <summary>Sends a request built by <paramref name="create"/> (requests cannot be sent twice) and retries if worthwhile.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> create, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            // Disposing the request also closes an uploaded file right away (otherwise Windows keeps it locked).
            using var request = create();
            try
            {
                response = await client.SendAsync(request, completion, cancellationToken);
                if (attempt < Attempts && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    response.Dispose();
                    await Task.Delay(delay < TimeSpan.FromMinutes(2) ? delay : TimeSpan.FromMinutes(2), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < Attempts)
            {
                response?.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
            catch (TaskCanceledException) when (attempt < Attempts && !cancellationToken.IsCancellationRequested)
            {
                response?.Dispose(); // timeout
            }
        }
    }

    /// <summary>Encodes each segment of a path for use in a URL.</summary>
    public static string EncodePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
