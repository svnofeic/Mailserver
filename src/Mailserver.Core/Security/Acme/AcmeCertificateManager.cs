using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security.Acme;

/// <summary>Outcome of the last attempt, shown in the web interface and by "mailadmin tls".</summary>
public sealed record AcmeStatus(DateTimeOffset? LastAttempt, bool? Success, string? Message, DateTimeOffset? NotAfter,
    IReadOnlyList<string> Names, bool Staging)
{
    public static readonly AcmeStatus None = new(null, null, null, null, [], false);
}

/// <summary>
/// Issues and renews the server certificate with Let's Encrypt (http-01). The certificate is stored as
/// data\acme\certificate.pfx and picked up by <see cref="CertificateProvider"/> without a restart.
/// </summary>
public sealed partial class AcmeCertificateManager(
    IOptions<MailserverOptions> options,
    DataPaths paths,
    AcmeChallengeStore challenges,
    TimeProvider timeProvider,
    ILogger<AcmeCertificateManager> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>For tests: a handler that trusts a local ACME test server.</summary>
    public HttpMessageHandler? Handler { get; set; }

    /// <summary>Set when the web interface itself listens on the challenge port and answers from <see cref="AcmeChallengeStore"/>.</summary>
    public bool ChallengesServedByWeb { get; set; }

    public string CertificateFile => Path.Combine(paths.AcmeRoot, "certificate.pfx");

    private string StatusFile => Path.Combine(paths.AcmeRoot, "status.json");
    private string AccountKeyFile => Path.Combine(paths.AcmeRoot, "account-key.pem");
    private string AccountFile => Path.Combine(paths.AcmeRoot, "accounts.json");

    public bool IsRunning => _gate.CurrentCount == 0;

    public AcmeStatus Status
    {
        get
        {
            try
            {
                return File.Exists(StatusFile) ? JsonSerializer.Deserialize<AcmeStatus>(File.ReadAllText(StatusFile)) ?? AcmeStatus.None : AcmeStatus.None;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return AcmeStatus.None;
            }
        }
    }

    /// <summary>The issued certificate, or null. The caller owns the returned instance.</summary>
    public X509Certificate2? LoadCertificate()
    {
        try
        {
            return File.Exists(CertificateFile) ? X509CertificateLoader.LoadPkcs12FromFile(CertificateFile, null) : null;
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Could not read {File}", CertificateFile);
            return null;
        }
    }

    /// <summary>Why a new certificate is needed now, or null if the current one is fine (or Let's Encrypt is off).</summary>
    public string? RenewalReason()
    {
        var acme = options.Value.Tls.Acme;
        if (!acme.Enabled)
        {
            return null;
        }

        using var certificate = LoadCertificate();
        if (certificate is null)
        {
            return "noch kein Zertifikat";
        }

        var names = acme.EffectiveHostnames(options.Value.Hostname);
        if (names.Any(n => !certificate.MatchesHostname(n)))
        {
            return "Hostnamen geändert";
        }

        if (Status.Staging != acme.UseStaging && string.IsNullOrWhiteSpace(acme.DirectoryUrl))
        {
            return acme.UseStaging ? "Testmodus eingeschaltet" : "Testmodus ausgeschaltet";
        }

        var remaining = certificate.NotAfter.ToUniversalTime() - timeProvider.GetUtcNow().UtcDateTime;
        return remaining < TimeSpan.FromDays(acme.RenewDaysBefore) ? $"läuft in {Math.Max(0, (int)remaining.TotalDays)} Tagen ab" : null;
    }

    /// <summary>Requests a certificate for the configured names. Never throws; the result is also stored as <see cref="Status"/>.</summary>
    /// <param name="settings">Settings to use instead of the configured ones (just saved, not yet reloaded).</param>
    public async Task<AcmeStatus> IssueAsync(Action<string>? progress = null, CancellationToken cancellationToken = default,
        AcmeOptions? settings = null)
    {
        var acme = settings ?? options.Value.Tls.Acme;
        var names = acme.EffectiveHostnames(options.Value.Hostname);
        var staging = acme.UseStaging && string.IsNullOrWhiteSpace(acme.DirectoryUrl);
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new AcmeStatus(timeProvider.GetUtcNow(), false, "Es läuft bereits eine Ausstellung.", null, names, staging);
        }

        try
        {
            void Report(string text)
            {
                logger.LogInformation("ACME: {Step}", text);
                progress?.Invoke(text);
            }

            var notAfter = await RunAsync(acme, names, Report, cancellationToken);
            return Save(new AcmeStatus(timeProvider.GetUtcNow(), true,
                $"Zertifikat für {string.Join(", ", names)} ausgestellt, gültig bis {notAfter.ToLocalTime():dd.MM.yyyy}.", notAfter, names, staging));
        }
        catch (Exception ex) when (ex is AcmeException or HttpRequestException or IOException or CryptographicException
                                       or TaskCanceledException or UnauthorizedAccessException)
        {
            logger.LogWarning("ACME: issuing a certificate for {Names} failed: {Message}", string.Join(", ", names), ex.Message);
            var previous = Status;
            return Save(new AcmeStatus(timeProvider.GetUtcNow(), false, ex is TaskCanceledException ? "Zeitüberschreitung beim ACME-Server." : ex.Message,
                previous.NotAfter, previous.Names, previous.Staging));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DateTimeOffset> RunAsync(AcmeOptions acme, IReadOnlyList<string> names, Action<string> report, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            throw new AcmeException("Kein Hostname angegeben.");
        }

        if (names.FirstOrDefault(n => !HostnamePattern().IsMatch(n)) is { } invalid)
        {
            throw new AcmeException($"„{invalid}“ ist kein gültiger Hostname (Platzhalter * und IP-Adressen sind nicht möglich).");
        }

        Directory.CreateDirectory(paths.AcmeRoot);
        var directoryUrl = acme.EffectiveDirectoryUrl;
        using var accountKey = LoadOrCreateAccountKey();
        using var client = new AcmeClient(directoryUrl, accountKey, Handler);

        var accounts = LoadAccounts();
        client.AccountUrl = accounts[directoryUrl.ToString()]?.GetValue<string>();
        if (client.AccountUrl is null)
        {
            report("Konto bei Let's Encrypt anlegen");
            accounts[directoryUrl.ToString()] = await client.RegisterAsync(acme.Email, cancellationToken);
            SaveAccounts(accounts);
        }

        report($"Zertifikat bestellen für {string.Join(", ", names)}");
        string orderUrl;
        JsonObject order;
        try
        {
            (orderUrl, order) = await client.CreateOrderAsync(names, cancellationToken);
        }
        catch (AcmeException ex) when (ex.Type?.EndsWith(":accountDoesNotExist", StringComparison.Ordinal) == true)
        {
            accounts[directoryUrl.ToString()] = await client.RegisterAsync(acme.Email, cancellationToken);
            SaveAccounts(accounts);
            (orderUrl, order) = await client.CreateOrderAsync(names, cancellationToken);
        }

        var directory = string.IsNullOrWhiteSpace(acme.ChallengeDirectory) ? null : new AcmeChallengeDirectory(acme.ChallengeDirectory);
        var published = new List<string>();
        AcmeHttpChallengeServer? server = null;
        try
        {
            foreach (var authorizationUrl in order["authorizations"]?.AsArray().Select(a => a!.GetValue<string>()) ?? [])
            {
                var authorization = await client.GetAsync(authorizationUrl, cancellationToken);
                var name = authorization["identifier"]?["value"]?.GetValue<string>() ?? "?";
                if (authorization["status"]?.GetValue<string>() == "valid")
                {
                    continue;
                }

                var challenge = authorization["challenges"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "http-01") as JsonObject
                                ?? throw new AcmeException($"Für {name} bietet der ACME-Server keine http-01-Prüfung an.");
                var token = challenge["token"]?.GetValue<string>() ?? "";
                if (!TokenPattern().IsMatch(token))
                {
                    throw new AcmeException("Der ACME-Server hat ein ungültiges Token geschickt.");
                }

                var keyAuthorization = client.KeyAuthorization(token);
                if (directory is not null)
                {
                    directory.Write(token, keyAuthorization);
                }
                else
                {
                    challenges.Add(token, keyAuthorization);
                    server ??= ChallengesServedByWeb ? null : AcmeHttpChallengeServer.Start(challenges, acme.HttpPort);
                }

                published.Add(token);
                report($"Prüfung von {name} über http://{name}/.well-known/acme-challenge/…");
                await client.AcceptChallengeAsync(challenge["url"]!.GetValue<string>(), cancellationToken);
                await WaitForAsync(client, authorizationUrl, "valid", status =>
                {
                    var error = status["challenges"]?.AsArray().Select(c => c?["error"]?["detail"]?.GetValue<string>()).FirstOrDefault(d => d is not null);
                    return $"Prüfung von {name} fehlgeschlagen: {error ?? "unbekannter Grund"}. Ist {name} per DNS auf diesen Server " +
                           "gerichtet und Port 80 von außen erreichbar?";
                }, cancellationToken);
            }
        }
        finally
        {
            foreach (var token in published)
            {
                challenges.Remove(token);
                directory?.Delete(token);
            }

            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }

        report("Zertifikat abholen");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            alternativeNames.AddDnsName(name);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());
        await client.FinalizeAsync(order["finalize"]!.GetValue<string>(), request.CreateSigningRequest(), cancellationToken);
        var final = await WaitForAsync(client, orderUrl, "valid", _ => "Der ACME-Server hat die Bestellung abgelehnt.", cancellationToken);
        var pem = await client.DownloadCertificateAsync(final["certificate"]!.GetValue<string>(), cancellationToken);

        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(pem);
        using var leaf = chain[0].CopyWithPrivateKey(key);
        var export = new X509Certificate2Collection { leaf };
        export.AddRange(chain.Skip(1).ToArray());
        var temp = CertificateFile + ".tmp";
        await File.WriteAllBytesAsync(temp, export.Export(X509ContentType.Pkcs12)!, cancellationToken);
        File.Move(temp, CertificateFile, overwrite: true);
        InstallIntermediates(chain.Skip(1));
        return leaf.NotAfter.ToUniversalTime();
    }

    /// <summary>Polls an order or authorization until it reaches <paramref name="target"/>; "invalid" ends with an error.</summary>
    private static async Task<JsonObject> WaitForAsync(AcmeClient client, string url, string target, Func<JsonObject, string> failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            var current = await client.GetAsync(url, cancellationToken);
            var status = current["status"]?.GetValue<string>();
            if (status == target)
            {
                return current;
            }

            if (status == "invalid")
            {
                throw new AcmeException(failure(current));
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcmeException($"Der ACME-Server hat nach {PollTimeout.TotalSeconds:0} Sekunden noch nicht geantwortet (Status {status}).");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Windows builds the chain it sends to clients from its own stores; without the intermediate in "CA",
    /// some mail programs would see an incomplete chain.
    /// </summary>
    private void InstallIntermediates(IEnumerable<X509Certificate2> intermediates)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in intermediates)
            {
                store.Add(certificate);
            }
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Could not add the intermediate certificate to the Windows store");
        }
    }

    private ECDsa LoadOrCreateAccountKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(AccountKeyFile))
        {
            key.ImportFromPem(File.ReadAllText(AccountKeyFile));
        }
        else
        {
            File.WriteAllText(AccountKeyFile, key.ExportPkcs8PrivateKeyPem());
        }

        return key;
    }

    private JsonObject LoadAccounts()
    {
        try
        {
            return File.Exists(AccountFile) ? JsonNode.Parse(File.ReadAllText(AccountFile)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SaveAccounts(JsonObject accounts) => File.WriteAllText(AccountFile, accounts.ToJsonString(Json));

    private AcmeStatus Save(AcmeStatus status)
    {
        try
        {
            Directory.CreateDirectory(paths.AcmeRoot);
            File.WriteAllText(StatusFile, JsonSerializer.Serialize(status, Json));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write {File}", StatusFile);
        }

        return status;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,256}$")]
    private static partial Regex TokenPattern();
}
