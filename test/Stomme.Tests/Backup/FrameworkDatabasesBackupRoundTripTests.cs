#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Backup;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Models;
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// <c>FrameworkStoreBackupRoundTripConformanceTests</c> - the framework's own tables on an
    /// INSTALLED host, SQLite and PostgreSQL: the part is taken; afterwards a setting is added, the site renamed and a user
    /// role removed; the restore brings the framework tables back BYTE FOR BYTE - original keys, audit columns and Identity
    /// rows included - and the next insert takes a fresh key (on PostgreSQL after its sequence was rewound to where a fresh
    /// database would have it). A snapshot from another database layout is refused before anything is written.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class FrameworkDatabasesBackupRoundTripTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        protected FrameworkDatabasesBackupRoundTripTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        /// <summary>Moves the Setting table's identity back to 1 - what a freshly created database would hand out next.</summary>
        protected virtual Task RewindSettingIdentityAsync(TenantDBContext context) => Task.CompletedTask;

        /// <summary>How each identity column is generated - the restore may relax one, and must put it back.</summary>
        protected virtual Task<string> IdentityGenerationsAsync(TenantDBContext context) => Task.FromResult(string.Empty);

        // ---- store-level probes: relational by default, the RavenDB leg overrides them ----------------------------------

        protected virtual Task PlantSearchRowAsync(WebApplication app) =>
            WithTenantContextAsync(app, async context =>
            {
                context.Set<SearchContentAggregate>().Add(NewSearchRow());
                return await context.SaveChangesAsync();
            });

        protected virtual Task<int> SearchRowsAsync(WebApplication app) =>
            WithTenantContextAsync(app, context => context.Set<SearchContentAggregate>().CountAsync());

        protected virtual Task<int> HighestSettingIdAsync(WebApplication app) =>
            WithTenantContextAsync(app, context => context.Set<SettingAggregate>().MaxAsync(setting => setting.Id));

        protected virtual Task RewindSettingKeysAsync(WebApplication app) =>
            WithTenantContextAsync(app, async context => { await RewindSettingIdentityAsync(context); return 0; });

        protected virtual Task<string> IdentityGenerationsAsync(WebApplication app) =>
            WithTenantContextAsync(app, IdentityGenerationsAsync);

        protected static SearchContentAggregate NewSearchRow() => new()
        {
            SiteId = 1, EntityName = "Page", EntityId = "1", Title = "after the snapshot", Description = string.Empty, Body = string.Empty,
            Url = string.Empty, Permissions = string.Empty, ContentModifiedBy = string.Empty, ContentModifiedOn = DateTime.UtcNow,
            AdditionalContent = string.Empty, CreatedOn = DateTime.UtcNow,
        };

        [Fact]
        public async Task A_restore_returns_the_framework_tables_byte_for_byte_and_the_next_insert_collides_with_nothing()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var scope = await ScopeAsync(app);
                var snapshot = await ExportAsync(app, scope);
                _output.WriteLine($"[part] {snapshot.Length:N0} bytes");

                // Each physical database carries a table ONCE - the Setting table the master shares with the tenant included.
                var carried = new List<(string Database, string Table)>();
                string? database = null;
                await foreach (var (_, json) in BackupRecords.ReadAsync(new MemoryStream(snapshot), CancellationToken.None))
                {
                    var header = System.Text.Json.JsonDocument.Parse(json).RootElement;
                    if (header.TryGetProperty("$database", out var name))
                    {
                        database = name.GetString();
                    }
                    else if (header.TryGetProperty("$table", out var table))
                    {
                        carried.Add((database!, table.GetString()!));
                    }
                }

                Assert.Contains(carried, entry => entry.Table.EndsWith(".SettingAggregate", StringComparison.Ordinal));
                Assert.Contains(carried, entry => entry.Table.EndsWith(".HtmlTextAggregate", StringComparison.Ordinal));
                Assert.Equal(carried.Count, carried.Distinct().Count());

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = "BackupRoundTrip", SettingValue = "after the snapshot", IsPrivate = false });

                    var sites = sp.GetRequiredService<ISiteRepository>();
                    var site = await sites.GetSiteAsync(alias.SiteId);
                    site.Name = "Renamed after the snapshot";
                    await sites.UpdateSiteAsync(site);

                    var userRoles = sp.GetRequiredService<IUserRoleRepository>();
                    var anyRole = (await userRoles.GetUserRolesAsync(alias.SiteId)).First();
                    await userRoles.DeleteUserRoleAsync(anyRole.UserRoleId);

                    // A new version of the home page's content - HtmlText rides with the framework tables.
                    var placed = (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                        .Where(pm => !pm.IsDeleted && pm.Module?.ModuleDefinitionName?.Contains("HtmlText", StringComparison.Ordinal) == true)
                        .OrderBy(pm => pm.PageModuleId).First();
                    await sp.GetRequiredService<Stomme.Modules.HtmlText.Repository.IHtmlTextRepository>().AddHtmlTextAsync(
                        new Stomme.Modules.HtmlText.Models.HtmlText { ModuleId = placed.ModuleId, Content = "<p>after the snapshot</p>" });
                    return 0;
                });
                Assert.NotEqual(snapshot, await ExportAsync(app, scope));

                // A search row after the snapshot: the index is not carried, it is cleared for a rebuild.
                await PlantSearchRowAsync(app);
                Assert.True(await SearchRowsAsync(app) > 0);
                await RewindSettingKeysAsync(app);
                var generations = await IdentityGenerationsAsync(app);

                var participant = Participant(app);
                var verdict = await participant.ValidateAsync(new BytesPart(participant, snapshot), scope, CancellationToken.None);
                Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
                await participant.ReplaceAsync(new BytesPart(participant, snapshot), scope, CancellationToken.None);

                Assert.Equal(snapshot, await ExportAsync(app, scope));
                Assert.Equal(0, await SearchRowsAsync(app));
                Assert.Equal(generations, await IdentityGenerationsAsync(app));

                // The next insert takes a key past every restored one.
                var highest = await HighestSettingIdAsync(app);
                var added = await GateDrive.InTenantAsync(app, Host, (sp, alias) => sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = "AfterTheRestore", SettingValue = "fresh key", IsPrivate = false,
                }));
                Assert.True(added.SettingId > highest, $"the insert after the restore took key {added.SettingId}; the highest restored is {highest}.");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_snapshot_of_another_database_layout_is_refused_before_anything_is_written()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var scope = await ScopeAsync(app);
                var snapshot = await ExportAsync(app, scope);

                // The same rows, filed as if the tenant lived in a database of its own.
                using var relabelled = new MemoryStream();
                await foreach (var (home, json) in BackupRecords.ReadAsync(new MemoryStream(snapshot), CancellationToken.None))
                {
                    var text = json.StartsWith("{\"$database\"", StringComparison.Ordinal) ? System.Text.RegularExpressions.Regex.Replace(json, "\"\\$database\":\"[^\"]*\"", "\"$$database\":\"elsewhere\"") : json;
                    await BackupRecords.WriteAsync(relabelled, home, text, CancellationToken.None);
                }

                var participant = Participant(app);
                var verdict = await participant.ValidateAsync(new BytesPart(participant, relabelled.ToArray()), scope, CancellationToken.None);
                Assert.False(verdict.IsValid);
                Assert.Contains(verdict.Problems, problem => problem.Contains("not this installation's", StringComparison.Ordinal));
                Assert.Equal(snapshot, await ExportAsync(app, scope));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static IBackupParticipant Participant(WebApplication app) =>
            app.Services.CreateScope().ServiceProvider.GetServices<IBackupParticipant>().Single(p => p.Name == FrameworkDatabasesBackupParticipant.PartName);

        private static Task<BackupScope> ScopeAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                new BackupScope("gate", (await sp.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Select(tenant => tenant.TenantId).ToList()));

        private static async Task<byte[]> ExportAsync(WebApplication app, BackupScope scope)
        {
            var participant = Participant(app);
            using var buffer = new MemoryStream();
            await participant.ExportAsync(new ArchivePartWriter(participant.Name, buffer), scope, CancellationToken.None);
            return buffer.ToArray();
        }

        protected static async Task<T> WithTenantContextAsync<T>(WebApplication app, Func<TenantDBContext, Task<T>> action)
        {
            var tenant = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => (await sp.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Single(t => t.TenantId == alias.TenantId));
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
            await using var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TenantDBContext>>().CreateDbContext();
            return await action(context);
        }

        /// <summary>A part held in memory - the archive's own behaviour is proven elsewhere.</summary>
        private sealed class BytesPart(IBackupParticipant participant, byte[] bytes) : IBackupPartReader
        {
            public string PartName => participant.Name;

            public string SchemaVersion => participant.SchemaVersion;

            public long Length => bytes.Length;

            public Stream OpenRead() => new MemoryStream(bytes, writable: false);
        }
    }

    public sealed class SqliteFrameworkDatabasesBackupRoundTripTests(ITestOutputHelper output) : FrameworkDatabasesBackupRoundTripTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    /// <summary>The RavenDB leg: the framework collections and Identity documents, and the HiLo moved past the restored ids.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenFrameworkDatabasesBackupRoundTripTests(ITestOutputHelper output) : FrameworkDatabasesBackupRoundTripTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();

        private static (Raven.Client.Documents.IDocumentStore Store, string Database) Raven(WebApplication app)
        {
            var store = app.Services.GetRequiredService<Raven.Client.Documents.IDocumentStore>();
            var options = app.Services.GetRequiredService<RavenFrameworkStoreOptions>();
            return (store, string.IsNullOrEmpty(options.FrameworkDatabase) ? store.Database : options.FrameworkDatabase);
        }

        protected override async Task PlantSearchRowAsync(WebApplication app)
        {
            var (store, database) = Raven(app);
            using var session = store.OpenAsyncSession(database);
            await session.StoreAsync(NewSearchRow(), "SearchContentAggregate/999999");
            await session.SaveChangesAsync();
        }

        protected override async Task<int> SearchRowsAsync(WebApplication app)
        {
            var (store, database) = Raven(app);
            var statistics = await store.Maintenance.ForDatabase(database).SendAsync(new global::Raven.Client.Documents.Operations.GetCollectionStatisticsOperation());
            return (int)statistics.Collections.GetValueOrDefault(store.Conventions.FindCollectionName(typeof(SearchContentAggregate)));
        }

        protected override async Task<int> HighestSettingIdAsync(WebApplication app)
        {
            var (store, database) = Raven(app);
            using var session = store.OpenAsyncSession(new global::Raven.Client.Documents.Session.SessionOptions { Database = database, NoTracking = true });
            var highest = 0;
            await using var results = await session.Advanced.StreamAsync<SettingAggregate>("SettingAggregate/");
            while (await results.MoveNextAsync())
            {
                highest = Math.Max(highest, results.Current.Document.Id);
            }

            return highest;
        }

        // What a fresh database would have: the SettingAggregate range starts again at 1 - and this process then RESERVES
        // one from there, so a restore that forgot to drop the cached range would hand out an id that overwrites a document.
        protected override async Task RewindSettingKeysAsync(WebApplication app)
        {
            var (store, database) = Raven(app);
            await store.Operations.ForDatabase(database).SendAsync(new global::Raven.Client.Documents.Operations.PatchOperation("Raven/Hilo/SettingAggregate", null,
                new global::Raven.Client.Documents.Operations.PatchRequest { Script = "this.Max = 0;" }));
            var generator = (app.Services.GetRequiredService<Stomme.Persistence.RavenDb.IRavenIdGenerator>() as Stomme.Persistence.RavenDb.RavenHiLoIdGenerator)!;
            generator.Forget();
            Assert.Equal(1, (int)await generator.NextIdAsync("SettingAggregate", typeof(int), database));

            // Reserving moved the server's Max to the end of that range; put it back, so only the restore can move it past
            // the restored ids (measured: left at the range end, it happened to clear them, and a missing raise survived).
            await store.Operations.ForDatabase(database).SendAsync(new global::Raven.Client.Documents.Operations.PatchOperation("Raven/Hilo/SettingAggregate", null,
                new global::Raven.Client.Documents.Operations.PatchRequest { Script = "this.Max = 0;" }));
        }
    }

    /// <summary>
    /// SQL Server on a shared test server <c>:1434</c> - original keys through IDENTITY_INSERT, one table at a time, and no reseed:
    /// the Setting identity is rewound to where a fresh database would start, and the restore's explicit keys carry it past
    /// every restored row by themselves.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerFrameworkDatabasesBackupRoundTripTests(ITestOutputHelper output) : FrameworkDatabasesBackupRoundTripTests(output)
    {
        private protected override IGateStore Store() => new SqlServerGateStore();

        protected override async Task RewindSettingIdentityAsync(TenantDBContext context)
        {
            var table = context.Model.FindEntityType(typeof(SettingAggregate))!.GetTableName()!;
#pragma warning disable EF1002 // names from the EF model
            await context.Database.ExecuteSqlRawAsync($"DBCC CHECKIDENT ('[{table}]', RESEED, 0)");
#pragma warning restore EF1002
            Assert.Equal(0m, (await context.Database.SqlQueryRaw<decimal>($"SELECT CAST(IDENT_CURRENT('{table}') AS decimal(38,0)) AS [Value]").ToListAsync()).Single());
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresFrameworkDatabasesBackupRoundTripTests(ITestOutputHelper output) : FrameworkDatabasesBackupRoundTripTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();

        protected override async Task RewindSettingIdentityAsync(TenantDBContext context)
        {
            var entity = context.Model.FindEntityType(typeof(SettingAggregate))!;
            var table = entity.GetTableName()!;
            var column = entity.FindPrimaryKey()!.Properties[0].GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema()))!;
#pragma warning disable EF1002 // names from the EF model
            await context.Database.ExecuteSqlRawAsync($"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), 1, false)");
#pragma warning restore EF1002
        }

        protected override async Task<string> IdentityGenerationsAsync(TenantDBContext context)
        {
            var rows = await context.Database.SqlQueryRaw<string>(
                "SELECT table_name || '.' || column_name || '=' || identity_generation AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND is_identity = 'YES' ORDER BY 1").ToListAsync();
            Assert.Contains(rows, row => row.EndsWith("=ALWAYS", StringComparison.Ordinal));
            return string.Join(";", rows);
        }
    }
}
