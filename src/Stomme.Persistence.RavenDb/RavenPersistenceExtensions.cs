using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Json.Serialization.NewtonsoftJson;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Registers the RavenDB store (the default provider): the open-generic
/// <see cref="IRepository{TAggregate, TId}"/> and the compare-exchange distributed lock.
/// </summary>
public static class RavenPersistenceExtensions
{
    /// <summary>
    /// Applies Stomme conventions and initializes the store. Stomme addresses documents by explicit
    /// id, so RavenDB's identity-property convention is disabled — an aggregate's typed/VO <c>Id</c>
    /// is stored as an ordinary field that round-trips on load.
    /// </summary>
    public static IDocumentStore InitializeStommeStore(this DocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Conventions.FindIdentityProperty = _ => false;
        // Align to the spec database-timeout default (feature 28): cap each HTTP request to RavenDB
        // at 30 s so a stalled server surfaces as a timeout rather than hanging indefinitely.
        store.Conventions.RequestTimeout = TimeSpan.FromSeconds(30);

        // On a container network or behind a reverse proxy the RavenDB client must NOT chase the
        // server's advertised PublicServerUrl — a shared RavenDB commonly advertises its LAN address, which
        // an internal-network app cannot route to (the client then fails with FailedToContactAllNodes even
        // though the configured Url is reachable). Setting RAVEN_DISABLE_TOPOLOGY_UPDATES=true pins the client
        // to the configured Urls. Default OFF, so a stock LAN/local install keeps native topology + failover.
        if (string.Equals(
                Environment.GetEnvironmentVariable("RAVEN_DISABLE_TOPOLOGY_UPDATES"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            store.Conventions.DisableTopologyUpdates = true;
        }

        // Tenancy value objects (TenantId, TenantSlug) expose only private constructors, so the
        // default reflection-based (de)serializer cannot reconstruct them; teach it to round-trip
        // each as a scalar. Both hooks are needed because RavenDB builds separate serializer and
        // deserializer instances.
        if (store.Conventions.Serialization is NewtonsoftJsonSerializationConventions newtonsoft)
        {
            newtonsoft.CustomizeJsonSerializer += AddStommeConverters;
            newtonsoft.CustomizeJsonDeserializer += AddStommeConverters;
            // Transient domain events are dispatched by the unit of work, never stored.
            newtonsoft.JsonContractResolver = new DomainEventsIgnoringContractResolver(newtonsoft);
        }

        store.Initialize();
        return store;
    }

    public static IServiceCollection AddRavenPersistence(this IServiceCollection services, IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(store);
        services.AddSingleton<IDistributedLock>(_ => new RavenDistributedLock(store));
        // Database routing: default to the shared database; a deployment registers a per-tenant resolver
        // (e.g. ConventionRavenTenantDatabaseResolver) for database-per-tenant isolation.
        services.TryAddSingleton<IRavenTenantDatabaseResolver, NullRavenTenantDatabaseResolver>();
        // Database-assigned identities for aggregates inserted with an unset id. Singleton: each HiLo
        // generator caches a server-reserved range, so a shorter lifetime burns a range per scope.
        services.TryAddSingleton<IRavenIdGenerator>(_ => new RavenHiLoIdGenerator(store));
        // The seam's read path. Bound to the store's default database and to StoreNames.Main only;
        // a host needing two stores binds two factories over two databases explicitly.
        services.TryAddSingleton<IDocumentSessionFactory>(provider =>
            new RavenDocumentSessionFactory(store, provider.GetRequiredService<IRavenIdGenerator>()));
        // Per-scope change-vector cache enabling optimistic concurrency across the per-operation sessions.
        services.TryAddScoped<RavenChangeVectorTracker>();
        services.TryAddScoped<IAggregateTracker, AggregateTracker>();
        services.TryAddSingleton<IOutboxStore, RavenOutboxStore>();
        // Changes-API change feed for cache invalidation / collaboration edit sync.
        services.TryAddSingleton<IChangeNotifier>(_ => new RavenChangeNotifier(store));
        // Data-patch migration runner (invoked by the setup/upgrade flow); resolves any registered
        // IRavenDataMigration implementations.
        services.TryAddSingleton<Migrations.RavenDataMigrationRunner>();
        // NOTE: the durable job-run/job-schedule stores are omitted here; the
        // BackgroundJobs seam is not part of this extension. They are
        // re-added when that seam lands.
        services.AddScoped(typeof(IRepository<,>), typeof(RavenRepository<,>));
        return services;
    }

    private static void AddStommeConverters(JsonSerializer serializer)
    {
        serializer.Converters.Add(new TenantIdJsonConverter());
        // Convention-based: round-trip any module's value objects that implement the SharedKernel
        // serialization markers, without this store referencing the module that defines them.
        serializer.Converters.Add(new StringValueObjectConverter());
        serializer.Converters.Add(new MapValueObjectConverter());
    }
}
