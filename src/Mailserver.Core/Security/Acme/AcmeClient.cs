using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailserver.Core.Security.Acme;

/// <summary>An error reported by the ACME server (RFC 8555 problem document) or a failed step.</summary>
public sealed class AcmeException(string message, string? type = null) : Exception(message)
{
    public string? Type { get; } = type;
}

/// <summary>
/// Minimal ACME v2 client (RFC 8555) for Let's Encrypt: account, order, http-01 challenge, finalize, download.
/// Requests are signed with an ES256 account key (JWS).
/// </summary>
public sealed class AcmeClient : IDisposable
{
    private const string JoseJson = "application/jose+json";

    private readonly HttpClient _http;
    private readonly Uri _directoryUrl;
    private readonly ECDsa _accountKey;
    private JsonObject? _directory;
    private string? _nonce;

    public AcmeClient(Uri directoryUrl, ECDsa accountKey, HttpMessageHandler? handler = null)
    {
        _directoryUrl = directoryUrl;
        _accountKey = accountKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mailserver-ACME/1.0");
    }

    /// <summary>Account URL ("kid"); set by <see cref="RegisterAsync"/> or from a previous run.</summary>
    public string? AccountUrl { get; set; }

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the account or finds the existing one for this key; agrees to the terms of service.</summary>
    public async Task<string> RegisterAsync(string? email, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["termsOfServiceAgreed"] = true };
        if (!string.IsNullOrWhiteSpace(email))
        {
            payload["contact"] = new JsonArray($"mailto:{email.Trim()}");
        }

        var response = await PostAsync(await EndpointAsync("newAccount", cancellationToken), payload, useJwk: true, cancellationToken);
        AccountUrl = response.Location ?? throw new AcmeException("Der ACME-Server hat keine Konto-Adresse geliefert.");
        return AccountUrl;
    }

    /// <summary>Places an order for the names; returns its URL and content.</summary>
    public async Task<(string Url, JsonObject Order)> CreateOrderAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var identifiers = new JsonArray(names.Select(n => (JsonNode)new JsonObject { ["type"] = "dns", ["value"] = n }).ToArray());
        var response = await PostAsync(await EndpointAsync("newOrder", cancellationToken), new JsonObject { ["identifiers"] = identifiers },
            useJwk: false, cancellationToken);
        return (response.Location ?? throw new AcmeException("Der ACME-Server hat keine Bestell-Adresse geliefert."), response.Json);
    }

    /// <summary>POST-as-GET: reads an order, authorization or challenge.</summary>
    public async Task<JsonObject> GetAsync(string url, CancellationToken cancellationToken) =>
        (await PostAsync(url, null, useJwk: false, cancellationToken)).Json;

    /// <summary>Tells the server the challenge response is in place.</summary>
    public async Task AcceptChallengeAsync(string url, CancellationToken cancellationToken) =>
        await PostAsync(url, new JsonObject(), useJwk: false, cancellationToken);

    public async Task<JsonObject> FinalizeAsync(string url, byte[] csr, CancellationToken cancellationToken) =>
        (await PostAsync(url, new JsonObject { ["csr"] = Base64Url(csr) }, useJwk: false, cancellationToken)).Json;

    /// <summary>The issued certificate with its chain (PEM).</summary>
    public async Task<string> DownloadCertificateAsync(string url, CancellationToken cancellationToken) =>
        (await PostAsync(url, null, useJwk: false, cancellationToken)).Text;

    /// <summary>token "." thumbprint of the account key — the content the http-01 challenge file must have.</summary>
    public string KeyAuthorization(string token) => $"{token}.{Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(Thumbprintable())))}";

    private sealed record Response(JsonObject Json, string Text, string? Location);

    private async Task<string> EndpointAsync(string name, CancellationToken cancellationToken)
    {
        if (_directory is null)
        {
            using var response = await _http.GetAsync(_directoryUrl, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            _directory = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
                         ?? throw new AcmeException("Ungültiges ACME-Verzeichnis.");
        }

        return _directory[name]?.GetValue<string>() ?? throw new AcmeException($"Das ACME-Verzeichnis enthält kein \"{name}\".");
    }

    private async Task<Response> PostAsync(string url, JsonObject? payload, bool useJwk, CancellationToken cancellationToken)
    {
        // A server may reject a nonce once ("badNonce"); the error response carries a fresh one.
        for (var attempt = 0; ; attempt++)
        {
            _nonce ??= await NewNonceAsync(cancellationToken);
            var body = Sign(url, payload, useJwk, _nonce);
            _nonce = null;
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(JoseJson);
            using var response = await _http.PostAsync(url, content, cancellationToken);
            if (response.Headers.TryGetValues("Replay-Nonce", out var nonces))
            {
                _nonce = nonces.FirstOrDefault();
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var problem = ParseProblem(text, response.StatusCode);
                if (problem.Type?.EndsWith(":badNonce", StringComparison.Ordinal) == true && attempt < 2)
                {
                    continue;
                }

                throw problem;
            }

            var json = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                ? JsonNode.Parse(text) as JsonObject ?? []
                : [];
            return new Response(json, text, response.Headers.Location?.ToString());
        }
    }

    private async Task<string> NewNonceAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, await EndpointAsync("newNonce", cancellationToken));
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.First()
            : throw new AcmeException("Der ACME-Server hat keine Nonce geliefert.");
    }

    private string Sign(string url, JsonObject? payload, bool useJwk, string nonce)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url };
        if (useJwk || AccountUrl is null)
        {
            header["jwk"] = Jwk();
        }
        else
        {
            header["kid"] = AccountUrl;
        }

        var protectedPart = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var payloadPart = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        // ECDsa.SignData yields r||s (IEEE P1363), the format JWS requires.
        var signature = _accountKey.SignData(Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}"), HashAlgorithmName.SHA256);
        return new JsonObject { ["protected"] = protectedPart, ["payload"] = payloadPart, ["signature"] = Base64Url(signature) }.ToJsonString();
    }

    private JsonObject Jwk()
    {
        var p = _accountKey.ExportParameters(false);
        return new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = Base64Url(p.Q.X!), ["y"] = Base64Url(p.Q.Y!) };
    }

    /// <summary>The JWK members in lexicographic order without whitespace (RFC 7638).</summary>
    private string Thumbprintable()
    {
        var p = _accountKey.ExportParameters(false);
        return $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(p.Q.X!)}\",\"y\":\"{Base64Url(p.Q.Y!)}\"}}";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw ParseProblem(await response.Content.ReadAsStringAsync(cancellationToken), response.StatusCode);
        }
    }

    private static AcmeException ParseProblem(string text, HttpStatusCode status)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject problem)
            {
                var detail = problem["detail"]?.GetValue<string>() ?? problem["title"]?.GetValue<string>();
                var sub = problem["subproblems"] is JsonArray subs
                    ? string.Join("; ", subs.Select(s => s?["detail"]?.GetValue<string>()).Where(d => d is not null))
                    : "";
                return new AcmeException($"{detail}{(sub.Length > 0 ? $" ({sub})" : "")}", problem["type"]?.GetValue<string>());
            }
        }
        catch (JsonException)
        {
        }

        return new AcmeException($"ACME-Server antwortet mit {(int)status} {status}.");
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
