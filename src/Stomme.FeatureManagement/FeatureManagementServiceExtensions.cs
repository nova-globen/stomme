using System.Reflection;
using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.FeatureManagement.Infrastructure;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

namespace Stomme.FeatureManagement;

/// <summary>Registers the Feature Management module: its store, evaluator, registry, and admin surface.</summary>
public static class FeatureManagementServiceExtensions
{
    /// <summary>
    /// Wires the feature-flag system over the persistence seam (backlog item 01) using the relational
    /// (EF Core) provider. <paramref name="configureStore"/> applies the provider to the module's own
    /// <see cref="FeatureManagementDbContext"/> — e.g. <c>options =&gt; options.UseSqlite(connectionString)</c>.
    /// Discovers every <see cref="IFeatureContributor"/> across the loaded <c>Stomme.*</c> assemblies, the
    /// same reflection seam that discovers <c>IServerStartup</c>.
    /// </summary>
    public static IServiceCollection AddFeatureManagement(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configureStore)
    {
        ArgumentNullException.ThrowIfNull(configureStore);

        // The module's own store: a scoped factory, plus one context per scope built from it. Registering
        // the factory alone (rather than AddDbContext + AddDbContextFactory, which each register
        // DbContextOptions<T> at a different lifetime) keeps a single options registration, and mirrors the
        // shape the seam's own AddTenantAwareDbContext uses. The container owns the per-scope context, so it
        // disposes it.
        services.AddDbContextFactory<FeatureManagementDbContext>(configureStore, ServiceLifetime.Scoped);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FeatureManagementDbContext>>().CreateDbContext());

        services.AddEfCorePersistence<FeatureManagementDbContext>();
        services.AddEfDocumentSessions<FeatureManagementDbContext>();

        // The module's two aggregates are read through NON-tracking repositories. The open generic above tracks,
        // and MEASURED: that made the admin list go stale in a long-lived DI scope: list, toggle from
        // another scope, list again -> the OLD value. Its one consumer today is an HTTP controller, a scope per request,
        // so nothing showed - but a server-side IFeatureService, or a module calling the admin surface from a component,
        // would have inherited it silently. A CLOSED registration wins over the open generic in MS.DI whatever the load
        // order, and an open generic cannot carry an initializer, which is why these two are spelled out. Both
        // aggregates persist as scalar columns (the overrides are ONE converted column), so a detached update writes
        // the whole row and cannot orphan a child.
        services.AddScoped<IRepository<FeatureFlag, Guid>>(sp => NonTracking<FeatureFlag>(sp));
        services.AddScoped<IRepository<FeatureFlagAudit, Guid>>(sp => NonTracking<FeatureFlagAudit>(sp));

