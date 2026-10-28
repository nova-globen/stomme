using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// THE DATA-LEAK PIN for D37. <c>DbContext.OnConfiguring</c> is a sync EF platform virtual that must read
    /// no store, so it routes via <c>ITenantManager.GetResolvedTenant()</c> -> <c>ITenantStoreBindings</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS FILE EXISTS. Five loops switch tenant MID-SCOPE and build a DbContext after each switch
    /// (ModuleDefinitionController:201, DatabaseManager:405/501/698, HostedServiceBase:171). The tempting
    /// implementation — a holder that resolves "this scope's tenant" once and caches it — routes EVERY tenant
    /// in those loops at whichever tenant warmed the scope first. That is a cross-tenant read against a REAL
    /// connection string: it does not throw, it does not log, it returns another tenant's rows. This project
    /// has already shipped one silent wrong-tenant bug (per-tenant flag seeding), which is why the leak gets a
    /// test and not a comment.
    /// </para>
    /// <para>
    /// These tests drive the REAL <c>TenantManager</c> and the REAL <c>TenantStoreBindings</c>. Only the
    /// repositories are stubbed, and the tenant stub COUNTS READS — because "OnConfiguring touches no store"
    /// is the whole decision, and a test that let a read through silently would be pinning nothing.
    /// </para>
    /// </remarks>
    public class TenantRoutingIsNotLeakyTests
    {
        private static readonly Tenant TenantA = new() { TenantId = 1, Name = "A", DBConnectionString = "ConnA", DBType = "TypeA" };
        private static readonly Tenant TenantB = new() { TenantId = 2, Name = "B", DBConnectionString = "ConnB", DBType = "TypeB" };

        private static (TenantManager Manager, CountingTenantRepository Tenants) NewManager(params Tenant[] tenants)
        {
            var repository = new CountingTenantRepository(tenants);
            var manager = new TenantManager(
                new HttpContextAccessor { HttpContext = null },
                new StubAliasRepository(),
                repository,
                new SiteState(),
                new TenantStoreBindings());

            return (manager, repository);
        }

        // THE LEAK, STATED AS A TEST. This is exactly HostedServiceBase's and DatabaseManager's loop shape:
        // one scope, one manager, SetTenant per iteration, a context built after each switch. If routing were
        // cached per scope, the second assertion would return TenantA's connection string.
        [Fact]
        public void Switching_tenant_mid_scope_routes_each_context_at_its_own_tenant()
        {
            var (manager, _) = NewManager();

            manager.SetTenant(TenantA);
            Assert.Equal("ConnA", manager.GetResolvedTenant().DBConnectionString);

            manager.SetTenant(TenantB);
            Assert.Equal("ConnB", manager.GetResolvedTenant().DBConnectionString);

            // ...and back, because a loop that revisits a tenant must not get the last one either.
            manager.SetTenant(TenantA);
            Assert.Equal("ConnA", manager.GetResolvedTenant().DBConnectionString);
        }

        // The other half of D37: the routing read must not reach a store. If GetResolvedTenant ever queries,
        // the sync platform virtual is touching a store again and the decision has silently regressed.
        [Fact]
        public void The_routing_read_never_touches_the_store()
        {
            var (manager, tenants) = NewManager();

            manager.SetTenant(TenantA);
            for (var i = 0; i < 10; i++)
            {
                manager.GetResolvedTenant();
            }

            manager.SetTenant(TenantB);
            manager.GetResolvedTenant();

            Assert.Equal(0, tenants.Reads);
        }

        // FAIL CLOSED. An alias naming a tenant nobody warmed must THROW, never fall back to a tenant that
        // happens to be in the table. Routing at the wrong tenant silently is strictly worse than a crash.
        [Fact]
        public void An_alias_naming_an_unwarmed_tenant_throws_rather_than_routing_at_a_neighbour()
        {
            var siteState = new SiteState();
            var manager = new TenantManager(
                new HttpContextAccessor { HttpContext = null },
                new StubAliasRepository(),
                new CountingTenantRepository(),
                siteState,
                new TenantStoreBindings());

            // TenantA is warmed and present in the table...
            manager.SetTenant(TenantA);

            // ...and now the alias is moved to a tenant that was never warmed, which is what a missed warm
            // point looks like from inside OnConfiguring.
            siteState.Alias = new Alias { TenantId = 99, AliasId = -1, SiteId = -1 };

            var ex = Assert.Throws<InvalidOperationException>(() => manager.GetResolvedTenant());

            // The message must name the tenant and say what was warmed, or the throw is just a crash.
            Assert.Contains("99", ex.Message);
            Assert.Contains("1", ex.Message);
        }

        // The installer state. No alias is NOT a miss: OnConfiguring leaves the context unconfigured, which is
        // how the app boots with an empty DB config and serves the wizard. Throwing here would break install.
        [Fact]
        public void No_alias_yields_no_tenant_rather_than_throwing()
        {
            var (manager, _) = NewManager();

            Assert.Null(manager.GetResolvedTenant());
        }

        // SetTenant warms from the object the caller already holds, so it must cost no read at all. This is
        // what makes the sync SetTenant(Tenant) overload legitimate rather than a loophole.
        [Fact]
        public void Setting_a_tenant_from_the_object_the_caller_holds_costs_no_read()
        {
            var (manager, tenants) = NewManager();

            manager.SetTenant(TenantA);
            manager.SetTenant(TenantB);

            Assert.Equal(0, tenants.Reads);
        }

        // The async warm point: SetAliasAsync has an alias but no Tenant, so it reads the (cached) store ONCE
        // and the routing read that follows is free. This is the request path's shape.
        [Fact]
        public async Task Warming_by_alias_reads_the_store_once_and_then_routes_free()
        {
            var (manager, tenants) = NewManager(TenantA, TenantB);

            await manager.SetAliasAsync(new Alias { TenantId = 2, AliasId = 5, SiteId = 1, Name = "b.example" });

            var readsAfterWarm = tenants.Reads;
            Assert.Equal("ConnB", manager.GetResolvedTenant().DBConnectionString);

            // The warm read the store; the routing read did not.
            Assert.True(readsAfterWarm >= 1, "SetAliasAsync should have resolved the tenant from the store.");
            Assert.Equal(readsAfterWarm, tenants.Reads);
        }

        private sealed class CountingTenantRepository : ITenantRepository
        {
            private readonly List<Tenant> _tenants;

            public CountingTenantRepository(params Tenant[] tenants) => _tenants = tenants.ToList();

            public int Reads { get; private set; }

            public Task<IEnumerable<Tenant>> GetTenantsAsync()
            {
                Reads++;
                return Task.FromResult<IEnumerable<Tenant>>(_tenants);
            }

            public Task<Tenant?> GetTenantAsync(int tenantId)
            {
                Reads++;
                return Task.FromResult(_tenants.FirstOrDefault(t => t.TenantId == tenantId));
            }

            public Task<Tenant> AddTenantAsync(Tenant tenant) => throw new NotSupportedException();
            public Task<Tenant> UpdateTenantAsync(Tenant tenant) => throw new NotSupportedException();
            public Task DeleteTenantAsync(int tenantId) => throw new NotSupportedException();
        }

        private sealed class StubAliasRepository : IAliasRepository
        {
            public Task<IEnumerable<Alias>> GetAliasesAsync() => Task.FromResult<IEnumerable<Alias>>(new List<Alias>());
            public Task<Alias> GetAliasAsync(int aliasId) => Task.FromResult<Alias>(null!);
            public Task<Alias> GetAliasAsync(int aliasId, bool tracking) => Task.FromResult<Alias>(null!);
            public Task<Alias> GetAliasAsync(string name) => Task.FromResult<Alias>(null!);
            public Task<Alias> AddAliasAsync(Alias alias) => throw new NotSupportedException();
            public Task<Alias> UpdateAliasAsync(Alias alias) => throw new NotSupportedException();
            public Task DeleteAliasAsync(int aliasId) => throw new NotSupportedException();
        }
    }
}
