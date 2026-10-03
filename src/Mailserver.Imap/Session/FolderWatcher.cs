using System.Collections.Concurrent;
using Mailserver.Core.Storage;

namespace Mailserver.Imap.Session;

/// <summary>
/// Wakes IDLE sessions when a folder changes (new mail, flags changed by another client, expunges).
/// </summary>
public sealed class FolderWatcher
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _waiters = new();

    public FolderWatcher(MailboxStore mailboxes) => mailboxes.FolderChanged += Notify;

    /// <summary>Completes on the next change of <paramref name="folderId"/>. Obtain it before syncing to not miss changes.</summary>
    public Task WaitAsync(long folderId) =>
        _waiters.GetOrAdd(folderId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void Notify(long folderId)
    {
        if (_waiters.TryRemove(folderId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }
}
