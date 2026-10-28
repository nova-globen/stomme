#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    internal sealed class RavenStepExecutionStore : RavenEngineStore, IStepExecutionStore
    {
        public RavenStepExecutionStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [StepExecutionDocument.From(record, Serializer)], insertOnly: false, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(r => StepExecutionDocument.From(r, Serializer)), insertOnly: false, cancellationToken);
        }

        public async Task AddManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(r => StepExecutionDocument.From(r, Serializer)), insertOnly: true, cancellationToken);
        }

        public async Task<StepExecutionRecord?> FindAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<IEnumerable<StepExecutionRecord>> FindManyAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var documents = await query.ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async Task<IEnumerable<StepExecutionRecord>> FindManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default) =>
            (await FindManyAsync(filter, order, cancellationToken)).Select(StepExecutionRecordSummary.FromRecord).ToList();

        public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default) =>
            (await FindManyAsync(filter, cancellationToken)).Select(StepExecutionRecordSummary.FromRecord).ToList();

        public async Task<long> CountAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await Filtered(session, filter).LongCountAsync(cancellationToken);
        }

        public async Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        // StepExecutionRecordFilter.Apply, clause for clause (an EMPTY id list is ignored there, so here too).
        private IAsyncDocumentQuery<StepExecutionDocument> Filtered(IAsyncDocumentSession session, StepExecutionRecordFilter filter)
        {
            var query = Query<StepExecutionDocument>(session);
            Equal(query, nameof(StepExecutionDocument.Id), filter.Id);
            In(query, nameof(StepExecutionDocument.Id), filter.Ids);
            Equal(query, nameof(StepExecutionDocument.PipelineInstanceId), filter.PipelineInstanceId);
            In(query, nameof(StepExecutionDocument.PipelineInstanceId), filter.PipelineInstanceIds);
            Equal(query, nameof(StepExecutionDocument.StepId), filter.StepId);
            In(query, nameof(StepExecutionDocument.StepId), NonEmpty(filter.StepIds));
            Equal(query, nameof(StepExecutionDocument.StepNodeId), filter.StepNodeId);
            In(query, nameof(StepExecutionDocument.StepNodeId), NonEmpty(filter.StepNodeIds));
            Equal(query, nameof(StepExecutionDocument.StepName), filter.Name);
            In(query, nameof(StepExecutionDocument.StepName), NonEmpty(filter.Names));
            Equal(query, nameof(StepExecutionDocument.Status), filter.Status);
            In(query, nameof(StepExecutionDocument.Status), NonEmpty(filter.Statuses));
            Equal(query, nameof(StepExecutionDocument.IsCompleted), filter.Completed);
            Equal(query, nameof(StepExecutionDocument.SchedulingStepExecutionId), filter.SchedulingStepExecutionId);
            In(query, nameof(StepExecutionDocument.SchedulingStepExecutionId), NonEmpty(filter.SchedulingStepExecutionIds));
            Equal(query, nameof(StepExecutionDocument.SchedulingStepId), filter.SchedulingStepId);
            In(query, nameof(StepExecutionDocument.SchedulingStepId), NonEmpty(filter.SchedulingStepIds));
            Equal(query, nameof(StepExecutionDocument.SchedulingPipelineInstanceId), filter.SchedulingPipelineInstanceId);
            In(query, nameof(StepExecutionDocument.SchedulingPipelineInstanceId), NonEmpty(filter.SchedulingPipelineInstanceIds));
            Equal(query, nameof(StepExecutionDocument.CallStackDepth), filter.CallStackDepth);
            return query;
        }

        private static ICollection<T>? NonEmpty<T>(ICollection<T>? values) => values is { Count: > 0 } ? values : null;
    }
}
