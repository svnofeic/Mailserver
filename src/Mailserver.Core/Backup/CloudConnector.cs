using Microsoft.Extensions.Logging;

namespace Mailserver.Core.Backup;

/// <summary>
/// Connecting the cloud storages from the web interface: keeps a pending OneDrive sign-in (the server waits in the
/// background until the code was confirmed) and a pending pCloud two-factor login between two requests.
/// </summary>
public sealed class CloudConnector(CloudTokens tokens, ILogger<CloudConnector> logger)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _oneDriveWait;

    public DeviceLogin? PendingOneDrive { get; private set; }

    /// <summary>Result of the last OneDrive sign-in that was waited for: error text, or null.</summary>
    public string? OneDriveError { get; private set; }

    /// <summary>pCloud asked for the two-factor code; the token of the first login step.</summary>
    public (string Email, string Token)? PendingPCloudCode { get; private set; }

    public bool IsConnected(string provider) => provider == OneDriveStore.Provider
        ? tokens.Load<OneDriveToken>(provider) is not null
        : tokens.Load<PCloudToken>(provider) is not null;

    public string? Account(string provider) => provider == OneDriveStore.Provider
        ? tokens.Load<OneDriveToken>(provider)?.Account
        : tokens.Load<PCloudToken>(provider)?.Account;

    /// <summary>Requests a code from Microsoft and waits in the background until it was entered (or expired).</summary>
    public async Task<DeviceLogin> StartOneDriveAsync(BackupOptions settings, CancellationToken stopping)
    {
        var login = await OneDriveStore.StartLoginAsync(settings, stopping);
        CancellationTokenSource wait;
        lock (_lock)
        {
            _oneDriveWait?.Cancel();
            _oneDriveWait = wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            PendingOneDrive = login;
            OneDriveError = null;
        }

        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                var account = await OneDriveStore.CompleteLoginAsync(settings, login, tokens, wait.Token);
                logger.LogInformation("Connected to OneDrive as {Account}", account);
            }
            catch (BackupException ex)
            {
                error = ex.Message;
            }
            catch (HttpRequestException ex)
            {
                error = $"Verbindungsfehler: {ex.Message}";
            }
            catch (OperationCanceledException)
            {
                return; // replaced by a newer sign-in or the service stops
            }

            lock (_lock)
            {
                if (ReferenceEquals(_oneDriveWait, wait))
                {
                    PendingOneDrive = null;
                    OneDriveError = error;
                }
            }
        }, CancellationToken.None);
        return login;
    }

    /// <summary>Logs in to pCloud. Returns false if the two-factor code is needed (then call again with it).</summary>
    public async Task<bool> ConnectPCloudAsync(BackupOptions settings, string email, string password, string? code, CancellationToken cancellationToken)
    {
        var pending = PendingPCloudCode;
        var result = await PCloudStore.LoginAsync(settings, email, password, code,
            pending is { } p && p.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) ? p.Token : null, cancellationToken);
        if (result.NeedsCode)
        {
            PendingPCloudCode = (email.Trim(), result.TwoFactorToken ?? "");
            return false;
        }

        tokens.Save(PCloudStore.Provider, result.Token!);
        PendingPCloudCode = null;
        return true;
    }

    public void Disconnect(string provider)
    {
        tokens.Delete(provider);
        lock (_lock)
        {
            if (provider == OneDriveStore.Provider)
            {
                _oneDriveWait?.Cancel();
                PendingOneDrive = null;
                OneDriveError = null;
            }
            else
            {
                PendingPCloudCode = null;
            }
        }
    }
}
