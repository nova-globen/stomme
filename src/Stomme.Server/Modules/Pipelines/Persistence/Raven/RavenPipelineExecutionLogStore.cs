#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
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
    internal sealed class RavenPipelineExecutionLogStore : RavenEngineStore, IPipelineExecutionLogStore
    {
        public RavenPipelineExecutionLogStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async Task AddAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [ExecutionLogDocument.From(record, Serializer)], insertOnly: true, cancellationToken);
        }

        public async Task AddManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(r => ExecutionLogDocument.From(r, Serializer)), insertOnly: true, cancellationToken);
        }

        public async Task SaveAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [ExecutionLogDocument.From(record, Serializer)], insertOnly: false, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(r => ExecutionLogDocument.From(r, Serializer)), insertOnly: false, cancellationToken);
        }

        public async Task<PipelineExecutionLogRecord?> FindAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<PipelineExecutionLogRecord?> FindAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var document = await query.FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<Page<PipelineExecutionLogRecord>> FindManyAsync(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, DefaultOrder, cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async Task<Page<PipelineExecutionLogRecord>> FindManyAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async Task<long> DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        private static void DefaultOrder(IAsyncDocumentQuery<ExecutionLogDocument> query)
        {
            OrderBy(query, nameof(ExecutionLogDocument.Timestamp), typeof(DateTime));
            OrderBy(query, nameof(ExecutionLogDocument.Sequence), typeof(long));
        }

        // PipelineExecutionLogRecordFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<ExecutionLogDocument> Filtered(IAsyncDocumentSession session, PipelineExecutionLogRecordFilter filter)
        {
            var query = Query<ExecutionLogDocument>(session);
            Equal(query, nameof(ExecutionLogDocument.Id), filter.Id);
            In(query, nameof(ExecutionLogDocument.Id), filter.Ids);
            Equal(query, nameof(ExecutionLogDocument.PipelineInstanceId), filter.PipelineInstanceId);
            In(query, nameof(ExecutionLogDocument.PipelineInstanceId), filter.PipelineInstanceIds);
            Equal(query, nameof(ExecutionLogDocument.ParentStepInstanceId), filter.ParentStepInstanceId);
            Equal(query, nameof(ExecutionLogDocument.StepId), filter.StepId);
            In(query, nameof(ExecutionLogDocument.StepId), filter.StepIds);
            Equal(query, nameof(ExecutionLogDocument.StepNodeId), filter.StepNodeId);
            In(query, nameof(ExecutionLogDocument.StepNodeId), filter.StepNodeIds);
            Equal(query, nameof(ExecutionLogDocument.EventName), filter.EventName);
            In(query, nameof(ExecutionLogDocument.EventName), filter.EventNames);
            NotEqual(query, nameof(ExecutionLogDocument.StepType), filter.ExcludeStepType);
            NotIn(query, nameof(ExecutionLogDocument.StepType), filter.ExcludeStepTypes);
            return query;
        }
    }
}
