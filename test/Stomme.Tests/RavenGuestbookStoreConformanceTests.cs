using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Domain;
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
    /// The SAME Guestbook conformance suite against a LIVE RavenDB document store, each run in its own
    /// database (<c>RAVENDB_TEST_URL</c>, default http://127.0.0.1:7000). Composed the way a RavenDB install
    /// composes the module: <c>AddRavenFrameworkStore</c> supplies the host's Raven singletons in the
    /// composition root, then the D76 helper selects the Raven branch so the closed
    /// <see cref="IGuestbookRepository"/> delegates to a <c>RavenRepository</c> — so the guestbook keeps its
    /// entries in the install's document store, not a stray SQLite file. Since D78 the single provider-neutral
    /// <see cref="GuestbookRepository"/> serves BOTH providers (its by-module read goes through
    /// <c>IRepository.FindAsync</c>), so this run and the EF run register the SAME repository type.
    /// <para>
    /// Like the D46 parity suite this FAILS rather than skips when RavenDB is unreachable: an EF-only run proves
    /// nothing about the both-providers guarantee this exists to hold. This is the suite that DRIVES the
    /// <see cref="AuthorName"/> value object round-trip on a real server.
    /// </para>
    /// </summary>
    [Xunit.Trait("Category", "ExternalDatabase")]
    public sealed class RavenGuestbookStoreConformanceTests : GuestbookStoreConformanceTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-guestbook-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            // A KILLED prior run never reached DisposeStoreAsync — sweep its leftovers before creating ours
            // (once per process; the mechanism that keeps C:\RavenDB\Data\Databases from eating the disk).
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-guestbook-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        /// <summary>BL-150, measured.</summary>

        protected override bool ValueObjectEqualityIsCaseInsensitive => true;


        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = RavenType,
                })
                .Build());

            // The host's Raven support singletons (IDocumentStore, id generator, tenant-db resolver, change
            // vector tracker, aggregate tracker) — registered in the composition root before any module startup.
            services.AddRavenFrameworkStore(_store);

            // The STORAGE-BLIND overload: the Raven branch ignores any relational context/connection and
            // binds the generic RavenRepository, so the module authors zero EF code on this provider too.
            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            services.AddScoped<IGuestbookRepository, GuestbookRepository>();
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
