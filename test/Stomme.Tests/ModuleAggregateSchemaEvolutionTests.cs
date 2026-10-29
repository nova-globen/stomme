using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests
{
    /// <summary>
    /// The marketplace-arrival schema gap, reproduced exactly as a drive found it.
    /// The shared module-aggregates store brings its schema up with <c>EnsureCreated</c>, which creates the FULL
    /// schema only when the DATABASE does not exist. The sidecar database is created at FIRST boot with whatever
    /// aggregates are then registered — so a clean-arch module arriving LATER (marketplace nupkg → restart →
    /// aggregate enrolled) found the database present, <c>EnsureCreated</c> no-opped, and its table never
    /// existed: writes threw <c>no such table: BlogPost</c>, swallowed into a silent empty read.
    /// <para>
    /// The contract pinned here: an aggregate registered AFTER the store database exists still gets its table
    /// (additive CREATE for the missing tables only), and the data already in the store survives untouched —
    /// a fix that dropped/recreated the database would pass the first assertion and fail the second.
    /// </para>
    /// </summary>
    public sealed class ModuleAggregateSchemaEvolutionTests : IAsyncLifetime
    {
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"stomme-schema-evolution-{Guid.NewGuid():N}.db");

        /// <summary>The one string both boots and the teardown use — the pool is keyed by it.</summary>
        private string ConnectionString => $"Data Source={_databasePath}";

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            // This database's pool only - ClearAllPools is process-wide.
            ScratchSqlite.Release(ConnectionString);
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }

        [Fact]
        public async Task An_aggregate_registered_after_the_store_database_exists_still_gets_its_table_additively()
        {
            Guid firstBootEntryId;

            // ---- BOOT 1: only the Guestbook aggregate exists; the sidecar is created with its table alone ----
            await using (var boot1 = Compose(includeLaterAggregate: false))
            {
                await StartHostedServicesAsync(boot1);

                await using var scope = boot1.CreateAsyncScope();
                var guestbook = scope.ServiceProvider.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                var entry = GuestbookEntryAggregate.Sign(TenantId.Default, 4242, "Ada Lovelace", "Signed before the marketplace install.");
                await guestbook.AddAsync(entry);
                firstBootEntryId = entry.Id;
            }

            // Let go of boot 1's pooled connections so boot 2 genuinely reopens the file - this database's pool
            // only, never the process-wide form.
            ScratchSqlite.Release(ConnectionString);

            // ---- BOOT 2: the marketplace restart — a SECOND aggregate is enrolled, the database already exists ----
            await using (var boot2 = Compose(includeLaterAggregate: true))
            {
                await StartHostedServicesAsync(boot2);

                // The later-installed aggregate's table must exist: a write round-trips into a FRESH scope.
                Guid noteId;
                await using (var writeScope = boot2.CreateAsyncScope())
                {
                    var notes = writeScope.ServiceProvider
                        .GetRequiredService<IRepository<LaterInstalledNoteAggregate, Guid>>();
                    var note = LaterInstalledNoteAggregate.Create("Installed after first boot.");
                    await notes.AddAsync(note); // Throws 'no such table' before the fix
                    noteId = note.Id;
                }

                await using (var readScope = boot2.CreateAsyncScope())
                {
                    var notes = readScope.ServiceProvider
                        .GetRequiredService<IRepository<LaterInstalledNoteAggregate, Guid>>();
                    var reread = await notes.GetByIdAsync(noteId);
                    Assert.NotNull(reread);
                    Assert.Equal("Installed after first boot.", reread.Text);
                }

                // ADDITIVE, not recreate: the first boot's row survived the schema catch-up.
                await using (var survivorScope = boot2.CreateAsyncScope())
                {
                    var guestbook = survivorScope.ServiceProvider
                        .GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                    var survivor = await guestbook.GetByIdAsync(firstBootEntryId);
                    Assert.NotNull(survivor);
                    Assert.Equal("Signed before the marketplace install.", survivor.Message);
                }
            }
        }

        // Composes the storage-blind stack exactly as module ServerStartups do on a relational install (the
        // EfGuestbookStoreConformanceTests shape), pointing the shared store at this fixture's throwaway file.
        /// <summary>
        /// An aggregate that GAINED a column after its table existed - the Guestbook's tenant, on every install
        /// that predates it. The table is present, the column absent, and before the fix every read threw. The catch-up
        /// ADDS the column, nullable: the legacy row is untouched (still counted in the table) and, holding no tenant, is
        /// hidden by every tenant's filter - fail-closed - while a new row signed under the tenant reads back.
        /// </summary>
        [Fact]
        public async Task A_column_the_aggregate_gained_after_its_table_existed_is_added_nullable_and_legacy_rows_are_hidden_not_lost()
        {
            Guid legacyId;

            // ---- BOOT 1: the current shape; then the table is put back to its LEGACY shape (no tenant column) ----
            await using (var boot1 = Compose(includeLaterAggregate: false))
            {
                await StartHostedServicesAsync(boot1);
                await using var scope = boot1.CreateAsyncScope();
                var guestbook = scope.ServiceProvider.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                var entry = GuestbookEntryAggregate.Sign(TenantId.Default, 4242, "Ada Lovelace", "Signed before the column existed.");
                await guestbook.AddAsync(entry);
                legacyId = entry.Id;
            }

            ScratchSqlite.Release(ConnectionString);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var drop = connection.CreateCommand();
                drop.CommandText = "ALTER TABLE \"GuestbookEntryAggregate\" DROP COLUMN \"TenantId\"";
                await drop.ExecuteNonQueryAsync();
            }

            ScratchSqlite.Release(ConnectionString);

            // ---- BOOT 2: the upgrade - the model has the column, the table has not ----
            await using (var boot2 = Compose(includeLaterAggregate: false))
            {
                await StartHostedServicesAsync(boot2); // Adds the column, nullable

                Guid freshId;
                await using (var scope = boot2.CreateAsyncScope())
                {
                    var guestbook = scope.ServiceProvider.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                    Assert.Null(await guestbook.GetByIdAsync(legacyId)); // hidden: it holds no tenant
                    var fresh = GuestbookEntryAggregate.Sign(TenantId.Default, 4242, "Grace Hopper", "Signed after the upgrade.");
                    await guestbook.AddAsync(fresh);
                    freshId = fresh.Id;
                }

                await using (var scope = boot2.CreateAsyncScope())
                {
                    var guestbook = scope.ServiceProvider.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                    Assert.NotNull(await guestbook.GetByIdAsync(freshId));
                    Assert.Null(await guestbook.GetByIdAsync(legacyId));
                }
            }

            // Nothing was deleted: both rows are in the table, the legacy one with no tenant.
            ScratchSqlite.Release(ConnectionString);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM \"GuestbookEntryAggregate\"";
                Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
                await using var legacy = connection.CreateCommand();
                legacy.CommandText = "SELECT COUNT(*) FROM \"GuestbookEntryAggregate\" WHERE \"TenantId\" IS NULL";
                Assert.Equal(1L, (long)(await legacy.ExecuteScalarAsync())!);
            }
        }

        private ServiceProvider Compose(bool includeLaterAggregate)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = SqliteType,
                    ["ConnectionStrings:ModuleAggregates"] = ConnectionString,
                })
                .Build());

            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            if (includeLaterAggregate)
            {
                services.AddModuleAggregatePersistence<LaterInstalledNoteAggregate, Guid>();
            }

            return services.BuildServiceProvider();
        }

        // Runs the module-store schema initializer exactly as the host does at startup.
        private static async Task StartHostedServicesAsync(ServiceProvider provider)
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The "module installed from the marketplace after first boot" stand-in: a minimal storage-blind
    /// aggregate that is deliberately NOT registered on the first composition of the shared store.
    /// </summary>
    public sealed class LaterInstalledNoteAggregate : AggregateRoot<Guid>
    {
        // EF Core materialisation constructor. Not for application use.
        private LaterInstalledNoteAggregate()
        {
        }

        public string Text { get; private set; } = string.Empty;

        public static LaterInstalledNoteAggregate Create(string text)
        {
            return new LaterInstalledNoteAggregate
            {
                Id = Guid.NewGuid(),
                Text = text ?? string.Empty,
            };
        }
    }
}
