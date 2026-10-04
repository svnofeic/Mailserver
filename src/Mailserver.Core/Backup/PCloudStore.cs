using System.Collections.Concurrent;
using System.Net.Http.Headers;
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
    private const int TwoFactorRequired = 2297;

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

    public string Key => $"pcloud:{_token.Account}:{_folder}".ToLowerInvariant();

    public static string ApiUrl(BackupOptions settings) =>
        (settings.PCloudApiUrl ?? (string.Equals(settings.PCloudRegion, "US", StringComparison.OrdinalIgnoreCase)
            ? "https://api.pcloud.com"
            : "https://eapi.pcloud.com")).TrimEnd('/');

    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var json = await CallAsync("listfolder", new() { ["path"] = Full(folder), ["nofiles"] = "1" }, cancellationToken, FolderMissing);
        return json is null ? [] : Contents(json["metadata"]).Where(c => (bool?)c["isfolder"] == true).Select(c => (string)c["name"]!).ToList();
    }

    public async Task<IReadOnlyList<StoredFile>> ListFilesAsync(string folder, CancellationToken cancellationToken)
    {
        var json = await CallAsync("listfolder", new() { ["path"] = Full(folder), ["recursive"] = "1" }, cancellationToken, FolderMissing);
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
        var full = Full(path);
        var folder = full[..full.LastIndexOf('/')];
        await EnsureFolderAsync(folder, cancellationToken);

        var name = full[(full.LastIndexOf('/') + 1)..];
        var url = $"{_token.ApiUrl}/uploadfile?auth={Uri.EscapeDataString(_token.Auth)}&path={Uri.EscapeDataString(folder)}&nopartial=1";
        using var response = await CloudHttp.SendAsync(_http, () =>
        {
            var content = new MultipartFormDataContent();
            var file = new StreamContent(File.OpenRead(localFile));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "file", name); // the name of the part is the file name in pCloud; an existing file is replaced
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        }, cancellationToken);
        Check(await ReadAsync(response, cancellationToken), $"Hochladen von {path}");
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
            if (await CallAsync("stat", new() { ["path"] = Full(path) }, cancellationToken, FileMissing, FolderMissing) is null)
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
        var stat = await CallAsync("stat", new() { ["path"] = full }, cancellationToken, FileMissing, FolderMissing);
        if (stat is null)
        {
            return;
        }

        var folder = (bool?)stat["metadata"]?["isfolder"] == true;
        await CallAsync(folder ? "deletefolderrecursive" : "deletefile", new() { ["path"] = full }, cancellationToken, FileMissing, FolderMissing);
        if (folder)
        {
            foreach (var key in _createdFolders.Keys.Where(k => k == full || k.StartsWith(full + "/", StringComparison.Ordinal)))
            {
                _createdFolders.TryRemove(key, out _);
            }
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

    public sealed record LoginResult(PCloudToken? Token, bool NeedsCode, string? TwoFactorToken);

    /// <summary>
    /// Logs in with e-mail address and password. With two-factor authentication the first call returns
    /// <see cref="LoginResult.NeedsCode"/>; the second call passes the code and the returned token.
    /// </summary>
    public static async Task<LoginResult> LoginAsync(BackupOptions settings, string email, string password, string? code, string? twoFactorToken,
        CancellationToken cancellationToken)
    {
        var api = ApiUrl(settings);
        using var http = CloudHttp.CreateClient();
        JsonNode json;
        if (twoFactorToken is not null && !string.IsNullOrWhiteSpace(code))
        {
            json = await PostAsync(http, $"{api}/tfa_login", WithLifetime(new()
            {
                ["token"] = twoFactorToken, ["code"] = code.Trim(), ["trustdevice"] = "1", ["getauth"] = "1",
            }), cancellationToken);
        }
        else
        {
            json = await PostAsync(http, $"{api}/userinfo", WithLifetime(new()
            {
                ["getauth"] = "1", ["logout"] = "1", ["username"] = email.Trim(), ["password"] = password, ["device"] = "Mailserver-Sicherung",
            }), cancellationToken);
            if ((int?)json["result"] == TwoFactorRequired)
            {
                return new LoginResult(null, true, (string?)json["token"]);
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
            default:
                throw new BackupException($"pCloud: Anmeldung fehlgeschlagen ({(string?)json["error"] ?? json.ToJsonString()}).");
        }
    }

    /// <summary>Longest token lifetime pCloud allows: 2 years, or 62 days without use (the backup uses it every night).</summary>
    private static Dictionary<string, string> WithLifetime(Dictionary<string, string> parameters)
    {
        parameters["authexpire"] = "63072000";
        parameters["authinactiveexpire"] = "5356800";
        return parameters;
    }

    // ---- internals ----

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
