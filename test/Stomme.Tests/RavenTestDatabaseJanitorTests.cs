using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The janitor's contract: a database left behind by a KILLED prior run — the case
    /// per-fixture teardown structurally cannot cover — is hard-deleted by the NEXT run's fixture
    /// initialization. Runs against the LIVE local RavenDB and FAILS (not skips) when it is unreachable:
    /// a janitor that silently never runs is how 4.6 GB of leftovers accumulated.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenTestDatabaseJanitorTests : IAsyncLifetime
    {
        private const string Prefix = "stomme-janitor-selftest-";

        private static readonly string Url =
            Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private IDocumentStore _store = default!;

        public Task InitializeAsync()
        {
            _store = new DocumentStore { Urls = new[] { Url } }.Initialize();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            // Belt and braces: the test itself must not become a leaker.
            RavenTestDatabaseJanitor.ResetForTesting(Prefix);
            await RavenTestDatabaseJanitor.SweepAsync(_store, Prefix);
            _store.Dispose();
        }

        [Fact]
        public async Task A_database_left_by_a_dead_run_is_swept_on_the_next_initialization()
        {
            // Simulate the dead run: a prefix-matching database exists and nobody's teardown owns it.
            var stale = Prefix + Guid.NewGuid().ToString("N");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(stale)));

            RavenTestDatabaseJanitor.ResetForTesting(Prefix);
            var swept = await RavenTestDatabaseJanitor.SweepAsync(_store, Prefix);

            Assert.True(swept >= 1, $"expected the janitor to sweep at least the staged stale database, swept {swept}");
            var names = await _store.Maintenance.Server.SendAsync(new GetDatabaseNamesOperation(0, int.MaxValue));
            Assert.DoesNotContain(stale, names);

            // The once-per-process guard: a second call is a no-op even with a fresh stale database present.
            var stale2 = Prefix + Guid.NewGuid().ToString("N");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(stale2)));
            var secondSweep = await RavenTestDatabaseJanitor.SweepAsync(_store, Prefix);
            Assert.Equal(0, secondSweep);
            names = await _store.Maintenance.Server.SendAsync(new GetDatabaseNamesOperation(0, int.MaxValue));
            Assert.Contains(stale2, names); // guard held; DisposeAsync sweeps it
        }
    }
}
