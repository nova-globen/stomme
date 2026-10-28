#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Engine.Pipelines.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfStepExecutionStore : EfEngineStore, IStepExecutionStore
    {
        public EfStepExecutionStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async Task AddManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await InsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async Task<StepExecutionRecord?> FindAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<IEnumerable<StepExecutionRecord>> FindManyAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter).OrderBy(order)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async Task<IEnumerable<StepExecutionRecord>> FindManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        // A summary carries the metadata and nothing else that is text, so only that column is read.
        public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await SummaryRows(Query(db, filter).OrderBy(order)).ToListAsync(cancellationToken);
            return rows.Select(Summarize).ToList();
        }

        public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await SummaryRows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Summarize).ToList();
        }

        public async Task<long> CountAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).LongCountAsync(cancellationToken);
        }

        public async Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        private static IQueryable<StepExecutionRecord> Query(EngineDbContext db, StepExecutionRecordFilter filter) =>
            filter.Apply(Read<StepExecutionRecord>(db));

        private static IQueryable<Row<StepExecutionRecord>> Rows(IQueryable<StepExecutionRecord> query) =>
            query.Select(record => new Row<StepExecutionRecord>
            {
                Entity = record,
                A = EF.Property<string>(record, EngineDbContext.StepStateColumn),
                B = EF.Property<string>(record, EngineDbContext.PayloadColumn),
                C = EF.Property<string>(record, EngineDbContext.OutputsColumn),
                D = EF.Property<string>(record, EngineDbContext.PropertiesColumn),
                E = EF.Property<string>(record, EngineDbContext.MetadataColumn),
                F = EF.Property<string>(record, EngineDbContext.ExceptionColumn),
            });

        private static IQueryable<Row<StepExecutionRecord>> SummaryRows(IQueryable<StepExecutionRecord> query) =>
            query.Select(record => new Row<StepExecutionRecord>
            {
                Entity = record,
                E = EF.Property<string>(record, EngineDbContext.MetadataColumn),
            });

        private StepExecutionRecord Hydrate(Row<StepExecutionRecord> row)
        {
            var record = row.Entity;
            record.StepState = Serializer.SafeFrom<Dictionary<string, object?>>(row.A);
            record.Payload = Serializer.PayloadFrom<Dictionary<string, object>>(row.B);
            record.Outputs = Serializer.SafeFrom<Dictionary<string, object?>>(row.C);
            record.Properties = Serializer.PayloadFrom<Dictionary<string, object>>(row.D);
            record.Metadata = Serializer.PayloadFrom<Dictionary<string, object>>(row.E);
            record.Exception = Serializer.PayloadFrom<ExceptionState>(row.F);
            return record;
        }

        private StepExecutionRecordSummary Summarize(Row<StepExecutionRecord> row)
        {
            row.Entity.Metadata = Serializer.PayloadFrom<Dictionary<string, object>>(row.E);
            return StepExecutionRecordSummary.FromRecord(row.Entity);
        }

        private void Shadows(EntityEntry<StepExecutionRecord> entry, StepExecutionRecord record)
        {
            SetText(entry, EngineDbContext.StepStateColumn, Serializer.SafeOf(record.StepState));
            SetText(entry, EngineDbContext.PayloadColumn, Serializer.PayloadOf(record.Payload));
            SetText(entry, EngineDbContext.OutputsColumn, Serializer.SafeOf(record.Outputs));
            SetText(entry, EngineDbContext.PropertiesColumn, Serializer.PayloadOf(record.Properties));
            SetText(entry, EngineDbContext.MetadataColumn, Serializer.PayloadOf(record.Metadata));
            SetText(entry, EngineDbContext.ExceptionColumn, Serializer.PayloadOf(record.Exception));
        }
    }
}
