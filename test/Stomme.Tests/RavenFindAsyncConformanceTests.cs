using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Tests
{
    /// <summary>
    /// The SAME provider-neutral FindAsync conformance suite against a LIVE RavenDB document store, each run in its own
    /// database (<c>RAVENDB_TEST_URL</c>, default http://127.0.0.1:7000). Composed the way a RavenDB install
    /// composes a clean-arch module: <c>AddRavenFrameworkStore</c> supplies the host's Raven singletons, then a
    /// helper selects the Raven branch and binds the closed <c>IRepository&lt;ProbeAggregate, Guid&gt;</c> to
    /// <c>RavenRepository</c>.
    /// <para>
    /// Like the parity suite this FAILS rather than skips when RavenDB is unreachable: an EF-only run proves
    /// nothing about the both-providers guarantee FindAsync exists to hold. Its bare <c>==</c> on a string is
    /// case-INSENSITIVE, which the string-boundary test pins as the divergence from EF.
    /// </para>
    /// </summary>
    [Xunit.Trait("Category", "ExternalDatabase")]
    public sealed class RavenFindAsyncConformanceTests : FindAsyncConformanceTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-findasync-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override bool StringEqualityIsCaseInsensitive => true;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            // A KILLED prior run never reached DisposeStoreAsync — sweep its leftovers before creating ours
            // (once per process; the mechanism that keeps C:\RavenDB\Data\Databases from eating the disk).
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-findasync-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = RavenType,
                })
                .Build());

            services.AddRavenFrameworkStore(_store);

            services.AddModuleAggregatePersistence<ProbeAggregate, Guid, ProbeDbContext>(_ => { });
        }

        protected override async Task DisposeStoreAsync()
        {
            // The container's DisposeAsync ran first and already killed the shared store's operation executor —
            // deleting through _store throws OperationCanceledException EVERY time (swallowed for months; the
            // actual source of the C:\RavenDB leftovers, alongside killed runs). Delete through a FRESH store,
            // the same pattern RavenInstalledSiteRenderTests.BestEffortDropAsync uses.
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: an unreachable server leaves the database for the next run's janitor sweep
            }
        }
    }
}
