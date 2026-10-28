#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    internal sealed class EfPipelineExecutionLogStore : EfEngineStore, IPipelineExecutionLogStore
    {
        public EfPipelineExecutionLogStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async Task AddAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await InsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async Task AddManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await InsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async Task SaveAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async Task<PipelineExecutionLogRecord?> FindAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<PipelineExecutionLogRecord?> FindAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter).OrderBy(order)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<Page<PipelineExecutionLogRecord>> FindManyAsync(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(x => x.Timestamp).ThenBy(x => x.Sequence).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async Task<Page<PipelineExecutionLogRecord>> FindManyAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(order).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async Task<long> DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        private static IQueryable<PipelineExecutionLogRecord> Query(EngineDbContext db, PipelineExecutionLogRecordFilter filter) =>
            filter.Apply(Read<PipelineExecutionLogRecord>(db));

        private static IQueryable<Row<PipelineExecutionLogRecord>> Rows(IQueryable<PipelineExecutionLogRecord> query) =>
            query.Select(record => new Row<PipelineExecutionLogRecord>
            {
                Entity = record,
                A = EF.Property<string>(record, EngineDbContext.StepStateColumn),
                B = EF.Property<string>(record, EngineDbContext.PayloadColumn),
            });

        private PipelineExecutionLogRecord Hydrate(Row<PipelineExecutionLogRecord> row)
        {
#pragma warning disable CS0618 // the obsolete StepState is still persisted for readers that have not moved
            row.Entity.StepState = Serializer.PayloadFrom<Dictionary<string, object>>(row.A);
#pragma warning restore CS0618
            row.Entity.Payload = Serializer.PayloadFrom(row.B);
            return row.Entity;
        }

        private void Shadows(EntityEntry<PipelineExecutionLogRecord> entry, PipelineExecutionLogRecord record)
        {
#pragma warning disable CS0618
            SetText(entry, EngineDbContext.StepStateColumn, Serializer.PayloadOf(record.StepState));
#pragma warning restore CS0618
            SetText(entry, EngineDbContext.PayloadColumn, Serializer.PayloadOf(record.Payload));
        }
    }
}
