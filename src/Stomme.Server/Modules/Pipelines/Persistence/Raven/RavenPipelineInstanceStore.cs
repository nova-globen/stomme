#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Enums;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    internal sealed class RavenPipelineInstanceStore : RavenEngineStore, IPipelineInstanceStore
    {
        public RavenPipelineInstanceStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async ValueTask<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async ValueTask<Page<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, DefaultOrder, cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async ValueTask<Page<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async ValueTask<IEnumerable<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async ValueTask<IEnumerable<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var documents = await query.ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async ValueTask<long> CountAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await Filtered(session, filter).LongCountAsync(cancellationToken);
        }

        public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, DefaultOrder, cancellationToken);
            return Page.Of(page.Items.Select(Summarize).ToList(), page.TotalCount);
        }

        public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(Summarize).ToList(), page.TotalCount);
        }

        public async ValueTask<IEnumerable<string>> FindManyIdsAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var rows = await Filtered(session, filter).SelectFields<IdOnly>().ToListAsync(cancellationToken);
            return rows.Select(r => r.Id).ToList();
        }

        public async ValueTask<Page<string>> FindManyIdsAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, DefaultOrder, cancellationToken);
            return Page.Of(page.Items.Select(d => d.Id).ToList(), page.TotalCount);
        }

        public async ValueTask<Page<string>> FindManyIdsAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(d => d.Id).ToList(), page.TotalCount);
        }

        public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(Summarize).ToList();
        }

        public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync<TOrder>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrder> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var documents = await query.ToListAsync(cancellationToken);
            return documents.Select(Summarize).ToList();
        }

        public async ValueTask SaveAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
        {
            Stamp(instance);
            using var session = OpenSession();
            await StoreAsync(session, [PipelineInstanceDocument.From(instance, Serializer)], insertOnly: false, cancellationToken);
        }

        public async ValueTask AddAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
        {
            Stamp(instance);
            using var session = OpenSession();
            await StoreAsync(session, [PipelineInstanceDocument.From(instance, Serializer)], insertOnly: true, cancellationToken);
        }

        public ValueTask UpdateAsync(PipelineInstance instance, CancellationToken cancellationToken = default) => SaveAsync(instance, cancellationToken);

        public async ValueTask SaveManyAsync(IEnumerable<PipelineInstance> instances, CancellationToken cancellationToken = default)
        {
            var list = instances.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(i => PipelineInstanceDocument.From(i, Serializer)), insertOnly: false, cancellationToken);
        }

        public async ValueTask<long> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        public async Task UpdateUpdatedTimestampAsync(string pipelineInstanceId, DateTimeOffset value, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await session.LoadAsync<PipelineInstanceDocument>(DocumentId<PipelineInstanceDocument>(pipelineInstanceId), cancellationToken)
                ?? throw new InvalidOperationException($"Pipeline instance with ID '{pipelineInstanceId}' does not exist.");
            document.UpdatedAt = value.UtcDateTime;
            await session.SaveChangesAsync(cancellationToken);
        }

        private PipelineInstanceSummary Summarize(PipelineInstanceDocument document) =>
            PipelineInstanceSummary.FromInstance(document.ToEntity(Serializer, withState: false));

        private static void DefaultOrder(IAsyncDocumentQuery<PipelineInstanceDocument> query)
        {
            OrderBy(query, nameof(PipelineInstanceDocument.CreatedAt), typeof(DateTime));
            OrderBy(query, nameof(PipelineInstanceDocument.Id), typeof(string));
        }

        // PipelineInstanceFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<PipelineInstanceDocument> Filtered(IAsyncDocumentSession session, PipelineInstanceFilter filter)
        {
            var query = Query<PipelineInstanceDocument>(session);
            if (!string.IsNullOrWhiteSpace(filter.Id)) Equal(query, nameof(PipelineInstanceDocument.Id), filter.Id);
            In(query, nameof(PipelineInstanceDocument.Id), filter.Ids);
            if (!string.IsNullOrWhiteSpace(filter.DefinitionId)) Equal(query, nameof(PipelineInstanceDocument.DefinitionId), filter.DefinitionId);
            if (!string.IsNullOrWhiteSpace(filter.DefinitionVersionId)) Equal(query, nameof(PipelineInstanceDocument.DefinitionVersionId), filter.DefinitionVersionId);
            In(query, nameof(PipelineInstanceDocument.DefinitionId), filter.DefinitionIds);
            In(query, nameof(PipelineInstanceDocument.DefinitionVersionId), filter.DefinitionVersionIds);
            Equal(query, nameof(PipelineInstanceDocument.Version), filter.Version);
            In(query, nameof(PipelineInstanceDocument.ParentPipelineInstanceId), filter.ParentPipelineInstanceIds);
            if (!string.IsNullOrWhiteSpace(filter.CorrelationId)) Equal(query, nameof(PipelineInstanceDocument.CorrelationId), filter.CorrelationId);
            In(query, nameof(PipelineInstanceDocument.CorrelationId), filter.CorrelationIds);
            In(query, nameof(PipelineInstanceDocument.Name), filter.Names);
            Equal(query, nameof(PipelineInstanceDocument.Status), filter.PipelineStatus);
            Equal(query, nameof(PipelineInstanceDocument.SubStatus), filter.PipelineSubStatus);
            In(query, nameof(PipelineInstanceDocument.Status), filter.PipelineStatuses);
            In(query, nameof(PipelineInstanceDocument.SubStatus), filter.PipelineSubStatuses);
            Equal(query, nameof(PipelineInstanceDocument.IsExecuting), filter.IsExecuting);
            if (filter.HasIncidents is { } hasIncidents)
            {
                if (hasIncidents)
                {
                    query.WhereGreaterThan(nameof(PipelineInstanceDocument.IncidentCount), 0);
                }
                else
                {
                    query.WhereEquals(nameof(PipelineInstanceDocument.IncidentCount), 0);
                }
            }

            Equal(query, nameof(PipelineInstanceDocument.IsSystem), filter.IsSystem);
            if (filter.Name is not null)
            {
                Contains(query, nameof(PipelineInstanceDocument.Name), filter.Name);
            }

            Before(query, nameof(PipelineInstanceDocument.UpdatedAt), filter.BeforeLastUpdated);
            if (filter.TimestampFilters is not null)
            {
                foreach (var timestampFilter in filter.TimestampFilters)
                {
                    ApplyTimestamp(query, timestampFilter);
                }
            }

            if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
            {
                ContainsAny(query, filter.SearchTerm,
                    nameof(PipelineInstanceDocument.Name), nameof(PipelineInstanceDocument.DefinitionVersionId),
                    nameof(PipelineInstanceDocument.DefinitionId), nameof(PipelineInstanceDocument.Id),
                    nameof(PipelineInstanceDocument.CorrelationId));
            }

            return query;
        }

        // The filter's dynamic-LINQ timestamp clauses, with their day-boundary rule for a zero time of day.
        private static void ApplyTimestamp(IAsyncDocumentQuery<PipelineInstanceDocument> query, TimestampFilter timestampFilter)
        {
            var column = timestampFilter.Column;
            var timestamp = timestampFilter.Timestamp;
            var isZeroTime = timestamp.TimeOfDay == TimeSpan.Zero;
            var startDay = new DateTimeOffset(timestamp.Date);
            var endDay = startDay.AddDays(1);

            switch (timestampFilter.Operator)
            {
                case TimestampFilterOperator.Is when isZeroTime:
                    query.WhereGreaterThanOrEqual(column, startDay.UtcDateTime);
                    query.WhereLessThan(column, endDay.UtcDateTime);
                    break;
                case TimestampFilterOperator.Is:
                    query.WhereEquals(column, timestamp.UtcDateTime);
                    break;
                case TimestampFilterOperator.IsNot when isZeroTime:
                    query.OpenSubclause();
                    query.WhereLessThan(column, startDay.UtcDateTime);
                    query.OrElse();
                    query.WhereGreaterThanOrEqual(column, endDay.UtcDateTime);
                    query.CloseSubclause();
                    break;
                case TimestampFilterOperator.IsNot:
                    query.WhereNotEquals(column, timestamp.UtcDateTime);
                    break;
                case TimestampFilterOperator.GreaterThan when isZeroTime:
                    query.WhereGreaterThan(column, endDay.UtcDateTime);
                    break;
                case TimestampFilterOperator.GreaterThan:
                    query.WhereGreaterThan(column, timestamp.UtcDateTime);
                    break;
                case TimestampFilterOperator.GreaterThanOrEqual when isZeroTime:
                    query.WhereGreaterThanOrEqual(column, startDay.UtcDateTime);
                    break;
                case TimestampFilterOperator.GreaterThanOrEqual:
                    query.WhereGreaterThanOrEqual(column, timestamp.UtcDateTime);
                    break;
                case TimestampFilterOperator.LessThan when isZeroTime:
                    query.WhereLessThan(column, startDay.UtcDateTime);
                    break;
                case TimestampFilterOperator.LessThan:
                    query.WhereLessThan(column, timestamp.UtcDateTime);
                    break;
                case TimestampFilterOperator.LessThanOrEqual when isZeroTime:
                    query.WhereLessThanOrEqual(column, endDay.UtcDateTime);
                    break;
                case TimestampFilterOperator.LessThanOrEqual:
                    query.WhereLessThanOrEqual(column, timestamp.UtcDateTime);
                    break;
            }
        }
    }
}
