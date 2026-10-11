using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mailserver.Core.Accounts;
using Mailserver.Core.Backup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.External;

/// <summary>
/// Outlook.com, Hotmail and Live no longer accept passwords for IMAP/SMTP, only OAuth 2. As for OneDrive, the user confirms
/// a code on microsoft.com/devicelogin once; the server keeps the refresh token (encrypted) and gets a fresh access token
/// for each connection.
/// </summary>
public sealed class MicrosoftMailAuth(ExternalAccountStore store, IOptions<MailserverOptions> options, ILogger<MicrosoftMailAuth> logger)
{
    public const string Scope = "offline_access https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";

    private static readonly HttpClient Http = CloudHttp.CreateClient();
    private readonly ConcurrentDictionary<long, (string Token, DateTimeOffset Expires)> _access = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public string? ClientId => options.Value.External.EffectiveMicrosoftClientId(options.Value.Backup);

    private string Endpoint(string path) =>
        $"{(options.Value.External.MicrosoftLoginUrl ?? "https://login.microsoftonline.com").TrimEnd('/')}/{options.Value.External.MicrosoftTenant}/oauth2/v2.0/{path}";

    public async Task<DeviceLogin> RequestCodeAsync(CancellationToken cancellationToken)
    {
        var clientId = ClientId ?? throw new ExternalMailException(
            "Für Outlook.com/Hotmail braucht der Server eine App-Registrierung bei Microsoft – ein Administrator trägt sie unter Fremde Konten ein.");
        var json = await PostAsync("devicecode", new() { ["client_id"] = clientId, ["scope"] = Scope }, cancellationToken);
        if (json["device_code"] is null)
        {
            var reason = Describe(json);
            throw new ExternalMailException($"Microsoft lehnt die Anmeldung ab: {reason}" + Hint(reason));
        }

        return new DeviceLogin((string)json["user_code"]!, (string)json["verification_uri"]!, (string)json["device_code"]!,
            TimeSpan.FromSeconds((int?)json["interval"] ?? 5), DateTimeOffset.UtcNow.AddSeconds((int?)json["expires_in"] ?? 900));
    }

    /// <summary>Waits until the user has confirmed the code; returns the refresh and a first access token.</summary>
    public async Task<(string Refresh, string Access)> WaitForTokenAsync(DeviceLogin login, CancellationToken cancellationToken)
    {
        var interval = login.Interval;
        while (DateTimeOffset.UtcNow < login.Expires)
        {
            await Task.Delay(interval, cancellationToken);
            var json = await PostAsync("token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId!,
                ["device_code"] = login.DeviceCode,
            }, cancellationToken);
            switch ((string?)json["error"])
            {
                case null when json["refresh_token"] is not null && json["access_token"] is not null:
                    return ((string)json["refresh_token"]!, (string)json["access_token"]!);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined" or "access_denied":
                    throw new ExternalMailException("Die Anmeldung bei Microsoft wurde abgelehnt.");
                default:
                    throw new ExternalMailException($"Anmeldung bei Microsoft fehlgeschlagen: {Describe(json)}{Hint(Describe(json))}");
            }
        }

        throw new ExternalMailException("Der Code ist abgelaufen – bitte noch einmal anmelden.");
    }

    /// <summary>An access token for IMAP/SMTP of the account; refreshed with the stored refresh token when needed.</summary>
    public async Task<string> AccessTokenAsync(ExternalAccount account, CancellationToken cancellationToken)
    {
        if (_access.TryGetValue(account.Id, out var cached) && DateTimeOffset.UtcNow < cached.Expires)
        {
            return cached.Token;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (_access.TryGetValue(account.Id, out cached) && DateTimeOffset.UtcNow < cached.Expires)
            {
                return cached.Token;
            }

            var refresh = store.Password(account.Id);
            if (refresh is null || ClientId is not { } clientId)
            {
                throw new ExternalMailException(Expired);
            }

            var json = await PostAsync("token", new()
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refresh,
                ["scope"] = Scope,
            }, cancellationToken);
            if (json["access_token"] is null)
            {
                logger.LogWarning("Microsoft refused the refresh token of {Address}: {Error}", account.Address, Describe(json));
                throw new ExternalMailException($"{Expired} ({Describe(json)})");
            }

            if ((string?)json["refresh_token"] is { } renewed && renewed != refresh)
            {
                // Microsoft hands out a new refresh token now and then; the old one expires eventually.
                store.SetSecret(account.Id, renewed);
            }

            var token = (string)json["access_token"]!;
            _access[account.Id] = (token, DateTimeOffset.UtcNow.AddSeconds(((int?)json["expires_in"] ?? 3600) - 300));
            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Drops a cached access token (e.g. after the provider refused it), so the next connection gets a new one.</summary>
    public void Forget(long accountId) => _access.TryRemove(accountId, out _);

    public const string Expired = "Die Anmeldung bei Microsoft ist abgelaufen oder wurde widerrufen – bitte unter Fremde Konten „Neu anmelden“.";

    private async Task<JsonNode> PostAsync(string path, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await CloudHttp.SendAsync(Http, () => new HttpRequestMessage(HttpMethod.Post, Endpoint(path))
            {
                Content = new FormUrlEncodedContent(form),
            }, cancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ExternalMailException($"Microsoft ist gerade nicht erreichbar ({ex.Message}).");
        }
    }

    /// <summary>What to change in the app registration for the usual setup mistakes.</summary>
    public static string Hint(string reason) => reason switch
    {
        _ when reason.Contains("AADSTS70002", StringComparison.Ordinal) =>
            " – In der App-Registrierung unter „Authentifizierung“ die Einstellung „Öffentliche Clientflows zulassen“ auf „Ja“ stellen, " +
            "speichern und nach ein bis zwei Minuten erneut versuchen.",
        _ when reason.Contains("AADSTS700016", StringComparison.Ordinal) =>
            " – Die Anwendungs-ID ist falsch, oder die App-Registrierung erlaubt keine persönlichen Microsoft-Konten " +
            "(Kontotypen: „… und persönliche Microsoft-Konten“).",
        _ when reason.Contains("AADSTS50020", StringComparison.Ordinal) || reason.Contains("AADSTS50194", StringComparison.Ordinal) =>
            " – Die App-Registrierung muss bei den Kontotypen „Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten“ erlauben.",
        _ => "",
    };

    private static string Describe(JsonNode json)
    {
        var text = (string?)json["error_description"] ?? (string?)json["error"] ?? json.ToJsonString();
        // Microsoft appends trace and correlation ids on new lines.
        text = text.Split('\n')[0].Trim();
        return text.Length > 300 ? text[..300] + " …" : text;
    }
}

