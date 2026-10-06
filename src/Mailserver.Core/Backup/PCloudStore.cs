using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Mailserver.Core.Backup;

public sealed record PCloudToken(string Auth, string ApiUrl, string Account);

/// <summary>
/// Backup into pCloud through its HTTP API. The server logs in once with e-mail address and password (plus the code of the
/// two-factor authentication, if switched on) and keeps only the resulting auth token, not the password.
/// </summary>
public sealed class PCloudStore : IBackupStore
{
    public const string Provider = "pcloud";

    // pCloud error codes
    private const int LoginFailed = 2000;
    private const int FolderMissing = 2005;
    private const int FileMissing = 2009;
    private const int NotFound = 2055; // "File or folder not found" – what stat actually answers for a missing path
    private const int TwoFactorRequired = 2297;
    private const int CodeMissing = 1022;
    private const int CodeInvalid = 2012;
    private const int CodeExpired = 2064;

    private readonly PCloudToken _token;
    private readonly HttpClient _http = CloudHttp.CreateClient();
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, bool> _createdFolders = new(StringComparer.Ordinal);

    public PCloudStore(BackupOptions settings, CloudTokens tokens)
    {
        _token = tokens.Load<PCloudToken>(Provider) ?? throw new BackupException("Noch nicht mit pCloud verbunden.");
        _folder = "/" + settings.RemoteFolder.Trim('/', '\\').Replace('\\', '/');
    }

    public string Description => $"pCloud ({_token.Account}): {_folder}";

    /// <summary>
    /// Larger files go up in pieces of this size (upload_create / upload_write / upload_save): one request for a database of
    /// several hundred MB tends to be cut off on the way, and then everything would start again from the beginning.
    /// </summary>
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;

    public string Key => $"pcloud:{_token.Account}:{_folder}".ToLowerInvariant();

