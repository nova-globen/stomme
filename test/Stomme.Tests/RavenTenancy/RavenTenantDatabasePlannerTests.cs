#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Repository.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests.RavenTenancy
{
    /// <summary>
    /// BL-176 - the rule that decides a RavenDB tenant's database, against infra UAT's RavenDB: the installation's own database
    /// when the connection string names it; the tenant's own database when it exists; a tenant created BEFORE BL-176 (its own
    /// database never created, its sites in the installation's) kept where its rows are and recorded so the answer is stable;
    /// a new tenant's database created with the search index; another server refused. And the resolver refuses a tenant it was
    /// never told about rather than route it to the installation's database.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenTenantDatabasePlannerTests : IAsyncLifetime
    {
        private readonly string _installation = "stomme_bl176_" + Guid.NewGuid().ToString("N")[..10];
        private readonly RavenTenantDatabases _databases = new();
        private IDocumentStore _store = null!;
        private string _shared = string.Empty;

        public async Task InitializeAsync()
        {
            _store = new DocumentStore { Urls = new[] { RavenGateStore.Url }, Database = _installation }.Initialize();
            await RavenFrameworkStoreServiceExtensions.EnsureTenantDatabaseAsync(_store, _installation);
        }

        private RavenTenantDatabasePlanner Planner() =>
            new(_store, _databases, () => _shared, value => _shared = value, NullLogger.Instance);

        private static Tenant Tenant(int id) => new() { TenantId = id, Name = "Tenant" + id };

        private string Connection(string database) => $"Url={RavenGateStore.Url};Database={database}";

        [Fact]
        public async Task The_installations_database_a_new_tenants_own_and_a_tenant_from_before_each_resolve_as_the_rule_says()
        {
            // Names the installation's database: shares it.
            Assert.Null(await Planner().PlanAsync(Tenant(2), Connection(_installation), () => Task.FromResult(true)));

            // A new tenant (no sites yet): its database is created, with the search index.
            var own = _installation + "_three";
            Assert.Equal(own, await Planner().PlanAsync(Tenant(3), Connection(own), () => Task.FromResult(false)));
            var record = await _store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(own));
            Assert.NotNull(record);
            Assert.Contains(record.Indexes.Keys, name => name.Contains("SearchContentWord", StringComparison.Ordinal));

            // Asked again (the next boot), it exists: its own again, and nothing recorded.
            Assert.Equal(own, await Planner().PlanAsync(Tenant(3), Connection(own), () => Task.FromResult(true)));

            // A tenant from before BL-176: its database never created, its sites in the installation's database.
            var never = _installation + "_four";
            Assert.Null(await Planner().PlanAsync(Tenant(4), Connection(never), () => Task.FromResult(true)));
            Assert.Equal("4", _shared);
            Assert.Null(await _store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(never)));   // not created

            // Recorded: stable even if its database appears later.
            await RavenFrameworkStoreServiceExtensions.EnsureTenantDatabaseAsync(_store, never);
            Assert.Null(await Planner().PlanAsync(Tenant(4), Connection(never), () => Task.FromResult(true)));

            var resolver = new TenantRavenDatabaseResolver(_databases);
            Assert.Null(resolver.Resolve(FrameworkTenantId.ToTenantId(1)));   // the master tenant, known from the start
            Assert.Null(resolver.Resolve(FrameworkTenantId.ToTenantId(2)));
            Assert.Equal(own, resolver.Resolve(FrameworkTenantId.ToTenantId(3)));
            Assert.Null(resolver.Resolve(FrameworkTenantId.ToTenantId(4)));
            Assert.Null(resolver.Resolve(null));
            Assert.True(resolver.RoutesPerTenant);
            Assert.Equal(new[] { own }, resolver.RoutedDatabases);

            // Fail-closed: a tenant this host never resolved is refused, not sent to the installation's database.
            Assert.Throws<InvalidOperationException>(() => resolver.Resolve(FrameworkTenantId.ToTenantId(99)));
            Assert.Throws<InvalidOperationException>(() => resolver.Resolve(Stomme.Tenancy.Domain.TenantId.From(Guid.NewGuid())));

            await DropAsync(own);
            await DropAsync(never);
        }

        [Fact]
        public async Task A_tenant_on_another_server_is_refused()
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Planner().PlanAsync(Tenant(5), "Url=http://127.0.0.1:1;Database=elsewhere", () => Task.FromResult(false)));
            Assert.Contains("server", refused.Message, StringComparison.Ordinal);
            Assert.False(_databases.TryGet(5, out _));
        }

        public async Task DisposeAsync()
        {
            await DropAsync(_installation);
            _store.Dispose();
        }

        private async Task DropAsync(string database)
        {
            if (await _store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database)) is not null)
            {
                await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
            }
        }
    }
}
