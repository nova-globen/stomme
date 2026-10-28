#nullable enable
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
    internal sealed class EfPipelineDefinitionStore : EfEngineStore, IPipelineDefinitionStore
    {
        public EfPipelineDefinitionStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async Task<PipelineDefinition?> FindAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<PipelineDefinition?> FindAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter).OrderBy(order)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<Page<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async Task<Page<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(order).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async Task<IEnumerable<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async Task<IEnumerable<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter).OrderBy(order)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var items = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Paginate(pageArgs)
                .Select(PipelineDefinitionSummary.FromDefinitionExpression()).ToListAsync(cancellationToken);
            return Page.Of(items, count);
        }

        public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var items = await query.OrderBy(order).Paginate(pageArgs)
                .Select(PipelineDefinitionSummary.FromDefinitionExpression()).ToListAsync(cancellationToken);
            return Page.Of(items, count);
        }

        public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).Select(PipelineDefinitionSummary.FromDefinitionExpression()).ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).OrderBy(order).Select(PipelineDefinitionSummary.FromDefinitionExpression()).ToListAsync(cancellationToken);
        }

        public async Task<PipelineDefinition?> FindLastVersionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter).OrderByDescending(x => x.Version)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task SaveAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [definition], Shadows, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, definitions.ToList(), Shadows, cancellationToken);
        }

        // The engine's contract counts DEFINITIONS, not rows: every version of a matched definition goes, and the
        // count is the number of distinct definitions removed.
        public async Task<long> DeleteAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var definitionIds = await Query(db, filter).Select(x => x.DefinitionId).Distinct().ToListAsync(cancellationToken);
            if (definitionIds.Count == 0)
            {
                return 0;
            }

            await Read<PipelineDefinition>(db, filter.TenantAgnostic)
                .Where(x => definitionIds.Contains(x.DefinitionId))
                .ExecuteDeleteAsync(cancellationToken);
            return definitionIds.Count;
        }

        public async Task<bool> AnyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).AnyAsync(cancellationToken);
        }

        public async Task<long> CountDistinctAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Read<PipelineDefinition>(db).Select(x => x.DefinitionId).Distinct().LongCountAsync(cancellationToken);
        }

        public async Task<bool> GetIsNameUnique(string name, string? definitionId = null, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var taken = await Read<PipelineDefinition>(db).AnyAsync(x => x.Name == name && x.DefinitionId != definitionId, cancellationToken);
            return !taken;
        }

        private static IQueryable<PipelineDefinition> Query(EngineDbContext db, PipelineDefinitionFilter filter) =>
            filter.Apply(Read<PipelineDefinition>(db, filter.TenantAgnostic));

        private static IQueryable<Row<PipelineDefinition>> Rows(IQueryable<PipelineDefinition> query) =>
            query.Select(definition => new Row<PipelineDefinition>
            {
                Entity = definition,
                A = EF.Property<string>(definition, EngineDbContext.DataColumn),
            });

        private PipelineDefinition Hydrate(Row<PipelineDefinition> row)
        {
            Serializer.ApplyDefinitionData(row.Entity, row.A);
            return row.Entity;
        }

        private void Shadows(EntityEntry<PipelineDefinition> entry, PipelineDefinition definition) =>
            SetText(entry, EngineDbContext.DataColumn, Serializer.DefinitionDataOf(definition, includeOptions: false));
    }
}
