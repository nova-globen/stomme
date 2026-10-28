using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// Caches a distinct EF Core model per schema for schema-per-tenant isolation. Without this, EF caches one
/// model per context type and every tenant would share the first tenant's schema. Including
/// <see cref="StommeDbContext.Schema"/> in the cache key gives each schema its own model.
/// </summary>
public sealed class TenantSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is StommeDbContext { Schema: { } schema }
            ? (context.GetType(), schema, designTime)
            : (context.GetType(), designTime);
}
