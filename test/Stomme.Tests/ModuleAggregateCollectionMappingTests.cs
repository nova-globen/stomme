using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Fixtures.CollectionAggregate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// How the shared relational <see cref="ModuleAggregatesDbContext"/> maps a
    /// COLLECTION property of a storage-blind aggregate, one element shape at a time (each on its own aggregate so a
    /// model-build throw from one shape cannot hide another). The census claimed collection properties "fall
    /// through SILENTLY"; the measurement this suite was written from (recorded in the Persistence.EfCore README)
    /// found that true only for the scalar-element lists — an <c>Entity&lt;Guid&gt;</c> element became an EF-only
    /// navigation table and a keyless record element threw at model build (swallowed by the schema initializer).
    /// <para>
    /// The contract pinned here, for every shape: the collection is ONE JSON column on the aggregate's own table
    /// (no second table, no navigation) and it round-trips member for member into a FRESH scope.
    /// </para>
    /// </summary>
    public sealed class ModuleAggregateCollectionMappingTests : IDisposable
    {
        private const string SqliteType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        private readonly List<string> _databases = [];

        public void Dispose()
        {
            foreach (var path in _databases)
            {
                SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(path)));
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A leftover temp file is not worth failing a green run over.
                }
            }
        }

        [Fact]
        public Task An_enum_list_is_one_json_column_and_round_trips() =>
            AssertRoundTripsAsJsonColumnAsync(EnumListProbeAggregate.Create, aggregate => aggregate.Items,
                (expected, actual) => Assert.Equal(expected, actual));

        [Fact]
        public Task A_string_list_is_one_json_column_and_round_trips() =>
            AssertRoundTripsAsJsonColumnAsync(StringListProbeAggregate.Create, aggregate => aggregate.Items,
                (expected, actual) => Assert.Equal(expected, actual));

        [Fact]
        public Task A_keyless_record_list_is_one_json_column_and_round_trips() =>
            AssertRoundTripsAsJsonColumnAsync(RecordListProbeAggregate.Create, aggregate => aggregate.Items,
                (expected, actual) => Assert.Equal(expected, actual));

        [Fact]
        public Task A_record_list_carrying_a_string_value_object_is_one_json_column_and_round_trips() =>
            AssertRoundTripsAsJsonColumnAsync(ValueObjectRecordListProbeAggregate.Create, aggregate => aggregate.Items,
                (expected, actual) => Assert.Equal(expected, actual));

        [Fact]
        public Task An_entity_child_list_is_one_json_column_not_a_navigation_and_round_trips() =>
            AssertRoundTripsAsJsonColumnAsync(EntityListProbeAggregate.Create, aggregate => aggregate.Items,
                (expected, actual) => Assert.Equal(
                    expected.Select(a => (a.Id, a.Channel, a.AttemptedOnUtc, a.AttemptedOnUtc.Kind, a.Error)),
                    actual.Select(a => (a.Id, a.Channel, a.AttemptedOnUtc, a.AttemptedOnUtc.Kind, a.Error))));

        [Fact]
        public async Task A_mutated_collection_is_detected_and_persisted_on_update()
        {
            var path = NewDatabasePath();
            await using var provider = Compose<CollectionBearingAggregate>(path);
            await StartHostedServicesAsync(provider);

            var aggregate = CollectionBearingAggregate.CreateFull(7, "tracked");
            await InScopeAsync<CollectionBearingAggregate>(provider, repository => repository.AddAsync(aggregate));

            // Load, mutate ONLY the collection contents (same list instance), save: the value comparer must see the
            // change, or EF writes nothing and the update is silently lost.
            await using (var scope = provider.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<CollectionBearingAggregate, Guid>>();
                var loaded = await repository.GetByIdAsync(aggregate.Id);
                Assert.NotNull(loaded);
                loaded!.AddTag("added-after-load");
                loaded.RecordAttempt(CollectionProbeKind.InApp, new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc), null);
                await repository.UpdateAsync(loaded);
            }

            await using (var scope = provider.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<CollectionBearingAggregate, Guid>>();
                var reread = await repository.GetByIdAsync(aggregate.Id);
                Assert.NotNull(reread);
                Assert.Contains("added-after-load", reread!.Tags);
                Assert.Equal(3, reread.Attempts.Count);
            }
        }

        [Fact]
        public async Task An_element_shape_the_convention_cannot_serialize_switches_its_aggregate_off_by_name()
        {
            // No longer a model-build failure every module shares - the aggregate is quarantined, the reason names
            // the property, and its repository refuses every call by name.
            var path = NewDatabasePath();
            await using var provider = Compose<UnserializableElementProbeAggregate>(path);
            await using var scope = provider.CreateAsyncScope();
            _ = scope.ServiceProvider.GetRequiredService<ModuleAggregatesDbContext>().Model;

            Assert.True(scope.ServiceProvider.GetRequiredService<ModuleAggregateRegistry>()
                .IsQuarantined(typeof(UnserializableElementProbeAggregate), out var reason));
            Assert.Contains(nameof(UnserializableElementProbeAggregate.Items), reason);
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<IRepository<UnserializableElementProbeAggregate, Guid>>().GetAllAsync());
            Assert.Contains(nameof(UnserializableElementProbeAggregate), refusal.Message);
        }

        private async Task AssertRoundTripsAsJsonColumnAsync<TAggregate, TElement>(
            Func<TAggregate> create,
            Func<TAggregate, IReadOnlyList<TElement>> items,
            Action<IReadOnlyList<TElement>, IReadOnlyList<TElement>> assertEqual)
            where TAggregate : AggregateRoot<Guid>
        {
            var path = NewDatabasePath();
            await using var provider = Compose<TAggregate>(path);

            var measured = Measure<TAggregate>(provider);
            Assert.True(measured.Shape == "json-column", $"{typeof(TAggregate).Name}.Items today: {measured.Detail}");
            // No second table: the element type must not survive as a convention-discovered entity.
            var expectedTables = string.Join(",", new[] { typeof(TAggregate).Name, "OutboxMessages" }.OrderBy(n => n));
            Assert.EndsWith($"tables=[{expectedTables}]", measured.Detail);

            await StartHostedServicesAsync(provider);
            var aggregate = create();
            await InScopeAsync<TAggregate>(provider, repository => repository.AddAsync(aggregate));

            await using var readScope = provider.CreateAsyncScope();
            var reread = await readScope.ServiceProvider
                .GetRequiredService<IRepository<TAggregate, Guid>>().GetByIdAsync(aggregate.Id);
            Assert.NotNull(reread);
            Assert.Equal(items(aggregate).Count, items(reread!).Count);
            assertEqual(items(aggregate), items(reread));
        }

        // What EF makes of the aggregate's Items property: a thrown model build, a navigation (second table), a
        // mapped scalar/JSON column on the aggregate's table, or nothing at all.
        private static (string Shape, string Detail) Measure<TAggregate>(ServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModuleAggregatesDbContext>();
            IModel model;
            try
            {
                model = context.GetService<IDesignTimeModel>().Model;
            }
            catch (Exception ex)
            {
                return ("throw", $"THROWS at model build: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }

            var tables = string.Join(",", model.GetRelationalModel().Tables.Select(table => table.Name).OrderBy(n => n));
            var entity = model.FindEntityType(typeof(TAggregate))!;
            if (entity.FindNavigation("Items") is { } navigation)
            {
                return ("navigation", $"NAVIGATION to {navigation.TargetEntityType.ClrType.Name}; tables=[{tables}]");
            }

            if (entity.FindProperty("Items") is { } property)
            {
                return ("json-column", $"COLUMN {property.GetColumnName()} ({property.ClrType.Name}); tables=[{tables}]");
            }

            return ("unmapped", $"SILENTLY UNMAPPED (no property, no navigation); tables=[{tables}]");
        }

        private string NewDatabasePath()
        {
            var path = Path.Combine(Path.GetTempPath(), $"stomme-collection-mapping-{Guid.NewGuid():N}.db");
            _databases.Add(path);
            return path;
        }

        private static string ConnectionString(string path) => $"Data Source={path};Pooling=True";

        private static ServiceProvider Compose<TAggregate>(string path)
            where TAggregate : AggregateRoot<Guid>
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = SqliteType,
                    ["ConnectionStrings:ModuleAggregates"] = ConnectionString(path),
                })
                .Build());

            services.AddModuleAggregatePersistence<TAggregate, Guid>();
            return services.BuildServiceProvider();
        }

        private static async Task InScopeAsync<TAggregate>(ServiceProvider provider, Func<IRepository<TAggregate, Guid>, Task> action)
            where TAggregate : AggregateRoot<Guid>
        {
            await using var scope = provider.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IRepository<TAggregate, Guid>>());
        }

        private static async Task StartHostedServicesAsync(ServiceProvider provider)
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>An element with no usable constructor for the shared JSON contract (two non-public parameterized ctors).</summary>
    public sealed class UnserializableProbeElement
    {
        private UnserializableProbeElement(string a) => A = a;

        private UnserializableProbeElement(int b) => A = b.ToString();

        public string A { get; }

        public static UnserializableProbeElement Make(string a) => new(a);
    }

    public sealed class UnserializableElementProbeAggregate : AggregateRoot<Guid>
    {
        private readonly List<UnserializableProbeElement> _items = [];

        private UnserializableElementProbeAggregate()
        {
        }

        public IReadOnlyList<UnserializableProbeElement> Items => _items;
    }
}