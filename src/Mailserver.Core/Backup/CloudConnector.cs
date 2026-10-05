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

    /// <summary>pCloud asked for the two-factor code: e-mail address of the pending login.</summary>
    public string? PendingPCloudEmail => _pendingPCloud?.Email;

    /// <summary>The pending code is a confirmation code pCloud sent by e-mail (not one from an authenticator app).</summary>
    public bool PendingPCloudCodeByEmail { get; private set; }

    // Token of the first login step (may be empty) and the password, kept in memory only until the code was entered.
    private (string Email, string Password, string? Token)? _pendingPCloud;

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
        var pending = _pendingPCloud is { } p && p.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) ? p : ((string, string, string?)?)null;
        if (string.IsNullOrEmpty(password) && pending is { } earlier)
        {
            password = earlier.Item2; // second step: only the code was entered
        }

        PCloudStore.LoginResult result;
        try
        {
            result = await PCloudStore.LoginAsync(settings, email, password, code, pending?.Item3, cancellationToken);
        }
        catch (BackupException)
        {
            _pendingPCloud = null;
            throw;
        }

        if (result.NeedsCode)
        {
            _pendingPCloud = (email.Trim(), password, result.TwoFactorToken);
            PendingPCloudCodeByEmail = result.CodeByEmail;
            return false;
        }

        tokens.Save(PCloudStore.Provider, result.Token!);
        _pendingPCloud = null;
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
                _pendingPCloud = null;
            }
        }
    }
}
