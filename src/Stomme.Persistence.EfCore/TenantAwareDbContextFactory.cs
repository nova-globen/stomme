using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// Builds a <typeparamref name="TContext"/> for the current tenant: it resolves the tenant's store binding
/// (connection string + optional schema) via <see cref="ITenantConnectionResolver"/> and applies it, so
/// database-per-tenant routes to the tenant's database and schema-per-tenant routes to the tenant's schema.
/// System scope (and shared-schema) resolves to the default tenant's binding. The provider is applied by
/// the host-supplied callback (e.g. <c>UseNpgsql</c> / <c>UseSqlite</c>), keeping this store provider-neutral.
/// When the store change feed is registered (<paramref name="changeNotifier"/> non-null), its save
/// interceptor is attached here because these options are built by hand, outside the DI options pipeline.
/// </summary>
public sealed class TenantAwareDbContextFactory<TContext>(
    ITenantContext tenantContext,
    ITenantConnectionResolver connectionResolver,
    Action<DbContextOptionsBuilder, string> configureProvider,
    EfCoreChangeNotifier? changeNotifier = null) : IDbContextFactory<TContext>
    where TContext : StommeDbContext
{
    /// <summary>
    /// NOT SUPPORTED. Building a context here means READING THE TENANT STORE for the tenant's binding, which
    /// is an async read (<see cref="ITenantConnectionResolver.ResolveAsync"/>, D32).
    /// <see cref="IDbContextFactory{TContext}"/>'s synchronous member cannot await it and must not block on
    /// it, so this factory is async-only: use <see cref="CreateDbContextAsync"/>.
    /// </summary>
    public TContext CreateDbContext() => throw new NotSupportedException(
        $"{nameof(TenantAwareDbContextFactory<TContext>)} resolves the tenant's store binding from the tenant " +
        $"store, which is an asynchronous read. Use {nameof(CreateDbContextAsync)} instead.");

    public async Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.CurrentTenantId ?? TenantId.Default;
        var binding = await connectionResolver.ResolveAsync(tenantId);

        var builder = new DbContextOptionsBuilder<TContext>();
        configureProvider(builder, binding.ConnectionString);
        // Cache a distinct model per schema so schema-per-tenant tenants don't share one tenant's schema.
        builder.ReplaceService<IModelCacheKeyFactory, TenantSchemaModelCacheKeyFactory>();
        if (changeNotifier is not null)
        {
            builder.AddInterceptors(changeNotifier);
        }

        var context = (TContext)Activator.CreateInstance(typeof(TContext), builder.Options, tenantContext)!;
        context.Schema = binding.Schema;
        return context;
    }
}
