using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-92 (D98 Wave 0) — the tenant-owned aggregate relational trap, measured by the August endgame spikes and
    /// ported from branch <c>endgame/proof-of-life</c>. <see cref="ModuleAggregatesDbContext"/>'s reflective
    /// value-object guard rejected <see cref="TenantId"/> as an unsupported shape, the throw was caught by the
    /// schema initializer and logged as "it will read as empty", and the host reported healthy — so EVERY
    /// <see cref="ITenantOwned"/> storage-blind aggregate silently persisted NOTHING on a relational install.
    /// 38 of 47 the predecessor platform aggregates are tenant-owned, which is why this gate lands before any of them is lifted.
    /// <para>
    /// The contract pinned here: a tenant-owned aggregate registered through the storage-blind overload
    /// round-trips on the relational store with its <c>TenantId</c> intact, exactly like a module-scoped one.
    /// </para>
    /// </summary>
    public sealed class ModuleAggregateTenantOwnedPersistenceTests : IAsyncLifetime
    {
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"stomme-tenant-owned-{Guid.NewGuid():N}.db");

        /// <summary>The one string both the store and the teardown use — the pool is keyed by it.</summary>
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
        public async Task A_tenant_owned_storage_blind_aggregate_round_trips_on_the_relational_store()
        {
            await using var provider = Compose();
            await StartHostedServicesAsync(provider);

            Guid id;
            await using (var writeScope = provider.CreateAsyncScope())
            {
                var repository = writeScope.ServiceProvider
                    .GetRequiredService<IRepository<TenantOwnedNoteAggregate, Guid>>();
                var note = TenantOwnedNoteAggregate.Create(TenantId.Default, "Owned by the default tenant.");
                await repository.AddAsync(note); // before the fix: NotSupportedException from the model build
                id = note.Id;
            }

            await using (var readScope = provider.CreateAsyncScope())
            {
                var repository = readScope.ServiceProvider
                    .GetRequiredService<IRepository<TenantOwnedNoteAggregate, Guid>>();
                var reread = await repository.GetByIdAsync(id);

                Assert.NotNull(reread);
                Assert.Equal("Owned by the default tenant.", reread.Text);
                Assert.Equal(TenantId.Default, reread.TenantId);
            }
        }

        // Composes the storage-blind stack exactly as module ServerStartups do on a relational install (the
        // ModuleAggregateSchemaEvolutionTests shape). The fallback ITenantContext supplies TenantId.Default, so
        // the tenant query filter admits the aggregate written above.
        private ServiceProvider Compose()
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

            services.AddModuleAggregatePersistence<TenantOwnedNoteAggregate, Guid>();
            return services.BuildServiceProvider();
        }

        private static async Task StartHostedServicesAsync(ServiceProvider provider)
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The smallest tenant-owned storage-blind aggregate — the shape of 38 of 47 the predecessor platform aggregates.
    /// </summary>
    public sealed class TenantOwnedNoteAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // EF Core materialisation constructor. Not for application use.
        private TenantOwnedNoteAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public string Text { get; private set; } = string.Empty;

        public static TenantOwnedNoteAggregate Create(TenantId tenantId, string text)
        {
            return new TenantOwnedNoteAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Text = text ?? string.Empty,
            };
        }
    }
}
