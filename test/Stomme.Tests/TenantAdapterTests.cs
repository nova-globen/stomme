using System;
using System.Collections.Generic;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests
{
    // The tenancy adapter that four backlog items blocked on. These cover the two things that are easy
    // to get wrong and impossible to see at a glance: the int<->GUID encoding (whose exact shape is what makes
    // the change safe to ship at all), and the fail-closed default (whose opposite is a cross-tenant leak).
    public class FrameworkTenantIdTests
    {
        // THE load-bearing assertion of this whole change. Stomme's first tenant is always 1 ("Master"), and
        // every flag an existing install has seeded is keyed TenantId.Default. If the encoding ever stops
        // mapping 1 -> Default, those rows fall outside the tenant query filter and EVERY FLAG SILENTLY READS
        // false — no error, no log. This test is the tripwire for that; it must fail loudly rather than let the
        // flags go dark.
        [Fact]
        public void Framework_tenant_one_encodes_to_the_seam_default_tenant()
        {
            Assert.Equal(TenantId.Default, FrameworkTenantId.ToTenantId(1));
            Assert.Equal("00000000-0000-0000-0000-000000000001", FrameworkTenantId.ToTenantId(1).ToString());
            Assert.True(FrameworkTenantId.MapsDefaultTenantToSeamDefault);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(42)]
        [InlineData(1000)]
        [InlineData(int.MaxValue)]
        public void Encoding_round_trips(int frameworkTenantId)
        {
            var tenantId = FrameworkTenantId.ToTenantId(frameworkTenantId);

            Assert.True(FrameworkTenantId.TryToTenantId(tenantId, out var decoded));
            Assert.Equal(frameworkTenantId, decoded);
        }

        [Fact]
        public void Distinct_tenants_encode_to_distinct_ids()
        {
            var ids = new HashSet<TenantId>();

            for (var i = 1; i <= 500; i++)
            {
                Assert.True(ids.Add(FrameworkTenantId.ToTenantId(i)), $"tenant {i} collided with an earlier id");
            }
        }

        // 0 would encode to Guid.Empty (which TenantId.From rejects) and -1 is TenantManager's "no alias"
        // sentinel. Neither is a tenant, and both must be rejected rather than quietly encoded.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void Non_tenant_keys_are_rejected(int frameworkTenantId)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameworkTenantId.ToTenantId(frameworkTenantId));
            Assert.Null(FrameworkTenantId.ToTenantIdOrNull(frameworkTenantId));
        }

        // A tenant id minted elsewhere over the same seam is not ours to decode. Reading an int out of its
        // trailing bytes would silently point a connection resolver at the wrong tenant's database.
        [Fact]
        public void A_foreign_tenant_id_is_not_decoded()
        {
            var foreign = TenantId.From(Guid.NewGuid());

            Assert.False(FrameworkTenantId.TryToTenantId(foreign, out var decoded));
            Assert.Equal(0, decoded);
            Assert.False(FrameworkTenantId.TryToTenantId(null, out _));
        }
    }

    public abstract class TenantAdapterTestBase
    {
        protected static StommeTenantContext NewContext(StubAliasAccessor aliasAccessor, SiteState siteState) =>
            new(aliasAccessor, siteState);
    }

    public class TenantScopeFactoryTests : TenantAdapterTestBase
    {
        [Fact]
        public void A_scope_sets_the_tenant_and_restores_the_previous_one_on_dispose()
        {
            var factory = new TenantScopeFactory();
            var context = NewContext(new StubAliasAccessor(alias: null), new SiteState());

            // No scope, no alias: fails closed (see StommeTenantContext).
            Assert.Null(context.CurrentTenantId);
            Assert.False(context.IsSystemScope);

            using (factory.BeginScope(FrameworkTenantId.ToTenantId(7).Value))
            {
                Assert.Equal(FrameworkTenantId.ToTenantId(7), context.CurrentTenantId);
                Assert.False(context.IsSystemScope);

                // Nested scopes must stack, not clobber — the outbox can dispatch a tenant's event while the
                // seeder holds a scope of its own.
                using (factory.BeginScope(FrameworkTenantId.ToTenantId(9).Value))
                {
                    Assert.Equal(FrameworkTenantId.ToTenantId(9), context.CurrentTenantId);
                }

                Assert.Equal(FrameworkTenantId.ToTenantId(7), context.CurrentTenantId);
            }

            Assert.Null(context.CurrentTenantId);
        }

        // BeginScope(null) is the ONLY way into system scope, and it must not fall through to the alias.
        [Fact]
        public void A_null_scope_is_an_explicit_system_scope()
        {
            var factory = new TenantScopeFactory();
            var context = NewContext(new StubAliasAccessor(AliasFor(tenantId: 3)), new SiteState());

            Assert.Equal(FrameworkTenantId.ToTenantId(3), context.CurrentTenantId);
            Assert.False(context.IsSystemScope);

            using (factory.BeginScope(null))
            {
                Assert.True(context.IsSystemScope);
                Assert.Null(context.CurrentTenantId);
            }

            Assert.False(context.IsSystemScope);
        }

        internal static Alias AliasFor(int tenantId) => new() { AliasId = 1, SiteId = 1, TenantId = tenantId, Name = "localhost" };
    }

    public class StommeTenantContextTests : TenantAdapterTestBase
    {
        // THE REGRESSION THIS ADAPTER ALMOST SHIPPED WITH. Background jobs run per tenant via
        // HostedServiceBase, which calls ITenantManager.SetTenant(id) -> SiteState gets a MOCK alias
        // (AliasId = -1) carrying only the real TenantId. A job has no HttpContext and therefore no request
        // alias at all, so an adapter built on the request alias alone reports "no tenant" in every job, fails
        // closed, and every flag a job reads evaluates false — NotificationJob stops sending e-mail, PurgeJob
        // stops tracking visitors, on every install, with nothing in the log but "disabled by feature flag".
        // Reading SiteState first is the fix; this pins it.
        [Fact]
        public void A_background_jobs_mock_alias_still_yields_its_tenant()
        {
            // Exactly what TenantManager.SetTenant(4) puts on SiteState.
            var siteState = new SiteState { Alias = new Alias { TenantId = 4, AliasId = -1, SiteId = -1 } };

            // The request alias is null here, as it is inside a real job (no HttpContext) — so the tenant can
            // ONLY come from SiteState.
            var context = NewContext(new StubAliasAccessor(alias: null), siteState);

            Assert.Equal(FrameworkTenantId.ToTenantId(4), context.CurrentTenantId);
            Assert.False(context.IsSystemScope);
        }

        // SiteState is the first source, so a populated one must not cost a resolution call at all.
        [Fact]
        public void A_populated_site_state_short_circuits_alias_resolution()
        {
            var siteState = new SiteState { Alias = TenantScopeFactoryTests.AliasFor(tenantId: 6) };
            var aliasAccessor = new StubAliasAccessor(TenantScopeFactoryTests.AliasFor(tenantId: 5));

            var context = NewContext(aliasAccessor, siteState);

            Assert.Equal(FrameworkTenantId.ToTenantId(6), context.CurrentTenantId);
            Assert.Equal(0, aliasAccessor.AliasReads);
        }

        // The everyday path: a request resolved an alias, the alias names a tenant, the seam sees that tenant.
        [Fact]
        public void The_tenant_comes_from_the_resolved_alias()
        {
            var context = NewContext(new StubAliasAccessor(TenantScopeFactoryTests.AliasFor(tenantId: 5)), new SiteState());

            Assert.Equal(FrameworkTenantId.ToTenantId(5), context.CurrentTenantId);
            Assert.False(context.IsSystemScope);
        }

        // An explicit scope outranks the alias: the seeder/outbox state the tenant, they do not infer it.
        [Fact]
        public void An_ambient_scope_outranks_the_alias()
        {
            var factory = new TenantScopeFactory();
            var context = NewContext(new StubAliasAccessor(TenantScopeFactoryTests.AliasFor(tenantId: 5)), new SiteState());

            using (factory.BeginScope(FrameworkTenantId.ToTenantId(8).Value))
            {
                Assert.Equal(FrameworkTenantId.ToTenantId(8), context.CurrentTenantId);
            }
        }

        // The security-relevant default. The seam's filter is `SystemScope || TenantId == CurrentTenant`, so
        // reporting system scope here would make an alias-less code path read EVERY tenant's rows. It must
        // fail closed (match nothing) instead.
        [Fact]
        public void No_alias_means_no_tenant_and_not_system_scope()
        {
            var context = NewContext(new StubAliasAccessor(alias: null), new SiteState());

            Assert.Null(context.CurrentTenantId);
            Assert.False(context.IsSystemScope);
        }

        // A mock/sentinel alias (TenantManager.SetTenant uses AliasId/SiteId -1) with no real tenant key is
        // not a tenant.
        [Fact]
        public void A_sentinel_alias_with_no_tenant_yields_no_tenant()
        {
            var context = NewContext(new StubAliasAccessor(new Alias { AliasId = -1, SiteId = -1, TenantId = 0 }), new SiteState());

            Assert.Null(context.CurrentTenantId);
        }

        // Found while running an extract-from-connection job against RavenDB. The engine's worker runs a dispatched run in a
        // scope with NO alias; a step that needs its site sets one on SiteState (StommeSiteScope, as every headless job
        // does). The context had already answered "no tenant" to an earlier read in that scope and cached it for the scope's
        // life, so every tenant-owned read after it failed closed - the site's connection "was not found". It now follows
        // SiteState's alias whenever that changes; without any alias it still fails closed.
        [Fact]
        public void An_alias_set_on_site_state_after_a_read_with_none_is_the_tenant_from_then_on()
        {
            var siteState = new SiteState();
            var context = NewContext(new StubAliasAccessor(alias: null), siteState);

            Assert.Null(context.CurrentTenantId);
            Assert.False(context.IsSystemScope);

            siteState.Alias = new Alias { TenantId = 4, AliasId = -1, SiteId = 1 }; // what TenantManager.SetAliasAsync puts there in a job

            Assert.Equal(FrameworkTenantId.ToTenantId(4), context.CurrentTenantId);
            Assert.False(context.IsSystemScope);
        }

        [Fact]
        public void A_scope_whose_site_state_alias_changes_reports_the_new_tenant()
        {
            var siteState = new SiteState { Alias = TenantScopeFactoryTests.AliasFor(tenantId: 5) };
            var context = NewContext(new StubAliasAccessor(alias: null), siteState);
            Assert.Equal(FrameworkTenantId.ToTenantId(5), context.CurrentTenantId);

            siteState.Alias = TenantScopeFactoryTests.AliasFor(tenantId: 6);

            Assert.Equal(FrameworkTenantId.ToTenantId(6), context.CurrentTenantId);
        }

        // The REQUEST's alias cannot change inside a scope; the filter reads this property on every query, so the request
        // alias is read once, however often the tenant is asked for.
        [Fact]
        public void The_alias_is_resolved_once_per_scope()
        {
            var aliasAccessor = new StubAliasAccessor(TenantScopeFactoryTests.AliasFor(tenantId: 5));
            var context = NewContext(aliasAccessor, new SiteState());

            _ = context.CurrentTenantId;
            _ = context.CurrentTenantId;
            _ = context.CurrentTenantId;

            Assert.Equal(1, aliasAccessor.AliasReads);
        }
    }

    /// <summary>
    /// Stands in for the request's alias — what TenantMiddleware publishes to HttpContext.Items and
    /// StommeTenantContext reads back synchronously. Counts reads so the tests can pin that a
    /// populated SiteState costs no read at all, and that the alias is read once per scope.
    /// </summary>
    public sealed class StubAliasAccessor : IAliasAccessor
    {
        private readonly Alias? _alias;

        public StubAliasAccessor(Alias? alias) => _alias = alias;

        public int AliasReads { get; private set; }

        public Alias Alias
        {
            get
            {
                AliasReads++;
                return _alias!; // a null alias is a deliberate test input; the '!' is compile-time only, runtime is unchanged
            }
        }
    }
}
