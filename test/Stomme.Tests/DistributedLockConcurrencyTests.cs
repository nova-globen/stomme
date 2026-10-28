#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Locks;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-154's gate: the property neither lock abstraction had a test for - N acquires of ONE key raced against ONE store,
    /// and EXACTLY one wins; the key is free again once the winner releases; another key is independent. One subclass per
    /// primitive the host now registers (<see cref="ConfiguredDistributedLock"/>). The store-backed ones FAIL rather than
    /// skip when their server is unreachable (D46).
    /// </summary>
    public abstract class DistributedLockConcurrencyTests : IAsyncLifetime
    {
        private const int Contenders = 8;

        protected abstract IDistributedLock Lock { get; }

        public virtual Task InitializeAsync() => Task.CompletedTask;

        public virtual Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task Of_many_CONCURRENT_acquires_of_one_key_exactly_one_wins_and_the_key_frees_on_release()
        {
            var key = "bl154-" + Guid.NewGuid().ToString("N");
            using var start = new ManualResetEventSlim(false);

            var attempts = Enumerable.Range(0, Contenders)
                .Select(_ => Task.Run(async () =>
                {
                    start.Wait();
                    return await Lock.AcquireAsync(key, TimeSpan.FromMinutes(1));
                }))
                .ToArray();
            start.Set();
            var handles = await Task.WhenAll(attempts);

            var winners = handles.Where(handle => handle is not null).ToList();
            Assert.Single(winners);

            // While it is held, nobody else gets it.
            Assert.Null(await Lock.AcquireAsync(key, TimeSpan.FromMinutes(1)));

            // Another key is independent.
            await using (var other = await Lock.AcquireAsync(key + "-other", TimeSpan.FromMinutes(1)))
            {
                Assert.NotNull(other);
            }

            // Released, it is free again.
            await winners[0]!.DisposeAsync();
            await using var again = await Lock.AcquireAsync(key, TimeSpan.FromMinutes(1));
            Assert.NotNull(again);
        }
    }

    public sealed class InProcessDistributedLockConcurrencyTests : DistributedLockConcurrencyTests
    {
        protected override IDistributedLock Lock { get; } = new InProcessDistributedLock();

        [Fact]
        public async Task An_EXPIRED_lease_is_taken_over_and_its_old_holder_cannot_release_the_new_one()
        {
            var key = "bl154-expiry-" + Guid.NewGuid().ToString("N");
            var stale = await Lock.AcquireAsync(key, TimeSpan.FromMilliseconds(1));
            Assert.NotNull(stale);
            await Task.Delay(20);

            var fresh = await Lock.AcquireAsync(key, TimeSpan.FromMinutes(1));
            Assert.NotNull(fresh);

            await stale!.DisposeAsync();                                  // the old holder lets go late...
            Assert.Null(await Lock.AcquireAsync(key, TimeSpan.FromMinutes(1)));   // ...and the new lease still stands
            await fresh!.DisposeAsync();
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresDistributedLockConcurrencyTests : DistributedLockConcurrencyTests
    {
        protected override IDistributedLock Lock { get; } = new PostgresAdvisoryLock(PostgresTestEndpoint.ConnectionString("postgres"));

        public override Task InitializeAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), "PostgreSQL is not reachable (POSTGRES_TEST_HOST/PORT); this gate FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerDistributedLockConcurrencyTests : DistributedLockConcurrencyTests
    {
        protected override IDistributedLock Lock { get; } = new SqlServerAppLock(SqlServerTestEndpoint.ConnectionString("master"));

        public override Task InitializeAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(), "SQL Server is not reachable (SQLSERVER_TEST_HOST/PORT); this gate FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenDistributedLockConcurrencyTests : DistributedLockConcurrencyTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-lock-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override IDistributedLock Lock => new RavenDistributedLock(_store);

        public override async Task InitializeAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.Initialize();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-lock-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        public override async Task DisposeAsync()
        {
            try
            {
                await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: the next run's janitor sweep removes a leftover
            }

            _store.Dispose();
        }
    }

    /// <summary>The host's registered lock picks the INSTALL's primitive, from the configured store type and connection.</summary>
    public sealed class ConfiguredDistributedLockTests
    {
        [Theory]
        [InlineData("Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", "Server=x;Database=y", typeof(PostgresAdvisoryLock), true)]
        [InlineData("Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server", "Server=x;Database=y", typeof(SqlServerAppLock), true)]
        [InlineData("Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server", "Data Source=x.db", typeof(InProcessDistributedLock), false)]
        [InlineData("Stomme.Database.File.FileDatabase, Stomme.Server", "x", typeof(InProcessDistributedLock), false)]
        [InlineData("Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", "", typeof(InProcessDistributedLock), false)]   // not installed yet
        public void The_install_decides_the_primitive(string databaseType, string connectionString, Type expected, bool crossNode)
        {
            var configured = new ConfiguredDistributedLock(new ServiceCollection().BuildServiceProvider(), FakeConfig.Create(databaseType, connectionString));

            Assert.IsType(expected, configured.Current());
            Assert.Equal(crossNode, configured.IsCrossNode);
        }

        [Fact]
        public void The_host_registers_it_and_a_lock_registered_first_is_kept()
        {
            // The host's own composition step (AddCaching registers the locks beside the cache).
            var services = new ServiceCollection();
            services.AddCaching(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDistributedLock) && descriptor.ImplementationType == typeof(ConfiguredDistributedLock));

            // TryAdd: a deployment (or a test) that registered its own lock first keeps it.
            var custom = new InProcessDistributedLock();
            var overridden = new ServiceCollection();
            overridden.AddSingleton<IDistributedLock>(custom);
            overridden.AddCaching(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            Assert.Same(custom, Assert.Single(overridden, descriptor => descriptor.ServiceType == typeof(IDistributedLock)).ImplementationInstance);
        }

        /// <summary>An <see cref="IConfigManager"/> answering the two questions the lock asks.</summary>
        public class FakeConfig : DispatchProxy
        {
            private string _type = "";
            private string _connection = "";

            public static IConfigManager Create(string type, string connection)
            {
                var proxy = DispatchProxy.Create<IConfigManager, FakeConfig>();
                var fake = (FakeConfig)(object)proxy;
                fake._type = type;
                fake._connection = connection;
                return proxy;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
            {
                nameof(IConfigManager.GetDatabaseType) => _type,
                nameof(IConfigManager.GetConnectionString) when args is null || args.Length == 0 => _connection,
                _ => throw new NotSupportedException(method.Name),
            };
        }
    }
}
