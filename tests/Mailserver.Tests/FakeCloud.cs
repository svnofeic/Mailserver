using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mailserver.Tests;

/// <summary>Base for in-memory cloud storages on a local port: files by path ("folder/sub/file").</summary>
public abstract class FakeCloud : IAsyncDisposable
{
    private WebApplication _app = null!;

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, bool> Folders { get; } = new(StringComparer.Ordinal);
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}";
    public int Uploads;

    protected async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
    }

    protected abstract Task HandleAsync(HttpContext context);

    public IEnumerable<string> FilesBelow(string folder) => Files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal));

    protected void AddFolders(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            Folders[string.Join('/', parts.Take(i))] = true;
        }
    }

    protected void DeleteTree(string path)
    {
        foreach (var key in Files.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            Files.TryRemove(key, out _);
        }

        foreach (var key in Folders.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            Folders.TryRemove(key, out _);
        }
    }

    protected static Task JsonAsync(HttpContext context, object value, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(value));
    }

    protected static async Task<byte[]> BodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Microsoft identity platform (device code, refresh) and the parts of Microsoft Graph the backup uses.</summary>
public sealed class FakeOneDrive : FakeCloud
{
    private readonly ConcurrentDictionary<string, (string Path, MemoryStream Data)> _sessions = new();
    private int _pendingPolls = 1;
    private int _tokenCounter;

    /// <summary>Seconds the client should wait between polls (Microsoft: 5).</summary>
    public int Interval { get; set; }

    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";
    public int Refreshes;

    public static async Task<FakeOneDrive> CreateAsync()
    {
        var drive = new FakeOneDrive();
        await drive.StartAsync();
        return drive;
    }

    private void IssueTokens()
    {
        var n = Interlocked.Increment(ref _tokenCounter);
        AccessToken = $"access-{n}";
        RefreshToken = $"refresh-{n}";
    }

    protected override async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        if (path.EndsWith("/oauth2/v2.0/devicecode", StringComparison.Ordinal))
        {
            await JsonAsync(context, new { user_code = "ABCD-1234", device_code = "device-1", verification_uri = "https://microsoft.com/devicelogin",
                interval = Interval, expires_in = 900 });
            return;
        }

        if (path.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            var form = await context.Request.ReadFormAsync();
            if (form["grant_type"] == "urn:ietf:params:oauth:grant-type:device_code")
            {
                if (Interlocked.Decrement(ref _pendingPolls) >= 0)
                {
                    await JsonAsync(context, new { error = "authorization_pending" }, 400);
                    return;
                }
            }
            else if (form["refresh_token"] != RefreshToken)
            {
                await JsonAsync(context, new { error = "invalid_grant", error_description = "AADSTS70000: refresh token revoked" }, 400);
                return;
            }
            else
            {
                Interlocked.Increment(ref Refreshes);
            }

            IssueTokens();
            await JsonAsync(context, new { access_token = AccessToken, refresh_token = RefreshToken, expires_in = 3600 });
            return;
        }

        if (path.StartsWith("/upload/", StringComparison.Ordinal))
        {
            await UploadChunkAsync(context, path["/upload/".Length..]);
            return;
        }

        if (context.Request.Headers.Authorization != $"Bearer {AccessToken}")
        {
            await JsonAsync(context, new { error = new { code = "InvalidAuthenticationToken" } }, 401);
            return;
        }

        if (path == "/v1.0/me/drive")
        {
            await JsonAsync(context, new { owner = new { user = new { displayName = "Sven", email = "sven@outlook.test" } }, quota = new { remaining = 5L << 30 } });
            return;
        }

