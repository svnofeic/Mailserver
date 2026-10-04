using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailserver.Core.Backup;

public sealed record OneDriveToken(string RefreshToken, string ClientId, string Tenant, string? Account);

/// <summary>A pending sign-in: the user opens <see cref="VerificationUri"/> and enters <see cref="UserCode"/>.</summary>
public sealed record DeviceLogin(string UserCode, string VerificationUri, string DeviceCode, TimeSpan Interval, DateTimeOffset Expires);

/// <summary>
/// Backup into OneDrive (personal or Microsoft 365) through Microsoft Graph. The server signs in with the device code flow:
/// the administrator confirms a code on microsoft.com/devicelogin once, the server then keeps a refresh token.
/// </summary>
public sealed class OneDriveStore : IBackupStore
{
    public const string Provider = "onedrive";
    public const string Scope = "Files.ReadWrite offline_access";

    private const int SimpleUploadLimit = 4 * 1024 * 1024;
    private const int ChunkSize = 32 * 320 * 1024; // 10 MiB, a multiple of 320 KiB as Graph requires

    private readonly BackupOptions _settings;
    private readonly CloudTokens _tokens;
    private readonly HttpClient _http = CloudHttp.CreateClient();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly string _folder;
    private OneDriveToken _token;
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public OneDriveStore(BackupOptions settings, CloudTokens tokens)
    {
        _settings = settings;
        _tokens = tokens;
        _token = tokens.Load<OneDriveToken>(Provider) ?? throw new BackupException("Noch nicht mit OneDrive verbunden.");
        _folder = settings.RemoteFolder.Trim('/', '\\').Replace('\\', '/');
    }

    public string Description => $"OneDrive{(_token.Account is { } account ? $" ({account})" : "")}: /{_folder}";

    public string Key => $"onedrive:{_token.Account}:{_folder}".ToLowerInvariant();

    private string Graph => (_settings.OneDriveGraphUrl ?? "https://graph.microsoft.com/v1.0").TrimEnd('/');

