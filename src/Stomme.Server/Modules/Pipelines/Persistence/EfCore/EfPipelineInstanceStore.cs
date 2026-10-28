#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfPipelineInstanceStore : EfEngineStore, IPipelineInstanceStore
    {
        public EfPipelineInstanceStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async ValueTask<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async ValueTask<Page<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async ValueTask<Page<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(order).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async ValueTask<IEnumerable<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async ValueTask<IEnumerable<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter).OrderBy(order)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async ValueTask<long> CountAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).LongCountAsync(cancellationToken);
        }

        public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var items = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Paginate(pageArgs)
                .Select(PipelineInstanceSummary.FromInstanceExpression()).ToListAsync(cancellationToken);
            return Page.Of(items, count);
        }

        public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var items = await query.OrderBy(order).Paginate(pageArgs)
                .Select(PipelineInstanceSummary.FromInstanceExpression()).ToListAsync(cancellationToken);
            return Page.Of(items, count);
        }

        public async ValueTask<IEnumerable<string>> FindManyIdsAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).Select(x => x.Id).ToListAsync(cancellationToken);
        }

        public async ValueTask<Page<string>> FindManyIdsAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var ids = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Paginate(pageArgs).Select(x => x.Id).ToListAsync(cancellationToken);
            return Page.Of(ids, count);
        }

        public async ValueTask<Page<string>> FindManyIdsAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var ids = await query.OrderBy(order).Paginate(pageArgs).Select(x => x.Id).ToListAsync(cancellationToken);
            return Page.Of(ids, count);
        }

        public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).Select(PipelineInstanceSummary.FromInstanceExpression()).ToListAsync(cancellationToken);
        }

        public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync<TOrder>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrder> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).OrderBy(order).Select(PipelineInstanceSummary.FromInstanceExpression()).ToListAsync(cancellationToken);
        }

        public async ValueTask SaveAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [instance], Shadows, cancellationToken);
        }

        public async ValueTask AddAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await InsertAsync(db, [instance], Shadows, cancellationToken);
        }

        public async ValueTask UpdateAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await ReplaceAsync(db, instance, Shadows, cancellationToken);
        }

        public async ValueTask SaveManyAsync(IEnumerable<PipelineInstance> instances, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, instances.ToList(), Shadows, cancellationToken);
        }

        public async ValueTask<long> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        public async Task UpdateUpdatedTimestampAsync(string pipelineInstanceId, DateTimeOffset value, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var updated = await Read<PipelineInstance>(db)
                .Where(x => x.Id == pipelineInstanceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedAt, value), cancellationToken);
            if (updated == 0)
            {
                throw new InvalidOperationException($"Pipeline instance with ID '{pipelineInstanceId}' does not exist.");
            }
        }

        private static IQueryable<PipelineInstance> Query(EngineDbContext db, PipelineInstanceFilter filter) =>
            filter.Apply(Read<PipelineInstance>(db));

        private static IQueryable<Row<PipelineInstance>> Rows(IQueryable<PipelineInstance> query) =>
            query.Select(instance => new Row<PipelineInstance>
            {
                Entity = instance,
                A = EF.Property<string>(instance, EngineDbContext.DataColumn),
            });

        private PipelineInstance Hydrate(Row<PipelineInstance> row)
        {
            row.Entity.PipelineState = Serializer.StateFrom(row.A);
            return row.Entity;
        }

        private void Shadows(EntityEntry<PipelineInstance> entry, PipelineInstance instance) =>
            SetText(entry, EngineDbContext.DataColumn, Serializer.StateOf(instance));
    }
}
