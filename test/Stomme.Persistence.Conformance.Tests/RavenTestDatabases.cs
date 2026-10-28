using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// Creating a test's own database - SETUP, never the behaviour under test. BL-209 named its intermittent CI failure on
/// 2026-10-07 (10.18.0, failing twice on one commit): "An exception occurred while contacting .../admin/databases?name=..." - the
/// job-local RavenDB container, on a 2 GiB runner creating one database per test, failed to create one. So the creation is tried
/// a few times with a short pause; an assertion of the suite is never retried, and a server that cannot create a database at all
/// still fails the test (D46: fail, never skip).
/// </summary>
internal static class RavenTestDatabases
{
    public const int Attempts = 3;

    public static async Task CreateAsync(IDocumentStore store, string database)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(database)));
                return;
            }
            catch (ConcurrencyException)
            {
                return; // an earlier attempt created it after all, before its answer was lost
            }
            catch (RavenException) when (attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}
