using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests
{
    /// <summary>
    /// The D78 FindAsync conformance suite against the RELATIONAL (EF Core) store — a throwaway SQLite FILE,
    /// composed exactly as a module composes its clean-arch store on a relational install (the D76 helper selects
    /// the EF branch and binds the closed <c>IRepository&lt;ProbeAggregate, Guid&gt;</c> to <c>EfRepository</c>).
    /// This is the baseline the live RavenDB run must match member for member on the SAFE predicates, and whose
    /// case-SENSITIVE bare <c>==</c> the string-boundary test pins.
    /// </summary>
    public sealed class EfFindAsyncConformanceTests : FindAsyncConformanceTests
    {
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"stomme-findasync-conformance-{Guid.NewGuid():N}.db");

        /// <summary>The one string both the store and the teardown use — the pool is keyed by it.</summary>
        private string ConnectionString => $"Data Source={_databasePath}";

        protected override bool StringEqualityIsCaseInsensitive => false;

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = SqliteType,
                })
                .Build());

            services.AddModuleAggregatePersistence<ProbeAggregate, Guid, ProbeDbContext>(
                options => options.UseSqlite(ConnectionString));
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