    public static string LoginUrl(BackupOptions settings) => (settings.OneDriveLoginUrl ?? "https://login.microsoftonline.com").TrimEnd('/');

    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken) =>
        (await ChildrenAsync(folder, cancellationToken)).Where(c => c["folder"] is not null).Select(c => (string)c["name"]!).ToList();

    public async Task<IReadOnlyList<StoredFile>> ListFilesAsync(string folder, CancellationToken cancellationToken)
    {
        var result = new List<StoredFile>();
        var pending = new Queue<string>([""]);
        while (pending.TryDequeue(out var relative))
        {
            foreach (var child in await ChildrenAsync(Combine(folder, relative), cancellationToken))
            {
                var path = Combine(relative, (string)child["name"]!);
                if (child["folder"] is not null)
                {
                    pending.Enqueue(path);
                }
                else
                {
                    result.Add(new StoredFile(path, (long?)child["size"] ?? 0));
                }
            }
        }

        return result;
    }

    public async Task UploadAsync(string localFile, string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(localFile).Length;
        if (length <= SimpleUploadLimit)
        {
            var bytes = await File.ReadAllBytesAsync(localFile, cancellationToken);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{Item(path)}/content")
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            }, cancellationToken);
            await EnsureSuccessAsync(response, $"Hochladen von {path}", cancellationToken);
            return;
        }

        // Larger files go through an upload session in pieces.
        using var session = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Item(path)}/createUploadSession")
        {
            Content = JsonContent("""{"item":{"@microsoft.graph.conflictBehavior":"replace"}}"""),
        }, cancellationToken);
        await EnsureSuccessAsync(session, $"Hochladen von {path}", cancellationToken);
        var uploadUrl = (string)JsonNode.Parse(await session.Content.ReadAsStringAsync(cancellationToken))!["uploadUrl"]!;

        await using var stream = File.OpenRead(localFile);
        var buffer = new byte[ChunkSize];
        for (long offset = 0; offset < length;)
        {
            var read = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, length - offset), throwOnEndOfStream: true, cancellationToken);
            var start = offset;
            // The upload URL is pre-authenticated: no Authorization header.
            using var chunk = await CloudHttp.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(buffer, 0, read)
                {
                    Headers = { ContentRange = new ContentRangeHeaderValue(start, start + read - 1, length) },
                },
            }, cancellationToken);
            await EnsureSuccessAsync(chunk, $"Hochladen von {path}", cancellationToken);
            offset += read;
        }
    }

    public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Item(path)}/content"), cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, $"Herunterladen von {path}", cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        await using var file = File.Create(localFile);
        await response.Content.CopyToAsync(file, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Item(path)}/content"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"Lesen von {path}", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Item(path)), cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, $"Löschen von {path}", cancellationToken);
        }
    }

    /// <summary>Name of the account and free space, for the settings page.</summary>
    public async Task<(string? Account, long? Free)> DescribeAccountAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Graph}/me/drive"), cancellationToken);
        await EnsureSuccessAsync(response, "Abfrage des Kontos", cancellationToken);
        var drive = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
        var user = drive["owner"]?["user"];
        return ((string?)user?["email"] ?? (string?)user?["displayName"], (long?)drive["quota"]?["remaining"]);
    }

    public void Dispose() => _http.Dispose();

    // ---- Sign-in (device code flow) ----

    public static async Task<DeviceLogin> StartLoginAsync(BackupOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OneDriveClientId))
        {
            throw new BackupException("Bitte zuerst die Anwendungs-ID (Client-ID) der App-Registrierung eintragen (siehe Anleitung).");
        }

        using var http = CloudHttp.CreateClient();
        using var response = await CloudHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post,
            $"{LoginUrl(settings)}/{settings.OneDriveTenant}/oauth2/v2.0/devicecode")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = settings.OneDriveClientId.Trim(), ["scope"] = Scope }),
        }, cancellationToken);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
        if (!response.IsSuccessStatusCode)
        {
            throw new BackupException($"Microsoft lehnt die Anmeldung ab: {Describe(json)}");
        }

        return new DeviceLogin((string)json["user_code"]!, (string)json["verification_uri"]!, (string)json["device_code"]!,
            TimeSpan.FromSeconds((int?)json["interval"] ?? 5), DateTimeOffset.UtcNow.AddSeconds((int?)json["expires_in"] ?? 900));
    }

    /// <summary>Waits until the user has confirmed the code, then stores the token. Returns the account name.</summary>
    public static async Task<string?> CompleteLoginAsync(BackupOptions settings, DeviceLogin login, CloudTokens tokens, CancellationToken cancellationToken)
    {
        using var http = CloudHttp.CreateClient();
        var interval = login.Interval;
        while (DateTimeOffset.UtcNow < login.Expires)
        {
            await Task.Delay(interval, cancellationToken);
            using var response = await CloudHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post,
                $"{LoginUrl(settings)}/{settings.OneDriveTenant}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = settings.OneDriveClientId!.Trim(),
                    ["device_code"] = login.DeviceCode,
                }),
            }, cancellationToken);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
            switch ((string?)json["error"])
            {
                case null:
                    tokens.Save(Provider, new OneDriveToken((string)json["refresh_token"]!, settings.OneDriveClientId!.Trim(), settings.OneDriveTenant, null));
                    using (var store = new OneDriveStore(settings, tokens))
                    {
                        var (account, _) = await store.DescribeAccountAsync(cancellationToken);
                        tokens.Save(Provider, store._token with { Account = account });
                        return account;
                    }

                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined" or "access_denied":
                    throw new BackupException("Die Anmeldung wurde abgelehnt.");
                default:
                    throw new BackupException($"Anmeldung fehlgeschlagen: {Describe(json)}");
            }
        }

        throw new BackupException("Der Code ist abgelaufen – bitte noch einmal verbinden.");
    }

    // ---- internals ----

    private string Item(string path)
    {
        var full = CloudHttp.EncodePath(Combine(_folder, path));
        return $"{Graph}/me/drive/root:/{full}:";
    }

    private async Task<List<JsonNode>> ChildrenAsync(string folder, CancellationToken cancellationToken)
    {
        var result = new List<JsonNode>();
        string? url = $"{Item(folder)}/children?$select=name,size,folder,file&$top=999";
        while (url is not null)
        {
            var next = url;
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, next), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return result;
            }

            await EnsureSuccessAsync(response, $"Auflisten von {folder}", cancellationToken);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
            result.AddRange(json["value"]!.AsArray().Where(v => v is not null)!);
            url = (string?)json["@odata.nextLink"];
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var token = await AccessTokenAsync(cancellationToken);
        var response = await CloudHttp.SendAsync(_http, () => Authorize(create(), token), cancellationToken, completion);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // The access token may have been revoked early: one more try with a fresh one.
        response.Dispose();
        _accessToken = null;
        token = await AccessTokenAsync(cancellationToken);
        return await CloudHttp.SendAsync(_http, () => Authorize(create(), token), cancellationToken, completion);
    }

    private static HttpRequestMessage Authorize(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpires)
            {
                return _accessToken;
            }

            using var response = await CloudHttp.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Post,
                $"{LoginUrl(_settings)}/{_token.Tenant}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _token.ClientId,
                    ["refresh_token"] = _token.RefreshToken,
                    ["scope"] = Scope,
                }),
            }, cancellationToken);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
            if (!response.IsSuccessStatusCode || json["access_token"] is null)
            {
                throw new BackupException(
                    $"Die Verbindung zu OneDrive ist abgelaufen oder wurde widerrufen ({Describe(json)}) – bitte unter Admin → Datensicherung neu verbinden.");
            }

            _accessToken = (string)json["access_token"]!;
            _accessExpires = DateTimeOffset.UtcNow.AddSeconds(((int?)json["expires_in"] ?? 3600) - 300);
            if ((string?)json["refresh_token"] is { } refresh && refresh != _token.RefreshToken)
            {
                // Microsoft hands out a new refresh token each time; the old one expires eventually.
                _token = _token with { RefreshToken = refresh };
                _tokens.Save(Provider, _token);
            }

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string detail;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            detail = (string?)error?["message"] ?? (string?)error?["code"] ?? body;
        }
        catch (JsonException)
        {
            detail = body;
        }

        throw new BackupException(response.StatusCode == HttpStatusCode.InsufficientStorage
            ? "Der OneDrive-Speicher ist voll."
            : $"OneDrive: {action} fehlgeschlagen ({(int)response.StatusCode}): {Truncate(detail)}");
    }

    private static string Describe(JsonNode json) => Truncate((string?)json["error_description"] ?? (string?)json["error"] ?? json.ToJsonString());

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + " …" : text;

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static string Combine(string a, string b) => string.Join('/', new[] { a, b }.Where(p => p.Length > 0));
}
