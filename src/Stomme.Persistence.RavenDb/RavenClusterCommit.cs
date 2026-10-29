using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// An idempotent cluster operation (creating a database, deploying an index) whose commit notification was
/// lost. RavenDB 7.2.5 was measured answering "Waited for 00:00:15 for task with index 38787 ... Last commit index
/// is: 38789. Number of errors is: 0" - the raft log had committed PAST the awaited index and the waiter never woke
/// (a missed wakeup in the server's index notifications). A longer budget cannot fix a lost notification; a recheck
/// can. So a timeout-shaped failure is followed by asking whether the operation's effect is there: if it is, the
/// operation completed and the call succeeds; if not, the operation runs ONCE more and a second failure propagates.
/// Any other failure propagates at once. Only for operations that are safe to repeat - never a document write.
/// </summary>
public static class RavenClusterCommit
{
    /// <summary>Runs <paramref name="operation"/>; after a commit-wait timeout, succeeds if <paramref name="completed"/> says the effect is present, else runs it once more.</summary>
    public static async Task RunAsync(Func<Task> operation, Func<Task<bool>> completed)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(completed);

        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCommitWaitTimeout(exception))
        {
            if (await completed().ConfigureAwait(false))
            {
                return;
            }

            await operation().ConfigureAwait(false);
        }
    }

    /// <summary>Deploys <paramref name="index"/> to <paramref name="database"/>, rechecked as <see cref="RunAsync"/> describes.</summary>
    public static Task EnsureIndexAsync(IDocumentStore store, AbstractIndexCreationTask index, string database, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);

        return RunAsync(
            () => index.ExecuteAsync(store, store.Conventions, database, ct),
            async () => await store.Maintenance.ForDatabase(database)
                .SendAsync(new GetIndexOperation(index.IndexName), ct)
                .ConfigureAwait(false) is not null);
    }

    /// <summary>True when the failure is the client giving up waiting - a <see cref="RavenTimeoutException"/> or a <see cref="TimeoutException"/> anywhere in the chain.</summary>
    public static bool IsCommitWaitTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RavenTimeoutException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
