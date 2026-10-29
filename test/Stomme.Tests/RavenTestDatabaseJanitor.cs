using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Tests
{
    /// <summary>
    /// Deletes STALE test databases left on the local RavenDB by prior runs (a sweep once found 72 leftovers,
    /// 4.6 GB under C:\RavenDB\Data\Databases). The per-fixture <c>DisposeStoreAsync</c> hard-deletes correctly,
    /// but a KILLED run (Stop-Process during a drive, a crashed host, a debugger stop) never reaches teardown —
    /// so cleanup must be a mechanism closed by construction, not a politeness: each Raven fixture calls
    /// <see cref="SweepAsync"/> with its own prefix BEFORE creating its fresh database, deleting whatever a dead
    /// run left behind. Safe by the suites' execution model: test methods of one class run sequentially in one
    /// xUnit collection, and each prefix belongs to exactly one class, so any prefix-matching database present
    /// at initialization time can only be a leftover. Swept once per prefix per process (later fixture
    /// instances skip), and a sweep failure never fails the run — leftovers are a disk problem, not a
    /// correctness problem, and the next run gets another chance.
    /// </summary>
    internal static class RavenTestDatabaseJanitor
    {
        private static readonly ConcurrentDictionary<string, bool> SweptPrefixes = new();

        /// <summary>
        /// Hard-deletes every database on <paramref name="store"/>'s server whose name starts with
        /// <paramref name="prefix"/>. Returns the number of databases deleted (0 when this prefix was already
        /// swept this process, or none matched).
        /// </summary>
        public static async Task<int> SweepAsync(IDocumentStore store, string prefix)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            if (!SweptPrefixes.TryAdd(prefix, true))
            {
                return 0;
            }

            try
            {
                var names = await store.Maintenance.Server.SendAsync(
                    new GetDatabaseNamesOperation(0, int.MaxValue));
                var stale = names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                if (stale.Length > 0)
                {
                    await store.Maintenance.Server.SendAsync(
                        new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
                        {
                            DatabaseNames = stale,
                            HardDelete = true,
                        }));
                }

                return stale.Length;
            }
            catch
            {
                // A failed sweep must never fail the suite; the databases stay for the next run's attempt.
                return 0;
            }
        }

        /// <summary>
        /// Hard-deletes <paramref name="database"/> and every database named <c>{database}_…</c> - the siblings an
        /// installed host creates beside its own: the dedicated OpenIddict store (<c>{database}_OpenIddict</c>, derived by
        /// <c>OpenIddictConnectionStrings</c> when the authority is on) and a second tenant's database (named
        /// <c>{database}_second</c>). Deleting the one name alone left each of those behind for good. Returns how many
        /// were deleted. Throws on a server error - the caller decides whether a teardown may swallow it.
        /// </summary>
        public static int DeleteWithSiblings(IDocumentStore store, string database)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(database);

            var names = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
            var doomed = names
                .Where(name => name == database || name.StartsWith(database + "_", StringComparison.Ordinal))
                .ToArray();
            if (doomed.Length > 0)
            {
                store.Maintenance.Server.Send(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
                {
                    DatabaseNames = doomed,
                    HardDelete = true,
                }));
            }

            return doomed.Length;
        }

        /// <summary>Test seam: forget the once-per-process guard for a prefix so a test can re-sweep.</summary>
        internal static void ResetForTesting(string prefix) => SweptPrefixes.TryRemove(prefix, out _);
    }
}