        // The provider-neutral half of the module: the evaluator, admin surface, audit trail, contributor
        // discovery and seeder. Everything above this line is the RELATIONAL store; everything the core wires
        // reads and writes through the open-generic IRepository<,> the store provider owns (EfRepository<,>
        // here).
        return services.AddFeatureManagementCore();
    }

    private static EfRepository<TAggregate, Guid> NonTracking<TAggregate>(IServiceProvider services)
        where TAggregate : AggregateRoot<Guid> =>
        new(
            services.GetRequiredService<FeatureManagementDbContext>(),
            services.GetRequiredService<IAggregateTracker>(),
            services.GetRequiredService<ITenantContext>(),
            services.GetServices<IDomainEventSerializer>())
        {
            TrackReads = false,
        };

    /// <summary>
    /// The provider-neutral registrations shared by every store the feature-flag system can run on: the
    /// tenancy/principal fallbacks, current-scope domain-event dispatch, the unit of work, the audit handler,
    /// the evaluator, the admin surface, the standard <see cref="IFeatureManager"/>, contributor discovery,
    /// the registry and the startup seeder. It deliberately wires NO store — the caller registers the store
    /// provider (the open-generic <see cref="IRepository{TAggregate, TId}"/> plus its read seam).
    /// <see cref="AddFeatureManagement"/> is the relational (EF Core) composition; a RavenDB install composes
    /// this together with the RavenDB store instead, so a document-store site keeps its flags in its
    /// document store rather than a side-car SQLite file. The store is the only variable between the two
    /// providers — which is exactly what the driven EF==RavenDB conformance suite pins.
    /// </summary>
    public static IServiceCollection AddFeatureManagementCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Fallbacks: the host overrides these with its own tenancy/principal adapters (TryAdd, so it wins).
        services.TryAddScoped<ITenantContext, DefaultTenantContext>();
        services.TryAddScoped<IFeatureContextProvider, AnonymousFeatureContextProvider>();

        // In-process domain-event dispatch in the CURRENT scope, driving the audit trail. The module
        // used to ship its own FeatureDomainEventDispatcher here, rivalling the one
        // Stomme.Persistence's AddDomainEvents TryAdd'd; both are now consolidated onto the single
        // CurrentScopeDomainEventDispatcher, so whichever Add*() runs first the semantics are identical.
        // Current scope is what keeps the audit trail atomic with the flag change it audits, and what carries
        // the scoped ITenantContext into FeatureFlagAuditHandler without re-propagating it by hand.
        services.TryAddScoped<IDomainEventDispatcher, CurrentScopeDomainEventDispatcher>();

        // The framework's single UnitOfWork: the module used to ship its own
        // FeatureUnitOfWork here, rivalling Stomme.Persistence.UnitOfWork that AddDomainEvents TryAdd'd,
        // with the two differing on clear-vs-dispatch order — an ordering accident with no compile error to
        // announce it. both are now collapsed onto this one clear-before-dispatch type in
        // Stomme.Persistence.Abstractions (the assembly both paths reference), so whichever Add*() runs
        // first the registration is order-inert, the same shape as the dispatcher line above.
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDomainEventHandler<FeatureToggledEvent>, FeatureFlagAuditHandler>();

        // The config-backed feature source: the config layer of the precedence rule config -> DB ->
        // catalog default. A factory so a host that HAS an IConfiguration (every real host does) gets flags
        // read from its FeatureManagement section — no extra host wiring — while a minimal host or a test that
        // composes without a config root falls back to the no-op source and the historical DB behaviour.
        // TryAdd so a host can register its own IFeatureConfigurationSource (e.g. a different section name)
        // and win.
        services.TryAddSingleton<IFeatureConfigurationSource>(sp =>
        {
            // Prefer the IConfigurationRoot the host registers (Stomme's own, which loads features.json and
            // appsettings), falling back to IConfiguration for a minimal host/test. In the Stomme host the
            // WebApplicationBuilder's IConfiguration and Stomme's passed-in IConfigurationRoot are DIFFERENT
            // objects — only the latter carries features.json — so resolving the root is what makes the
            // dedicated feature file take effect.
            var config = (IConfiguration?)sp.GetService<IConfigurationRoot>() ?? sp.GetService<IConfiguration>();
            return config is null
                ? NullFeatureConfigurationSource.Instance
                : new ConfigurationFeatureConfigSource(config);
        });

        // The evaluator's singleton per-tenant flag cache: one snapshot load per tenant instead of
        // one store round trip per evaluation, shared across request scopes AND background-job scopes (the
        // scoped evaluator reads through it). Plain BCL primitives — no caching package. TryAdd so a test
        // or host can substitute its own.
        services.TryAddSingleton<IFeatureFlagCache, FeatureFlagCache>();

        services.AddScoped<IFeatureEvaluator, FeatureEvaluator>();
        services.AddScoped<IFeatureAdminService, FeatureAdminService>();
        // The standard Microsoft.FeatureManagement surface, backed by Stomme's evaluator.
        services.AddScoped<IFeatureManager, StommeFeatureManager>();

        services.AddDiscoveredFeatureContributors();
        services.TryAddSingleton<IFeatureRegistry, FeatureRegistry>();

        // The framework's assembly scan also registers IHostedService implementations; TryAddEnumerable
        // keeps the seeder to a single registration either way.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FeatureSeeder>());

        return services;
    }

    /// <summary>
    /// Registers every <see cref="IFeatureContributor"/> with a public parameterless constructor found in the
    /// loaded <c>Stomme.*</c> assemblies. This mirrors the framework's <c>IServerStartup</c> discovery: a
    /// module declares its flags by shipping a contributor type — no framework code changes. A contributor
    /// needing constructor state registers itself explicitly in its own startup instead; both land in the
    /// registry.
    /// </summary>
    private static IServiceCollection AddDiscoveredFeatureContributors(this IServiceCollection services)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.FullName is not null && a.FullName.Contains("Stomme", StringComparison.OrdinalIgnoreCase)))
        {
            IEnumerable<Type> contributorTypes;
            try
            {
                contributorTypes = assembly.GetTypes()
                    .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsGenericType
                                && typeof(IFeatureContributor).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) is not null);
            }
            catch (ReflectionTypeLoadException ex)
            {
                contributorTypes = ex.Types
                    .Where(t => t is not null && !t.IsInterface && !t.IsAbstract && !t.IsGenericType
                                && typeof(IFeatureContributor).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) is not null)!;
            }

            foreach (var type in contributorTypes)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IFeatureContributor), type));
            }
        }

        return services;
    }
}
