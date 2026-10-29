using System;
using System.Collections.Generic;
using System.IO;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests
{
    /// <summary>
    /// The Guestbook conformance suite against the RELATIONAL (EF Core) store — a throwaway SQLite FILE (not
    /// <c>:memory:</c>, which would give each per-scope connection its own empty database), composed exactly as
    /// the module's <c>ServerStartup</c> composes it on a relational install: the shared helper selects the EF
    /// branch from the configured provider, and the EF <see cref="GuestbookRepository"/> is the closed
    /// <see cref="IGuestbookRepository"/>. This is the baseline the RavenDB run must match member for member.
    /// </summary>
    public sealed class EfGuestbookStoreConformanceTests : GuestbookStoreConformanceTests
    {
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"stomme-guestbook-conformance-{Guid.NewGuid():N}.db");

        /// <summary>The one string both the store and the teardown use — the pool is keyed by it.</summary>
        private string ConnectionString => $"Data Source={_databasePath}";

        /// <summary>Value-object equality is case-sensitive on this store.</summary>

        protected override bool ValueObjectEqualityIsCaseInsensitive => false;


        protected override void AddStore(IServiceCollection services)
        {
            // Compose exactly as the module's ServerStartup does on a relational install: the STORAGE-BLIND
            // overload — no module DbContext, no options lambda. The shared ModuleAggregatesDbContext maps
            // the aggregate; ConnectionStrings:ModuleAggregates points it at this fixture's own throwaway SQLite
            // file (the install's SQLite provider applied to it), so the run is isolated.
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = SqliteType,
                    ["ConnectionStrings:ModuleAggregates"] = ConnectionString,
                })
                .Build());

            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            services.AddScoped<IGuestbookRepository, GuestbookRepository>();
        }

        protected override System.Threading.Tasks.Task DisposeStoreAsync()
        {
            // This database's pool only: ClearAllPools is process-wide and tears pooled connections out from
            // under whatever else is running in parallel.
            ScratchSqlite.Release(ConnectionString);
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a green run over.
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