/// <summary>A sign-in at Microsoft that the user still has to confirm (shown on the page until done).</summary>
public sealed class MicrosoftSignIn
{
    public required string Address { get; init; }
    public required DeviceLogin Login { get; init; }
    public bool Done { get; set; }
    public string? Error { get; set; }
    public string? Result { get; set; }
}

/// <summary>Runs the sign-in for Outlook.com addresses of a mailbox: code, waiting, connection test, saving.</summary>
public sealed class MicrosoftSignIns(MicrosoftMailAuth auth, ExternalAccountStore store, IExternalMail client, ILogger<MicrosoftSignIns> logger)
{
    private readonly ConcurrentDictionary<long, MicrosoftSignIn> _pending = new();

    /// <summary>The sign-in of the mailbox that is waiting or just finished; a finished one is handed out once.</summary>
    public MicrosoftSignIn? Take(long ownerId)
    {
        if (!_pending.TryGetValue(ownerId, out var signIn))
        {
            return null;
        }

        if (signIn.Done || DateTimeOffset.UtcNow > signIn.Login.Expires.AddMinutes(1))
        {
            _pending.TryRemove(ownerId, out _);
        }

        return signIn;
    }

    /// <param name="existingId">Signing in again for an address already set up (the refresh token was revoked).</param>
    public async Task<MicrosoftSignIn> StartAsync(Account owner, ExternalAccountSettings settings, bool fetchExisting, long? existingId,
        CancellationToken cancellationToken)
    {
        var login = await auth.RequestCodeAsync(cancellationToken);
        var signIn = new MicrosoftSignIn { Address = settings.Address, Login = login };
        _pending[owner.Id] = signIn;
        _ = Task.Run(() => CompleteAsync(owner, settings, fetchExisting, existingId, signIn), CancellationToken.None);
        return signIn;
    }

    private async Task CompleteAsync(Account owner, ExternalAccountSettings settings, bool fetchExisting, long? existingId, MicrosoftSignIn signIn)
    {
        using var timeout = new CancellationTokenSource(signIn.Login.Expires - DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        try
        {
            var (refresh, access) = await auth.WaitForTokenAsync(signIn.Login, timeout.Token);
            if (await client.TestAsync(settings, access, timeout.Token) is { } error)
            {
                signIn.Error = error;
                return;
            }

            if (existingId is { } id)
            {
                store.Update(owner, id, settings, null);
                store.SetSecret(id, refresh);
                auth.Forget(id);
                signIn.Result = $"{settings.Address} ist wieder bei Microsoft angemeldet.";
            }
            else
            {
                var saved = store.Add(owner, settings, refresh, fetchExisting);
                if (!fetchExisting)
                {
                    await client.FetchAsync(saved, timeout.Token);
                }

                signIn.Result = $"{saved.Address} eingerichtet. Neue Mails erscheinen im Ordner „{saved.Settings.Folder}“ – abgerufen wird alle paar Minuten.";
            }
        }
        catch (Exception ex) when (ex is ExternalMailException or ArgumentException or OperationCanceledException)
        {
            signIn.Error = ex is OperationCanceledException ? "Der Code ist abgelaufen – bitte noch einmal anmelden." : ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Microsoft sign-in for {Address} failed", settings.Address);
            signIn.Error = "Die Anmeldung ist mit einem unerwarteten Fehler abgebrochen (siehe Protokoll des Servers).";
        }
        finally
        {
            signIn.Done = true;
        }
    }
}
