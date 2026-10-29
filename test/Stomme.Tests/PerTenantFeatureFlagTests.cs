using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The trap the tenancy adapter had to defuse in the same change, proven by running it.
    /// <para>
    /// Brief 06 seeds feature flags at <see cref="TenantId.Default"/> — correct while the host had no real
    /// tenant context, because the module's fallback reported <see cref="TenantId.Default"/> for everybody.
    /// Registering a REAL tenant adapter makes each tenant report its own id, at which point the seam's tenant
    /// query filter stops matching those rows. The evaluator's response to a missing flag row is
    /// <c>return false</c> — so the failure is not an error, it is EVERY FLAG SILENTLY OFF, which is strictly
    /// worse than the honest single-tenant scope it replaced.
    /// </para>
    /// <para>
    /// These tests wire the REAL adapter (<see cref="StommeTenantContext"/> + <see cref="TenantScopeFactory"/>),
    /// the REAL seeder, the REAL EF repositories and the REAL evaluator over a throwaway SQLite file, across
    /// three tenants. Nothing is faked but the tenant directory (which would otherwise need a master store) and
    /// the principal. <see cref="Every_flag_is_off_for_a_tenant_that_was_not_seeded"/> is the negative control:
    /// it reproduces the trap, so the passing tests above it are not passing vacuously.
    /// </para>
    /// </summary>
    public sealed class PerTenantFeatureFlagTests : IDisposable
    {
        private readonly List<string> _databases = [];

        // Both flags Stomme declares default to ENABLED (FeatureCatalog), which is what makes them a usable
        // probe: "true" can only come from a real seeded row that the tenant filter matched, while the trap's
        // signature is a uniform, silent "false".
        private const string EmailFlag = FeatureCatalog.EmailNotifications;
        private const string VisitorFlag = FeatureCatalog.VisitorTracking;

        // The framework's tenant 1 is "Master", created by every install. It must land on TenantId.Default so
        // an existing single-tenant install's rows stay visible with no migration.
        private static readonly TenantId Tenant1 = FrameworkTenantId.ToTenantId(1);
        private static readonly TenantId Tenant2 = FrameworkTenantId.ToTenantId(2);
        private static readonly TenantId Tenant3 = FrameworkTenantId.ToTenantId(3);

        [Fact]
        public async Task Every_tenant_gets_its_own_seeded_flags_and_they_evaluate_to_their_real_value()
        {
            using var host = NewHost(Tenant1, Tenant2, Tenant3);
            await host.RunSeederAsync();

            foreach (var tenant in new[] { Tenant1, Tenant2, Tenant3 })
            {
                // The real evaluator, under the real adapter, in this tenant's scope. `true` is the declared
                // default; `false` would be the trap.
                Assert.True(await host.IsEnabledAsync(EmailFlag, tenant), $"{EmailFlag} evaluated OFF for tenant {tenant}");
                Assert.True(await host.IsEnabledAsync(VisitorFlag, tenant), $"{VisitorFlag} evaluated OFF for tenant {tenant}");
            }
        }

        // Compatibility with what is already on disk: an install seeded before the adapter existed has its rows
        // at TenantId.Default, and tenant 1 must still read exactly those.
        [Fact]
        public async Task Tenant_one_reads_the_rows_seeded_at_the_seam_default()
        {
            using var host = NewHost(Tenant1);
            await host.RunSeederAsync();

            Assert.Equal(TenantId.Default, Tenant1);

            var rows = await host.FlagsForAsync(Tenant1);
            // One row per DECLARED flag: the core catalogue plus every discovered contributor's (was a literal 2; BL-78
            // added notifications.dispatch; BL-94 increment 12 added the first in-tree module contributor, datasets.mcp,
            // which a core-only count could not see).
            Assert.Equal(host.DeclaredFlagCount(), rows.Count);
            Assert.True(rows.Count > FeatureCatalog.All.Count, "the contributed flags were not seeded");
            Assert.All(rows, row => Assert.Equal(TenantId.Default, row.TenantId));
        }

        // Seeding is per tenant AND idempotent per tenant: a restart must not duplicate rows.
        [Fact]
        public async Task Seeding_is_idempotent_per_tenant_across_restarts()
        {
            using var host = NewHost(Tenant1, Tenant2);

            await host.RunSeederAsync();
            await host.RunSeederAsync();

            Assert.Equal(host.DeclaredFlagCount(), (await host.FlagsForAsync(Tenant1)).Count);
            Assert.Equal(host.DeclaredFlagCount(), (await host.FlagsForAsync(Tenant2)).Count);
        }

        // Tenants must not see each other's flags: the whole point of the filter.
        [Fact]
        public async Task A_tenants_flags_are_its_own()
        {
            using var host = NewHost(Tenant1, Tenant2);
            await host.RunSeederAsync();

            var tenant1Ids = (await host.FlagsForAsync(Tenant1)).Select(f => f.Id).ToHashSet();
            var tenant2Ids = (await host.FlagsForAsync(Tenant2)).Select(f => f.Id).ToHashSet();

            Assert.NotEmpty(tenant1Ids);
            Assert.NotEmpty(tenant2Ids);
            Assert.Empty(tenant1Ids.Intersect(tenant2Ids));
        }

        // Toggling for one tenant must not move another tenant's flag — proves the writes are scoped too, not
        // just the reads.
        [Fact]
        public async Task Toggling_a_flag_for_one_tenant_leaves_the_others_alone()
        {
            using var host = NewHost(Tenant1, Tenant2);
            await host.RunSeederAsync();

            await host.SetEnabledAsync(EmailFlag, Tenant2, enabled: false);

            Assert.True(await host.IsEnabledAsync(EmailFlag, Tenant1));
            Assert.False(await host.IsEnabledAsync(EmailFlag, Tenant2));
        }

        // ---- NEGATIVE CONTROL --------------------------------------------------------------------------
        // The trap itself, reproduced. Seed ONLY the default tenant (what the seeder did before this change),
        // then read as tenant 2 through the real adapter: no row matches the filter, and the evaluator answers
        // false for every flag — no exception, no log. If per-tenant seeding regresses, the tests above turn
        // into this. That is why they are meaningful.
        [Fact]
        public async Task Every_flag_is_off_for_a_tenant_that_was_not_seeded()
        {
            using var host = NewHost(Tenant1, Tenant2);

            // Emulate the pre-D16 seeder: the default tenant only.
            await host.SeedOnlyAsync(TenantId.Default);

            Assert.True(await host.IsEnabledAsync(EmailFlag, Tenant1));
            Assert.True(await host.IsEnabledAsync(VisitorFlag, Tenant1));

            Assert.False(await host.IsEnabledAsync(EmailFlag, Tenant2));
            Assert.False(await host.IsEnabledAsync(VisitorFlag, Tenant2));
            Assert.Empty(await host.FlagsForAsync(Tenant2));
        }

        private TenantFlagHost NewHost(params TenantId[] tenants)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"stomme-tenant-flags-{Guid.NewGuid():N}.db");
            _databases.Add(databasePath);
            return new TenantFlagHost(databasePath, tenants);
        }

        public void Dispose()
        {
            foreach (var database in _databases)
            {
                // Each host's own pool, by the exact string TenantFlagHost handed to UseSqlite. ClearAllPools is
                // process-wide and would reach into every other suite's pool.
                ScratchSqlite.Release($"Data Source={database}");

                try
                {
                    File.Delete(database);
                }
                catch (IOException)
                {
                    // A leftover temp file is not worth failing a green run over.
                }
            }
        }
    }

    /// <summary>
    /// The flag module composed the way Stomme's host composes it — the real AddFeatureManagement over a real
    /// SQLite store — but with the tenancy adapter registered FIRST, exactly as
    /// <c>AddStommeTenancy()</c> does ahead of the assembly scan, so the module's single-tenant fallback
    /// stands down and the real adapter is what everything reads.
    /// </summary>
    internal sealed class TenantFlagHost : IDisposable
    {
        private readonly ServiceProvider _services;

        internal TenantFlagHost(string databasePath, IReadOnlyList<TenantId> tenants)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));

            // The real adapter. StubAliasAccessor returns no alias and SiteState is empty, which is precisely
            // the seeder's situation (startup: no request, no alias) — so the tenant can only ever come from an
            // ambient scope, and a scoping bug shows up as a failure rather than an accident.
            services.AddScoped<SiteState>();
            services.AddScoped<IAliasAccessor>(_ => new StubAliasAccessor(alias: null));
            services.AddScoped<ITenantContext, StommeTenantContext>();
            services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
            services.AddScoped<ITenantDirectory>(_ => new StubTenantDirectory(tenants));
            services.AddSingleton<IFeatureContextProvider>(new AnonymousProvider());

            services.AddFeatureManagement(options => options.UseSqlite($"Data Source={databasePath}"));

            _services = services.BuildServiceProvider(new ServiceProviderOptions
            {
                // The failure mode this repo has already shipped once: a scoped service resolved from the root.
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
        }

        private ITenantScopeFactory Scopes => _services.GetRequiredService<ITenantScopeFactory>();

        /// <summary>Runs the real <c>FeatureSeeder</c> hosted service, as the host does at startup.</summary>
        internal async Task RunSeederAsync()
        {
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        /// <summary>The pre-D16 behaviour, for the negative control: seed one tenant and no other.</summary>
        internal async Task SeedOnlyAsync(TenantId tenantId)
        {
            using var tenantScope = Scopes.BeginScope(tenantId.Value);
            using var scope = _services.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<Stomme.FeatureManagement.Infrastructure.FeatureManagementDbContext>();
            await context.Database.EnsureCreatedAsync();

            var flags = scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            foreach (var descriptor in FeatureCatalog.All)
            {
                await flags.AddAsync(
                    FeatureFlag.Create(
                        Guid.NewGuid(), tenantId, descriptor.Key, descriptor.DefaultEnabled,
                        descriptor.RequiredEntitlement, descriptor.RequiredFlag));
            }

            await unitOfWork.SaveChangesAsync();
        }

        /// <summary>Evaluates a flag as <paramref name="tenantId"/>, through the real evaluator.</summary>
        internal Task<bool> IsEnabledAsync(string key, TenantId tenantId) =>
            InTenantScopeAsync(tenantId, sp => sp.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key));

        /// <summary>Every flag the composed contributors declare, distinct by key - what the seeder writes per tenant.</summary>
        internal int DeclaredFlagCount() =>
            _services.GetServices<IFeatureContributor>().SelectMany(contributor => contributor.Features).Select(feature => feature.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        internal Task<IReadOnlyList<FeatureFlag>> FlagsForAsync(TenantId tenantId) =>
            InTenantScopeAsync(tenantId, sp => sp.GetRequiredService<IRepository<FeatureFlag, Guid>>().GetAllAsync());

        internal async Task SetEnabledAsync(string key, TenantId tenantId, bool enabled) =>
            await InTenantScopeAsync(tenantId, async sp =>
            {
                var flags = sp.GetRequiredService<IRepository<FeatureFlag, Guid>>();
                var unitOfWork = sp.GetRequiredService<IUnitOfWork>();

                var flag = (await flags.GetAllAsync()).Single(f => f.Key == key);
                flag.Set(enabled);
                await flags.UpdateAsync(flag);
                await unitOfWork.SaveChangesAsync();

                return true;
            });

        // The tenant scope is entered BEFORE the DI scope, exactly as the seeder does it, so the context and
        // DbContext resolved inside observe this tenant from birth.
        private async Task<T> InTenantScopeAsync<T>(TenantId tenantId, Func<IServiceProvider, Task<T>> action)
        {
            using var tenantScope = Scopes.BeginScope(tenantId.Value);
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        public void Dispose() => _services.Dispose();
    }

    internal sealed class StubTenantDirectory(IReadOnlyList<TenantId> tenants) : ITenantDirectory
    {
        public Task<IReadOnlyList<TenantId>> GetTenantIdsAsync() => Task.FromResult(tenants);
    }

    internal sealed class AnonymousProvider : IFeatureContextProvider
    {
        public FeatureContext GetContext() => FeatureContext.Anonymous;
    }
}