        const string prefix = "/v1.0/me/drive/root:/";
        var colon = path.IndexOf(':', prefix.Length);
        var item = path[prefix.Length..colon];
        var action = path[(colon + 1)..];
        switch (context.Request.Method, action)
        {
            case ("GET", "/children"):
                if (!Folders.ContainsKey(item))
                {
                    await JsonAsync(context, new { error = new { code = "itemNotFound" } }, 404);
                    return;
                }

                var children = Folders.Keys.Where(f => Parent(f) == item).Select(f => (object)new { name = Name(f), folder = new { } })
                    .Concat(Files.Where(f => Parent(f.Key) == item).Select(f => (object)new { name = Name(f.Key), size = f.Value.Length, file = new { } }))
                    .ToList();
                // Two pages, to exercise @odata.nextLink.
                var skip = int.TryParse(context.Request.Query["skip"], out var s) ? s : 0;
                var page = children.Skip(skip).Take(2).ToList();
                await JsonAsync(context, skip + 2 < children.Count
                    ? new Dictionary<string, object> { ["value"] = page, ["@odata.nextLink"] = $"{Url}{path}?skip={skip + 2}" }
                    : new Dictionary<string, object> { ["value"] = page });
                return;
            case ("PUT", "/content"):
                Files[item] = await BodyAsync(context);
                AddFolders(Parent(item));
                Interlocked.Increment(ref Uploads);
                await JsonAsync(context, new { name = Name(item) }, 201);
                return;
            case ("POST", "/createUploadSession"):
                var id = Guid.NewGuid().ToString("N");
                _sessions[id] = (item, new MemoryStream());
                await JsonAsync(context, new { uploadUrl = $"{Url}/upload/{id}" });
                return;
            case ("GET", "/content"):
                if (!Files.TryGetValue(item, out var content))
                {
                    await JsonAsync(context, new { error = new { code = "itemNotFound" } }, 404);
                    return;
                }

                // Like Graph: a redirect to a pre-authenticated download address.
                context.Response.Redirect($"{Url}/upload/download?item={Uri.EscapeDataString(item)}");
                return;
            case ("DELETE", ""):
                if (!Files.ContainsKey(item) && !Folders.ContainsKey(item))
                {
                    await JsonAsync(context, new { error = new { code = "itemNotFound" } }, 404);
                    return;
                }

                DeleteTree(item);
                context.Response.StatusCode = 204;
                return;
            default:
                await JsonAsync(context, new { error = new { code = "invalidRequest", message = $"{context.Request.Method} {action}" } }, 400);
                return;
        }
    }

    private async Task UploadChunkAsync(HttpContext context, string id)
    {
        if (id == "download")
        {
            if (context.Request.Headers.Authorization.Count > 0)
            {
                context.Response.StatusCode = 400; // the download address must be used without the token
                return;
            }

            await context.Response.Body.WriteAsync(Files[context.Request.Query["item"]!]);
            return;
        }

        if (context.Request.Headers.Authorization.Count > 0)
        {
            context.Response.StatusCode = 401; // upload URLs must not get the token
            return;
        }

        var (item, data) = _sessions[id];
        var range = context.Request.Headers.ContentRange.ToString(); // bytes a-b/total
        var total = long.Parse(range[(range.IndexOf('/') + 1)..]);
        var chunk = await BodyAsync(context);
        data.Write(chunk);
        if (data.Length < total)
        {
            await JsonAsync(context, new { nextExpectedRanges = new[] { $"{data.Length}-" } }, 202);
            return;
        }

        Files[item] = data.ToArray();
        AddFolders(Parent(item));
        Interlocked.Increment(ref Uploads);
        _sessions.TryRemove(id, out _);
        await JsonAsync(context, new { name = Name(item) }, 201);
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>The parts of the pCloud API the backup uses; paths start with "/".</summary>
public sealed class FakePCloud : FakeCloud
{
    public const string Password = "pcloud-passwort";
    public const string Auth = "auth-token-1";

    public static async Task<FakePCloud> CreateAsync()
    {
        var cloud = new FakePCloud();
        await cloud.StartAsync();
        return cloud;
    }

    protected override async Task HandleAsync(HttpContext context)
    {
        var method = context.Request.Path.Value!.TrimStart('/');
        if (method.StartsWith("dl/", StringComparison.Ordinal))
        {
            await context.Response.Body.WriteAsync(Files[Uri.UnescapeDataString(method[3..])]);
            return;
        }

        var parameters = new Dictionary<string, string>(context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()));
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var field in form)
            {
                parameters[field.Key] = field.Value.ToString();
            }

            if (method == "uploadfile")
            {
                await UploadAsync(context, parameters, form.Files);
                return;
            }
        }

        if (method is "login" && parameters.ContainsKey("username"))
        {
            var user = parameters["username"];
            var code = parameters.GetValueOrDefault("code");
            await JsonAsync(context, parameters["password"] != Password ? new { result = 2000, error = "Log in failed." }
                // two variants of accounts with two-factor authentication: challenge token, or the code along with the password
                : user.StartsWith("2fa", StringComparison.Ordinal) ? new { result = 2297, error = "2FA required.", token = "tfa-token" }
                : user.StartsWith("code", StringComparison.Ordinal) && code is null ? new { result = 1022, error = "Please provide 'code'." }
                : user.StartsWith("code", StringComparison.Ordinal) && code != "123456" ? new { result = 2012, error = "Invalid code." }
                : (object)new { result = 0, auth = Auth, email = user });
            return;
        }

        if (method == "tfa_login")
        {
            await JsonAsync(context, parameters["token"] == "tfa-token" && parameters["code"] == "123456"
                ? new { result = 0, auth = Auth, email = "2fa@pcloud.test" }
                : (object)new { result = 2064, error = "Invalid code." });
            return;
        }

        if (parameters.GetValueOrDefault("auth") != Auth)
        {
            await JsonAsync(context, new { result = 1000, error = "Log in required." });
            return;
        }

        var path = parameters.GetValueOrDefault("path", "").Trim('/');
        switch (method)
        {
            case "userinfo":
                await JsonAsync(context, new { result = 0, quota = 10L << 30, usedquota = 1L << 30 });
                return;
            case "createfolderifnotexists":
                if (path.Contains('/') && !Folders.ContainsKey(path[..path.LastIndexOf('/')]))
                {
                    await JsonAsync(context, new { result = 2005, error = "Directory does not exist." });
                    return;
                }

                Folders[path] = true;
                await JsonAsync(context, new { result = 0 });
                return;
            case "listfolder":
                if (!Folders.ContainsKey(path))
                {
                    await JsonAsync(context, new { result = 2005, error = "Directory does not exist." });
                    return;
                }

                await JsonAsync(context, new { result = 0, metadata = Listing(path, parameters.ContainsKey("recursive"), parameters.ContainsKey("nofiles")) });
                return;
            case "stat":
                await JsonAsync(context, Files.ContainsKey(path) ? new { result = 0, metadata = new { isfolder = false } }
                    : Folders.ContainsKey(path) ? new { result = 0, metadata = new { isfolder = true } }
                    : (object)new { result = 2009, error = "File not found." });
                return;
            case "deletefile":
                await JsonAsync(context, Files.TryRemove(path, out _) ? new { result = 0 } : (object)new { result = 2009, error = "File not found." });
                return;
            case "deletefolderrecursive":
                DeleteTree(path);
                await JsonAsync(context, new { result = 0 });
                return;
            case "getfilelink":
                await JsonAsync(context, Files.ContainsKey(path)
                    ? new { result = 0, hosts = new[] { $"127.0.0.1:{Port}" }, path = "/dl/" + Uri.EscapeDataString(path) }
                    : (object)new { result = 2009, error = "File not found." });
                return;
            default:
                await JsonAsync(context, new { result = 2001, error = $"Unknown method {method}." });
                return;
        }
    }

    private async Task UploadAsync(HttpContext context, Dictionary<string, string> parameters, IFormFileCollection files)
    {
        var folder = parameters.GetValueOrDefault("path", "").Trim('/');
        if (parameters.GetValueOrDefault("auth") != Auth || !Folders.ContainsKey(folder))
        {
            await JsonAsync(context, new { result = 2005, error = "Directory does not exist." });
            return;
        }

        foreach (var file in files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            Files[$"{folder}/{file.FileName}"] = buffer.ToArray();
            Interlocked.Increment(ref Uploads);
        }

        await JsonAsync(context, new { result = 0 });
    }

    private object Listing(string folder, bool recursive, bool noFiles) => new { name = folder, isfolder = true, contents = Contents(folder, recursive, noFiles) };

    private List<object> Contents(string folder, bool recursive, bool noFiles)
    {
        var contents = Folders.Keys.Where(f => f.Contains('/') && f[..f.LastIndexOf('/')] == folder)
            .Select(f => recursive
                ? (object)new { name = f[(f.LastIndexOf('/') + 1)..], isfolder = true, contents = Contents(f, true, noFiles) }
                : new { name = f[(f.LastIndexOf('/') + 1)..], isfolder = true })
            .ToList();
        if (!noFiles)
        {
            contents.AddRange(Files.Where(f => f.Key[..f.Key.LastIndexOf('/')] == folder)
                .Select(f => (object)new { name = f.Key[(f.Key.LastIndexOf('/') + 1)..], isfolder = false, size = f.Value.Length }));
        }

        return contents;
    }
}