    public static string ApiUrl(BackupOptions settings) =>
        (settings.PCloudApiUrl ?? (string.Equals(settings.PCloudRegion, "US", StringComparison.OrdinalIgnoreCase)
            ? "https://api.pcloud.com"
            : "https://eapi.pcloud.com")).TrimEnd('/');

    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var json = await CallAsync("listfolder", new() { ["path"] = Full(folder), ["nofiles"] = "1" }, cancellationToken, FolderMissing, NotFound);
        return json is null ? [] : Contents(json["metadata"]).Where(c => (bool?)c["isfolder"] == true).Select(c => (string)c["name"]!).ToList();
    }

    public async Task<IReadOnlyList<StoredFile>> ListFilesAsync(string folder, CancellationToken cancellationToken)
    {
        var json = await CallAsync("listfolder", new() { ["path"] = Full(folder), ["recursive"] = "1" }, cancellationToken, FolderMissing, NotFound);
        var result = new List<StoredFile>();
        void Walk(JsonNode? node, string prefix)
        {
            foreach (var child in Contents(node))
            {
                var path = prefix + (string)child["name"]!;
                if ((bool?)child["isfolder"] == true)
                {
                    Walk(child, path + "/");
                }
                else
                {
                    result.Add(new StoredFile(path, (long?)child["size"] ?? 0));
                }
            }
        }

        Walk(json?["metadata"], "");
        return result;
    }

    public async Task UploadAsync(string localFile, string path, CancellationToken cancellationToken)
    {
        try
        {
            await UploadFileAsync(localFile, path, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Hochladen von {path} ({BackupManager.Format(new FileInfo(localFile).Length)}): {ex.Message}", ex);
        }
    }

    private async Task UploadFileAsync(string localFile, string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        var folder = full[..full.LastIndexOf('/')];
        await EnsureFolderAsync(folder, cancellationToken);

        var name = full[(full.LastIndexOf('/') + 1)..];
        if (new FileInfo(localFile).Length > ChunkSize)
        {
            await UploadInPiecesAsync(localFile, folder, name, path, cancellationToken);
            return;
        }

        var url = $"{_token.ApiUrl}/uploadfile?auth={Uri.EscapeDataString(_token.Auth)}&path={Uri.EscapeDataString(folder)}&nopartial=1";
        using var response = await CloudHttp.SendAsync(_http, () =>
            Upload(new HttpRequestMessage(HttpMethod.Post, url) { Content = FilePart(new StreamContent(File.OpenRead(localFile)), name) }),
            cancellationToken);
        CheckStored(await ReadAsync(response, cancellationToken), path);
    }

    /// <summary>
    /// The file as multipart/form-data the way browsers and curl send it. .NET's own form puts the boundary in quotes and the
    /// field names not (plus a filename*= parameter); pCloud then finds no file in the upload, answers "ok" for small files
    /// without storing anything and breaks off the connection for larger ones.
    /// </summary>
    private static MultipartFormDataContent FilePart(HttpContent file, string name)
    {
        var boundary = "----MailserverBackup" + Guid.NewGuid().ToString("N");
        var content = new MultipartFormDataContent(boundary);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // the file name of the part is the file name in pCloud; an existing file is replaced
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name.Replace("\"", "")}\"");
        content.Add(file);
        return content;
    }

    /// <summary>An upload counts only if pCloud reports the stored file – "ok" without one means it did not find the file.</summary>
    private static void CheckStored(JsonNode json, string path)
    {
        Check(json, $"Hochladen von {path}");
        if (json["metadata"] is not JsonArray { Count: > 0 })
        {
            throw new BackupException($"pCloud hat {path} nicht gespeichert (keine Datei im Upload erkannt).");
        }
    }

    private async Task UploadInPiecesAsync(string localFile, string folder, string name, string path, CancellationToken cancellationToken)
    {
        var created = await CallAsync("upload_create", [], cancellationToken);
        var uploadId = ((long?)created!["uploadid"] ?? throw new BackupException("pCloud: upload_create lieferte keine uploadid.")).ToString(CultureInfo.InvariantCulture);
        try
        {
            await using var file = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            while (true)
            {
                var length = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
                if (length == 0)
                {
                    break;
                }

                var url = $"{_token.ApiUrl}/upload_write?auth={Uri.EscapeDataString(_token.Auth)}&uploadid={uploadId}&uploadoffset={offset}";
                using var response = await CloudHttp.SendAsync(_http, () => Upload(new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new ByteArrayContent(buffer, 0, length),
                }), cancellationToken);
                Check(await ReadAsync(response, cancellationToken), $"Hochladen von {path}");
                offset += length;
            }

            await CallAsync("upload_save", new() { ["uploadid"] = uploadId, ["path"] = folder, ["name"] = name }, cancellationToken);
        }
        catch
        {
            // Unfinished uploads would otherwise occupy space at pCloud for a while.
            try
            {
                await CallAsync("upload_delete", new() { ["uploadid"] = uploadId }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is BackupException or HttpRequestException or TaskCanceledException)
            {
            }

            throw;
        }
    }

    public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken)
    {
        var link = await CallAsync("getfilelink", new() { ["path"] = Full(path) }, cancellationToken)
                   ?? throw new BackupException($"pCloud: {path} gibt es nicht.");
        var scheme = _token.ApiUrl.StartsWith("http://", StringComparison.Ordinal) ? "http" : "https"; // http only for test servers
        var url = $"{scheme}://{(string)link["hosts"]![0]!}{(string)link["path"]!}";
        using var response = await CloudHttp.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new BackupException($"pCloud: Herunterladen von {path} fehlgeschlagen ({(int)response.StatusCode}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        await using var file = File.Create(localFile);
        await response.Content.CopyToAsync(file, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var temp = Path.GetTempFileName();
        try
        {
            if (await CallAsync("stat", new() { ["path"] = Full(path) }, cancellationToken, FileMissing, FolderMissing, NotFound) is null)
            {
                return null;
            }

            await DownloadAsync(path, temp, cancellationToken);
            return await File.ReadAllTextAsync(temp, cancellationToken);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        var stat = await CallAsync("stat", new() { ["path"] = full }, cancellationToken, FileMissing, FolderMissing, NotFound);
        if (stat is null)
        {
            return;
        }

        var folder = (bool?)stat["metadata"]?["isfolder"] == true;
        await CallAsync(folder ? "deletefolderrecursive" : "deletefile", new() { ["path"] = full }, cancellationToken, FileMissing, FolderMissing, NotFound);
        if (folder)
        {
            foreach (var key in _createdFolders.Keys.Where(k => k == full || k.StartsWith(full + "/", StringComparison.Ordinal)))
            {
                _createdFolders.TryRemove(key, out _);
            }
        }
    }

    public sealed record ProbeResult(string Variant, int Size, bool Success, TimeSpan Duration, string? Detail);

    /// <summary>
    /// Connection test for the settings page: uploads test files of growing size in different ways, each only once (no retries),
    /// and removes them again – to find out where uploads break off.
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> ProbeAsync(CancellationToken cancellationToken)
    {
        var folder = Full("verbindungstest");
        await EnsureFolderAsync(folder, cancellationToken);
        var results = new List<ProbeResult>();
        var variants = new (string Name, Func<HttpClient, byte[], string, Task<JsonNode>> Send, bool FreshConnection)[]
        {
            ("uploadfile", (http, data, name) => SendOnceAsync(http, Upload(Multipart(folder, data, name)), cancellationToken), false),
            ("uploadfile, neue Verbindung", (http, data, name) => SendOnceAsync(http, Upload(Multipart(folder, data, name)), cancellationToken), true),
            ("uploadfile ohne 100-continue", (http, data, name) => SendOnceAsync(http, Multipart(folder, data, name), cancellationToken), false),
            ("upload_write", (http, data, name) => WriteOnceAsync(http, folder, data, name, cancellationToken), false),
        };
        foreach (var size in new[] { 1024, 32 * 1024, 128 * 1024, 512 * 1024, 2 * 1024 * 1024 })
        {
            var data = RandomNumberGenerator.GetBytes(size);
            foreach (var (name, send, fresh) in variants)
            {
                using var freshClient = fresh ? CloudHttp.CreateClient() : null;
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var json = await send(freshClient ?? _http, data, $"test-{size}-{results.Count}.bin");
                    var result = (int?)json["result"] ?? -1;
                    var stored = result == 0 && json["metadata"] is JsonArray { Count: > 0 } or JsonObject;
                    results.Add(new ProbeResult(name, size, stored, started.Elapsed,
                        stored ? null : result == 0 ? "pCloud meldet ok, hat aber keine Datei gespeichert" : $"{result}: {(string?)json["error"]}"));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BackupException)
                {
                    var reasons = new List<string>();
                    for (var e = ex; e is not null; e = e.InnerException)
                    {
                        if (!reasons.Contains(e.Message))
                        {
                            reasons.Add(e.Message);
                        }
                    }

                    results.Add(new ProbeResult(name, size, false, started.Elapsed, string.Join(" → ", reasons)));
                }
            }
        }

        await DeleteAsync("verbindungstest", cancellationToken);
        return results;
    }

    private HttpRequestMessage Multipart(string folder, byte[] data, string name)
    {
        var url = $"{_token.ApiUrl}/uploadfile?auth={Uri.EscapeDataString(_token.Auth)}&path={Uri.EscapeDataString(folder)}&nopartial=1";
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = FilePart(new ByteArrayContent(data), name) };
    }

    private async Task<JsonNode> WriteOnceAsync(HttpClient http, string folder, byte[] data, string name, CancellationToken cancellationToken)
    {
        var created = await CallAsync("upload_create", [], cancellationToken);
        var uploadId = ((long?)created!["uploadid"])?.ToString(CultureInfo.InvariantCulture);
        var url = $"{_token.ApiUrl}/upload_write?auth={Uri.EscapeDataString(_token.Auth)}&uploadid={uploadId}&uploadoffset=0";
        var written = await SendOnceAsync(http, Upload(new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(data) }), cancellationToken);
        if ((int?)written["result"] != 0)
        {
            return written;
        }

        return await CallAsync("upload_save", new() { ["uploadid"] = uploadId!, ["path"] = folder, ["name"] = name }, cancellationToken) ?? written;
    }

    private static async Task<JsonNode> SendOnceAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.SendAsync(request, timeout.Token);
            return await ReadAsync(response, timeout.Token);
        }
    }

    /// <summary>Free space of the account, for the settings page.</summary>
    public async Task<long?> FreeSpaceAsync(CancellationToken cancellationToken)
    {
        var info = await CallAsync("userinfo", [], cancellationToken);
        return (long?)info?["quota"] - (long?)info?["usedquota"];
    }

    public void Dispose() => _http.Dispose();

    // ---- Login ----

    /// <param name="CodeByEmail">
    /// True when pCloud only answered "Please provide 'code'" (1022), as it does for a login from a new device or place
    /// without two-factor authentication: the confirmation code then comes by e-mail. False: code from the authenticator app.
    /// </param>
    public sealed record LoginResult(PCloudToken? Token, bool NeedsCode, string? TwoFactorToken, bool CodeByEmail = false);

    /// <summary>
    /// Logs in with e-mail address and password. With two-factor authentication the first call returns
    /// <see cref="LoginResult.NeedsCode"/>; the second call passes the code and the returned token.
    /// </summary>
    public static async Task<LoginResult> LoginAsync(BackupOptions settings, string email, string password, string? code, string? twoFactorToken,
        CancellationToken cancellationToken)
    {
        var api = ApiUrl(settings);
        using var http = CloudHttp.CreateClient();
        var hasCode = !string.IsNullOrWhiteSpace(code);
        JsonNode json;
        if (!string.IsNullOrEmpty(twoFactorToken) && hasCode)
        {
            // Second step after "two-factor authentication required": the challenge token plus the code from the app.
            json = await PostAsync(http, $"{api}/tfa_login", Device(new()
            {
                ["token"] = twoFactorToken, ["code"] = code!.Trim(), ["trustdevice"] = "1",
            }), cancellationToken);
        }
        else
        {
            // "login" (as in pCloud's own SDK) answers accounts with two-factor authentication with a challenge token;
            // some answer "Please provide 'code'" instead, then the code goes along with the password.
            var parameters = Device(new() { ["username"] = email.Trim(), ["password"] = password });
            if (hasCode)
            {
                parameters["code"] = code!.Trim();
            }

            json = await PostAsync(http, $"{api}/login", parameters, cancellationToken);
            if (((int?)json["result"] is TwoFactorRequired or CodeMissing) && !hasCode) // the caller asks for the code and calls again
            {
                return new LoginResult(null, true, (string?)json["token"], (int?)json["result"] == CodeMissing);
            }
        }

        switch ((int?)json["result"])
        {
            case 0:
                var auth = (string?)json["auth"] ?? throw new BackupException("pCloud hat kein Zugangstoken geliefert.");
                return new LoginResult(new PCloudToken(auth, api, (string?)json["email"] ?? email.Trim()), false, null);
            case LoginFailed:
                throw new BackupException(
                    "pCloud: Anmeldung fehlgeschlagen. Stimmen E-Mail-Adresse und Passwort – und die Region (Konten aus Europa liegen auf „EU“)?");
            case CodeInvalid or CodeExpired or TwoFactorRequired or CodeMissing:
                throw new BackupException("pCloud: Der Code ist falsch oder abgelaufen – bitte noch einmal mit Passwort und neuem Code verbinden.");
            default:
                throw new BackupException($"pCloud: Anmeldung fehlgeschlagen ({(int?)json["result"]}: {(string?)json["error"] ?? json.ToJsonString()}).");
        }
    }

    /// <summary>Parameters every login call gets: an auth token in the answer, its lifetime and a device name.</summary>
    private static Dictionary<string, string> Device(Dictionary<string, string> parameters)
    {
        parameters["getauth"] = "1";
        parameters["logout"] = "1";
        parameters["device"] = $"Mailserver-Sicherung ({Environment.MachineName})";
        parameters["deviceid"] = $"Mailserver-Sicherung ({Environment.MachineName})";
        parameters["os"] = OperatingSystem.IsWindows() ? "5" : "7";
        return WithLifetime(parameters);
    }

    /// <summary>Longest token lifetime pCloud allows: 2 years, or 62 days without use (the backup uses it every night).</summary>
    private static Dictionary<string, string> WithLifetime(Dictionary<string, string> parameters)
    {
        parameters["authexpire"] = "63072000";
        parameters["authinactiveexpire"] = "5356800";
        return parameters;
    }

    // ---- internals ----

    /// <summary>
    /// Asks before sending the data ("Expect: 100-continue"). If pCloud refuses an upload, it answers right away and closes the
    /// connection; without asking first, the client is still sending and only sees "connection closed by the remote host"
    /// instead of pCloud's reason.
    /// </summary>
    private static HttpRequestMessage Upload(HttpRequestMessage request)
    {
        request.Headers.ExpectContinue = true;
        return request;
    }

    private string Full(string path) => path.Length == 0 ? _folder : $"{_folder}/{path.Trim('/')}";

    private async Task EnsureFolderAsync(string folder, CancellationToken cancellationToken)
    {
        if (_createdFolders.ContainsKey(folder))
        {
            return;
        }

        // Parents first; createfolderifnotexists does not create them.
        var path = "";
        foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            if (_createdFolders.ContainsKey(path))
            {
                continue;
            }

            await CallAsync("createfolderifnotexists", new() { ["path"] = path }, cancellationToken);
            _createdFolders[path] = true;
        }
    }

    /// <summary>Calls an API method; returns null if the result is one of <paramref name="missing"/>.</summary>
    private async Task<JsonNode?> CallAsync(string method, Dictionary<string, string> parameters, CancellationToken cancellationToken, params int[] missing)
    {
        parameters["auth"] = _token.Auth;
        var json = await PostAsync(_http, $"{_token.ApiUrl}/{method}", parameters, cancellationToken);
        var result = (int?)json["result"] ?? -1;
        if (missing.Contains(result))
        {
            return null;
        }

        Check(json, method);
        return json;
    }

    private static async Task<JsonNode> PostAsync(HttpClient http, string url, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var response = await CloudHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(parameters),
        }, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(body) ?? throw new BackupException($"pCloud: leere Antwort ({(int)response.StatusCode}).");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new BackupException($"pCloud: unerwartete Antwort ({(int)response.StatusCode}).");
        }
    }

    private static void Check(JsonNode json, string action)
    {
        var result = (int?)json["result"] ?? -1;
        if (result == 0)
        {
            return;
        }

        throw new BackupException(result switch
        {
            1000 or 2094 or 2095 => "pCloud: Die Anmeldung ist nicht mehr gültig – bitte unter Admin → Datensicherung neu verbinden.",
            2008 => "Der pCloud-Speicher ist voll.",
            _ => $"pCloud: {action} fehlgeschlagen ({result}: {(string?)json["error"]}).",
        });
    }

    private static IEnumerable<JsonNode> Contents(JsonNode? folder) =>
        folder?["contents"]?.AsArray().Where(c => c is not null).Select(c => c!) ?? [];
}
