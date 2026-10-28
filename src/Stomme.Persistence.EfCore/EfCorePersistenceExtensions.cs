using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// Registers the EF Core store: the open-generic <see cref="IRepository{TAggregate, TId}"/> over the
/// app's <typeparamref name="TContext"/>, plus the store change feed — the singleton
/// <see cref="EfCoreChangeNotifier"/> (<see cref="IChangeNotifier"/>, node-local) attached to the
/// context as a save interceptor. The host registers <c>AddDbContext&lt;TContext&gt;</c> with the
/// chosen relational provider (Npgsql for PostgreSQL, SQLite for dev/test).
/// </summary>
public static class EfCorePersistenceExtensions
{
    public static IServiceCollection AddEfCorePersistence<TContext>(this IServiceCollection services)
        where TContext : StommeDbContext
    {
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());
        services.TryAddScoped<IAggregateTracker, AggregateTracker>();
        services.TryAddScoped<IOutboxStore, EfOutboxStore>();
        services.AddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));

        // The store change feed: one singleton notifier shared by every EF store context in the host
        // (so cache invalidation and Collaboration change sync ride the native feed instead of the
        // polling fallback), attached to the DI-built options for TContext. The tenant-aware factory
        // below attaches it to its hand-built options itself.
        services.TryAddSingleton<EfCoreChangeNotifier>();
        services.TryAddSingleton<IChangeNotifier>(sp => sp.GetRequiredService<EfCoreChangeNotifier>());
        services.ConfigureDbContext<TContext>(
            (sp, options) => options.AddInterceptors(sp.GetRequiredService<EfCoreChangeNotifier>()));
        return services;
    }

    /// <summary>
    /// Registers a tenant-aware <typeparamref name="TContext"/>: each request resolves the current tenant's
    /// store binding (connection string + optional schema) via <see cref="ITenantConnectionResolver"/>, so
    /// database-per-tenant and schema-per-tenant isolation route at runtime. <paramref name="configureProvider"/>
    /// applies the relational provider to a connection string (e.g. <c>(b, cs) =&gt; b.UseNpgsql(cs)</c>).
    /// Use instead of <c>AddDbContext&lt;TContext&gt;</c>; compose with <see cref="AddEfCorePersistence{TContext}"/>.
    /// <para>
    /// <b>Registers the FACTORY only — there is deliberately no <c>AddScoped&lt;TContext&gt;</c> here (D32).</b>
    /// A tenant's store binding lives in the tenant store, so resolving it is an async read
    /// (<see cref="ITenantConnectionResolver.ResolveAsync"/>). A DI factory lambda is synchronous and cannot
    /// await, and blocking on it is what deadlocks under Blazor's renderer context (D29) — so a per-scope
    /// <typeparamref name="TContext"/> resolved straight from the container is not something this registration
    /// can honestly offer. Inject <see cref="IDbContextFactory{TContext}"/> and
    /// <c>await CreateDbContextAsync()</c>, owning the context's lifetime at the call site.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTenantAwareDbContext<TContext>(
        this IServiceCollection services, Action<DbContextOptionsBuilder, string> configureProvider)
        where TContext : StommeDbContext
    {
        ArgumentNullException.ThrowIfNull(configureProvider);
        services.AddScoped<IDbContextFactory<TContext>>(sp => new TenantAwareDbContextFactory<TContext>(
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<ITenantConnectionResolver>(),
            configureProvider,
            sp.GetService<EfCoreChangeNotifier>()));
        return services;
    }

    /// <summary>
    /// Registers EF Core document/query sessions (identity-map read &amp; read-write sessions) over a
    /// <typeparamref name="TContext"/> obtained per session from the registered
    /// <see cref="IDbContextFactory{TContext}"/>. Binds the main store; the logs store is bound by the
    /// Pipelines feature. The host must register <c>AddDbContextFactory&lt;TContext&gt;</c>.
    /// </summary>
    public static IServiceCollection AddEfDocumentSessions<TContext>(this IServiceCollection services)
        where TContext : StommeDbContext
    {
        services.TryAddSingleton<IStoreBindingResolver, StoreBindingResolver>();
        services.AddScoped<IDocumentSessionFactory>(sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TContext>>();
            var stores = new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = () => contextFactory.CreateDbContext(),
            };
            return new EfDocumentSessionFactory(stores);
        });
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL advisory-lock <see cref="IDistributedLock"/> so the EF/Npgsql path has a
    /// cross-node lock (the outbox processor uses it to keep concurrent nodes from double-draining). Call
    /// from the host when the active relational provider is PostgreSQL.
    /// </summary>
    public static IServiceCollection AddPostgresDistributedLock(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<IDistributedLock>(new PostgresAdvisoryLock(connectionString));
        return services;
    }
}
