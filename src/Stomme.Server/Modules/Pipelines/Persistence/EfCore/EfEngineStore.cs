#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    /// <summary>
    /// What every relational engine store shares: a context per operation, non-tracking reads scoped to the
    /// engine tenant (or not, when a filter is tenant-agnostic), and the upsert the engine's <c>SaveAsync</c>
    /// contracts mean - an existing row by id is updated, a new one is added, in one round trip per batch.
    /// </summary>
    internal abstract class EfEngineStore
    {
        private readonly IDbContextFactory<EngineDbContext> _contexts;

        protected EfEngineStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
        {
            _contexts = contexts;
            Serializer = serializer;
        }

        protected EngineEntitySerializer Serializer { get; }

        protected Task<EngineDbContext> OpenAsync(CancellationToken cancellationToken) => _contexts.CreateDbContextAsync(cancellationToken);

        protected static IQueryable<T> Read<T>(EngineDbContext db, bool tenantAgnostic = false) where T : class
        {
            var set = db.Set<T>().AsNoTracking();
            return tenantAgnostic ? set.IgnoreQueryFilters() : set;
        }

        // The engine stamps nothing itself: a record created inside a tenant scope carries that tenant, one created
        // outside any scope carries the default tenant (the empty string), and a record that already names its
        // tenant keeps it.
        protected static void Stamp(Entity entity, EngineDbContext db) => entity.TenantId ??= db.TenantId;

        protected static async Task UpsertAsync<T>(
            EngineDbContext db, IReadOnlyCollection<T> entities, Action<EntityEntry<T>, T> shadows, CancellationToken cancellationToken)
            where T : Entity
        {
            if (entities.Count == 0)
            {
                return;
            }

            var ids = entities.Select(entity => entity.Id).Distinct(StringComparer.Ordinal).ToList();
            var existing = (await db.Set<T>().IgnoreQueryFilters()
                    .Where(entity => ids.Contains(entity.Id))
                    .Select(entity => entity.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entity in entities)
            {
                Stamp(entity, db);
                var entry = existing.Contains(entity.Id) ? db.Update(entity) : db.Add(entity);
                shadows(entry, entity);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        protected static async Task InsertAsync<T>(
            EngineDbContext db, IReadOnlyCollection<T> entities, Action<EntityEntry<T>, T> shadows, CancellationToken cancellationToken)
            where T : Entity
        {
            if (entities.Count == 0)
            {
                return;
            }

            foreach (var entity in entities)
            {
                Stamp(entity, db);
                shadows(db.Add(entity), entity);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        protected static async Task ReplaceAsync<T>(
            EngineDbContext db, T entity, Action<EntityEntry<T>, T> shadows, CancellationToken cancellationToken)
            where T : Entity
        {
            Stamp(entity, db);
            shadows(db.Update(entity), entity);
            await db.SaveChangesAsync(cancellationToken);
        }

        protected static void SetText(EntityEntry entry, string column, string? value) => entry.Property(column).CurrentValue = value;

        /// <summary>An entity with the text of its shadow columns, read in one projection so no tracking is needed.</summary>
        protected sealed class Row<T>
        {
            public T Entity { get; set; } = default!;
            public string? A { get; set; }
            public string? B { get; set; }
            public string? C { get; set; }
            public string? D { get; set; }
            public string? E { get; set; }
            public string? F { get; set; }
        }
    }
}
