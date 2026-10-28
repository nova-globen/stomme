#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfTriggerStore : EfEngineStore, ITriggerStore
    {
        public EfTriggerStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async ValueTask SaveAsync(StoredTrigger record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async ValueTask SaveManyAsync(IEnumerable<StoredTrigger> records, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async ValueTask<StoredTrigger?> FindAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async ValueTask<IEnumerable<StoredTrigger>> FindManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public ValueTask<Page<StoredTrigger>> FindManyAsync(TriggerFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default) =>
            FindManyAsync(filter, pageArgs, new StoredTriggerOrder<string>(x => x.Id, OrderDirection.Ascending), cancellationToken);

        public async ValueTask<Page<StoredTrigger>> FindManyAsync<TProp>(TriggerFilter filter, PageArgs pageArgs, StoredTriggerOrder<TProp> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(order).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async ValueTask ReplaceAsync(IEnumerable<StoredTrigger> removed, IEnumerable<StoredTrigger> added, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var removedIds = removed.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList();
            if (removedIds.Count > 0)
            {
                await Read<StoredTrigger>(db, tenantAgnostic: true).Where(x => removedIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
            }

            await UpsertAsync(db, added.ToList(), Shadows, cancellationToken);
        }

        public async ValueTask<long> DeleteManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        private static IQueryable<StoredTrigger> Query(EngineDbContext db, TriggerFilter filter) =>
            filter.Apply(Read<StoredTrigger>(db, filter.TenantAgnostic));

        private static IQueryable<Row<StoredTrigger>> Rows(IQueryable<StoredTrigger> query) =>
            query.Select(trigger => new Row<StoredTrigger>
            {
                Entity = trigger,
                A = EF.Property<string>(trigger, EngineDbContext.DataColumn),
            });

        private StoredTrigger Hydrate(Row<StoredTrigger> row)
        {
            row.Entity.Payload = Serializer.PayloadFrom(row.A);
            return row.Entity;
        }

        private void Shadows(EntityEntry<StoredTrigger> entry, StoredTrigger trigger) =>
            SetText(entry, EngineDbContext.DataColumn, Serializer.PayloadOf(trigger.Payload));
    }
}
