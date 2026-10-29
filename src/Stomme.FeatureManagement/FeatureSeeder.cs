using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.FeatureManagement.Infrastructure;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.FeatureManagement;

/// <summary>
/// Brings the flag store up on startup: creates the module's schema if absent, then materialises the
/// declared feature catalog <b>for every tenant</b> (idempotent per tenant+key) — every flag a module
/// contributed via <see cref="IFeatureContributor"/> that has no backing row yet is created from its
/// descriptor, so a flag's declaration is enough for it to exist and be observable. Seeded flags are audited
/// with no principal (no one is signed in at startup).
/// <para>
/// <b>Why per tenant, and why that is not optional.</b> A <see cref="FeatureFlag"/> is a tenant-owned
/// aggregate, and the seam applies a tenant query filter to those. Seeding at
/// <see cref="TenantId.Default"/> only was correct while the host had no real tenant context — the module's
/// own fallback reported <see cref="TenantId.Default"/> for everyone, so the one seeded row was the row
/// everyone read. The moment the host registers a real tenancy adapter, a tenant reports its OWN id,
/// the filter stops matching those rows, <c>GetAllAsync</c> returns nothing, and every flag evaluates to
/// <c>false</c> — no error, no log, just capabilities silently switched off. Seeding per tenant is what makes
/// the adapter safe to register; the two belong in the same change.
/// </para>
/// <para>
/// The host's tenant 1 encodes to exactly <see cref="TenantId.Default"/> (see <c>FrameworkTenantId</c>), so
/// an existing single-tenant install's rows are already under the right key and this is a no-op for it —
/// tenants 2..n are the ones that gain rows.
/// </para>
/// </summary>
internal sealed class FeatureSeeder(IServiceProvider services, ILogger<FeatureSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // One scope kept open across the whole seed: it owns the store creation, the tenant lookup, and
            // the scope-factory reference. Resolving the ports from a scope (rather than the root provider)
            // keeps this correct whatever lifetime the host registers them at.
            using var scope = services.CreateScope();

            var registry = scope.ServiceProvider.GetRequiredService<IFeatureRegistry>();

            // The module owns its store, so it owns creating it. EnsureCreated is sufficient while the
            // feature store is a standalone database; when backlog item 01's phase 3 re-fronts the framework
            // store, these tables join the framework's migration pipeline instead.
            // <para>
            // GetService, not GetRequiredService: the relational composition registers a
            // FeatureManagementDbContext and this creates its schema, exactly as before. The RavenDB
            // composition registers NO DbContext — its documents' collections come into being on first write
            // and its database is created by the framework's RavenDB bootstrap — so a null context here means
            // "document store, nothing to EnsureCreated", not a fault.
            // </para>
            var context = scope.ServiceProvider.GetService<FeatureManagementDbContext>();
            if (context is not null)
            {
                await context.Database.EnsureCreatedAsync(cancellationToken);
            }

            if (registry.Known.Count == 0)
            {
                return;
            }

            var tenantScopes = scope.ServiceProvider.GetService<ITenantScopeFactory>();

            foreach (var tenantId in await ResolveTenantsAsync(scope.ServiceProvider, tenantScopes))
            {
                await SeedTenantAsync(tenantId, tenantScopes, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // A flag store that cannot start must not take the host down with it: every evaluation simply
            // resolves to off until the store is reachable.
            logger.LogError(ex, "Unable to initialize the feature flag store; features will evaluate as disabled.");
        }
    }

    /// <summary>
    /// The tenants to seed. Both ports are needed and are treated as one unit: without
    /// <see cref="ITenantScopeFactory"/> we could enumerate tenants but not enter their scope, so every write
    /// would land under whatever the ambient context happened to say — worse than not trying. When either is
    /// missing the host has no real tenancy (a minimal host, a test), the module's fallback context reports
    /// <see cref="TenantId.Default"/>, and that single logical tenant is exactly what to seed.
    /// <para>
    /// <b>Pre-install is not a failure.</b> On the wizard path the host boots BEFORE any database exists,
    /// this hosted service starts, and enumerating tenants reaches through <see cref="ITenantDirectory"/> to a
    /// master store that is not there yet — which throws. That is the EXPECTED, correct state: no site is served
    /// pre-install, so features being off is right by design, and the install (which every wizard completes with
    /// a host restart, see <c>InstallationManager.RestartApplication</c>) re-runs this seeder against the now-
    /// provisioned store on the next start. So a not-ready master is swallowed QUIETLY here and seeding is
    /// deferred — logging it at Error, as the outer catch used to, turned a benign boot state into noise
    /// indistinguishable from a real fault, the exact silent-failure-class trap this codebase warns about. A
    /// genuinely broken store post-install still surfaces loudly through the install pipeline and tenant
    /// resolution; this seeder is not that alarm, and the evaluator's fail-safe (a missing flag is off) covers
    /// correctness either way.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<TenantId>> ResolveTenantsAsync(IServiceProvider scoped, ITenantScopeFactory? tenantScopes)
    {
        var directory = scoped.GetService<ITenantDirectory>();
        if (directory is null || tenantScopes is null)
        {
            return [TenantId.Default];
        }

        IReadOnlyList<TenantId> tenants;
        try
        {
            tenants = await directory.GetTenantIdsAsync();
        }
        catch (Exception ex)
        {
            // The master store is not reachable yet — the pre-install boot. Defer to the post-install restart,
            // quietly: this is an expected boot state, not a fault (see the remarks above).
            logger.LogDebug(ex, "Feature flag store: the master store is not ready (pre-install boot); deferring seeding until after installation.");
            return [];
        }

        // Empty before the master store has tenants (an install that has not run yet). Seeding the default
        // keeps the pre-install/minimal case behaving as it did.
        return tenants.Count > 0 ? tenants : [TenantId.Default];
    }

    /// <summary>
    /// Seeds one tenant. The tenant scope is entered BEFORE the DI scope is created, so the
    /// <see cref="ITenantContext"/> and <c>DbContext</c> resolved inside it observe this tenant from birth —
    /// the reads (the tenant filter behind <c>GetAllAsync</c>) and the writes agree, and each tenant gets a
    /// fresh change tracker rather than inheriting the previous tenant's.
    /// </summary>
    private async Task SeedTenantAsync(TenantId tenantId, ITenantScopeFactory? tenantScopes, CancellationToken cancellationToken)
    {
        using var tenantScope = tenantScopes?.BeginScope(tenantId.Value);
        using var scope = services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IFeatureRegistry>();
        var flags = scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Filtered to this tenant by the seam's tenant query filter, so "already seeded" is asked and
        // answered per tenant.
        var existing = (await flags.GetAllAsync(cancellationToken))
            .Select(f => f.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        foreach (var descriptor in registry.Known.Where(d => !existing.Contains(d.Key)))
        {
            await flags.AddAsync(
                FeatureFlag.Create(
                    Guid.NewGuid(), tenantId, descriptor.Key, descriptor.DefaultEnabled,
                    descriptor.RequiredEntitlement, descriptor.RequiredFlag),
                cancellationToken);
            created++;
        }

        if (created > 0)
        {
            // Drains the tracked aggregates and dispatches their events, writing the audit trail.
            await unitOfWork.SaveChangesAsync(cancellationToken);

            // The seeder is a flag-write path like any other: drop the tenant's evaluator-cache
            // entry after the writes committed, so an evaluation that ran before seeding cannot keep
            // answering from a pre-seed (empty) flag list.
            scope.ServiceProvider.GetRequiredService<IFeatureFlagCache>().Invalidate(tenantId);
            logger.LogInformation("Seeded {Count} declared feature flag(s) for tenant {TenantId}.", created, tenantId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
