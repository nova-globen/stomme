using System;
using System.Collections.Concurrent;
using System.Linq;
using Stomme.Databases.Interfaces;
using Stomme.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Resolve-time selection between the framework store's two providers (D54). Both
    /// <see cref="FrameworkStoreServiceExtensions.AddStommeFrameworkStore"/> (EF Core) and
    /// <see cref="RavenFrameworkStoreServiceExtensions.AddRavenFrameworkStore(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Func{System.IServiceProvider, Raven.Client.Documents.IDocumentStore}, System.Action{RavenFrameworkStoreOptions})"/>
    /// register EVERY closed store contract (D42), so after both run each interface has two registrations. Plain
    /// DI is last-wins, which would silently pin the whole framework store to whichever extension ran last. This
    /// replaces each such pair with ONE factory that picks the provider at first resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why resolve-time, not composition-time.</b> On a fresh install the config is empty and
    /// <c>GetDatabaseType()</c> returns the relational default (SqlServer), so a branch taken when the container
    /// is composed could not see the provider the one in-process install pass must write to. But config is hot
    /// -reloaded mid-install — <c>MigrateMasterAsync</c> persists <c>DefaultDBType</c> and reloads BEFORE
    /// <c>CreateTenantAsync</c>/<c>CreateSiteAsync</c> resolve any store — so a factory that reads the type fresh
    /// on each resolve already sees the Raven type by the time the seam is touched. This mirrors how the EF
    /// DbContexts adapt per-<c>OnConfiguring</c> without a container rebuild.
    /// </para>
    /// <para>
    /// <b>Why this keeps the D42 gate green.</b> The gate exercises each extension on its own collection and
    /// asserts every contract is registered; it never runs this selector. Both extensions still register every
    /// twin, unchanged.
    /// </para>
    /// </remarks>
    public static class FrameworkStoreProviderSelection
    {
        // The framework store's EF and RavenDB store types (and their two session factories) all live in this
        // one namespace; the discriminator below is scoped to it so an unrelated interface that happens to have
        // a "Raven*"-named implementation elsewhere in the host can never be captured.
        private const string StoresNamespace = "Stomme.Repository.Stores";

        // Cached because the store factories resolve on every operation and Type.GetType +
        // Activator.CreateInstance per resolve would be waste. Keyed on the provider type string, so the one
        // mid-install flip from the relational default to the Raven type recomputes rather than staling.
        private static readonly ConcurrentDictionary<string, bool> _relationalByType = new(StringComparer.Ordinal);

        /// <summary>
        /// Collapses every EF/Raven store pair into a single resolve-time-selecting registration. Call AFTER
        /// both <c>AddStommeFrameworkStore</c> and <c>AddRavenFrameworkStore</c>.
        /// </summary>
        public static IServiceCollection SelectFrameworkStoreProviderByConfig(this IServiceCollection services) =>
            services.SelectStoreProviderByConfig(StoresNamespace);

        /// <summary>
        /// The reusable, MODULE-facing form of <see cref="SelectFrameworkStoreProviderByConfig"/> (D55, orchestrator
        /// decision 2). Collapses every EF/Raven store pair whose implementation lives in
        /// <paramref name="implementationNamespace"/> into one resolve-time-selecting registration, keyed on
        /// <c>IConfigManager.GetDatabaseType()</c> exactly as the framework store is. A module registers both twins
        /// of its own closed store contracts (in ONE implementation namespace) and calls this from its
        /// <c>IServerStartup.ConfigureServices</c> — the framework's own <c>SelectFrameworkStoreProviderByConfig</c>
        /// only scans <c>Stomme.Repository.Stores</c> and, being called in the composition root, runs BEFORE any
        /// module startup, so a module cannot rely on it.
        /// </summary>
        public static IServiceCollection SelectStoreProviderByConfig(this IServiceCollection services, string implementationNamespace)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrEmpty(implementationNamespace);

            var pairs = services
                .Where(descriptor => !descriptor.IsKeyedService
                    && descriptor.ImplementationType is not null
                    && descriptor.ImplementationType.Namespace == implementationNamespace)
                .GroupBy(descriptor => descriptor.ServiceType)
                .Where(group => group.Count() == 2
                    && group.Any(descriptor => IsRavenImplementation(descriptor.ImplementationType))
                    && group.Any(descriptor => !IsRavenImplementation(descriptor.ImplementationType)))
                .ToList();

            foreach (var pair in pairs)
            {
                var raven = pair.First(descriptor => IsRavenImplementation(descriptor.ImplementationType));
                var ef = pair.First(descriptor => !IsRavenImplementation(descriptor.ImplementationType));

                var contract = pair.Key;
                var efImplementation = ef.ImplementationType!;
                var ravenImplementation = raven.ImplementationType!;
                // Both providers register their stores with the same (transient) lifetime; take it from the EF
                // side so a future change to one is not silently overridden here.
                var lifetime = ef.Lifetime;

                services.Remove(ef);
                services.Remove(raven);

                services.Add(new ServiceDescriptor(
                    contract,
                    provider => IsRelationalProvider(provider)
                        ? ActivatorUtilities.CreateInstance(provider, efImplementation)
                        : ActivatorUtilities.CreateInstance(provider, ravenImplementation),
                    lifetime));
            }

            return services;
        }

        private static bool IsRavenImplementation(Type implementationType) =>
            implementationType.Name.StartsWith("Raven", StringComparison.Ordinal);

        /// <summary>
        /// Whether the configured database provider is relational (EF Core) rather than a document store. Shared
        /// with the identity-store swap (D54b), which selects EF's <c>UserStore</c> vs the hand-written
        /// <c>RavenIdentityUserStore</c> by the SAME resolve-time key, so a relational and a document install make
        /// the framework store and the identity store agree on provider.
        /// <para>
        /// PUBLIC since D55 (orchestrator decision 2): a module's own resolve-time selector needs the same key, and
        /// external modules live in another assembly. This is the <see cref="IServiceProvider"/> form, read at
        /// resolve time from live config; <see cref="IsRelationalProvider(string)"/> is the string form for code
        /// that already holds a <c>Tenant.DBType</c> (e.g. a module's <c>InstallAsync</c>).
        /// </para>
        /// </summary>
        public static bool IsRelationalProviderConfigured(IServiceProvider provider) => IsRelationalProvider(provider);

        /// <summary>
        /// Whether <paramref name="databaseType"/> (an assembly-qualified <c>IDatabase</c> type string, as carried by
        /// <c>Tenant.DBType</c> / <c>AvailableDatabases</c>) is a relational (EF Core) provider rather than a document
        /// store. The same test <c>DatabaseManager.IsRelationalProvider</c> uses to branch the install pipeline —
        /// exposed publicly (D55, orchestrator decision 2) so a module's <c>InstallAsync</c> can run its EF migrator
        /// ONLY on a relational tenant. An empty/unresolvable type is treated as NON-relational here: a module install
        /// with no known provider has no relational schema to migrate.
        /// </summary>
        public static bool IsRelationalProvider(string databaseType)
        {
            // The empty case is NOT cached: it means "no provider named", and the two callers disagree on what
            // that implies (the resolve-time selector below reads it as the SqlServer fresh-boot default; a module
            // install with an empty Tenant.DBType has no relational schema to migrate). The shared cache below only
            // ever holds NON-empty keys, where both agree — is IRelationalDatabase or not.
            if (string.IsNullOrEmpty(databaseType))
            {
                return false;
            }

            return IsRelationalNonEmpty(databaseType);
        }

        // The same predicate DatabaseManager.IsRelationalProvider uses to branch the install pipeline: construct
        // the provider reflectively and test for the relational surface. An empty/unknown type is treated as
        // relational, matching GetDatabaseType()'s SqlServer default — the fresh-boot case, which must resolve
        // the EF stores.
        private static bool IsRelationalProvider(IServiceProvider provider)
        {
            var databaseType = provider.GetRequiredService<IConfigManager>().GetDatabaseType();
            if (string.IsNullOrEmpty(databaseType))
            {
                return true;
            }

            return IsRelationalNonEmpty(databaseType);
        }

        // The shared, cached resolution for a NON-empty provider type string. Keyed on the string so the one
        // mid-install flip from the relational default to the Raven type recomputes rather than staling.
        private static bool IsRelationalNonEmpty(string databaseType) =>
            _relationalByType.GetOrAdd(databaseType, static type =>
            {
                var resolved = Type.GetType(type);
                return resolved is not null && Activator.CreateInstance(resolved) is IRelationalDatabase;
            });
    }
}
