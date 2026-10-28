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
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    internal sealed class RavenPipelineDefinitionStore : RavenEngineStore, IPipelineDefinitionStore
    {
        public RavenPipelineDefinitionStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async Task<PipelineDefinition?> FindAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<PipelineDefinition?> FindAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var document = await query.FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<Page<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, DefaultOrder, cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async Task<Page<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async Task<IEnumerable<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async Task<IEnumerable<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            Order(query, order);
            var documents = await query.ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            var page = await FindManyAsync(filter, pageArgs, cancellationToken);
            return Page.Of(page.Items.Select(PipelineDefinitionSummary.FromDefinition).ToList(), page.TotalCount);
        }

        public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
        {
            var page = await FindManyAsync(filter, order, pageArgs, cancellationToken);
            return Page.Of(page.Items.Select(PipelineDefinitionSummary.FromDefinition).ToList(), page.TotalCount);
        }

        public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default) =>
            (await FindManyAsync(filter, cancellationToken)).Select(PipelineDefinitionSummary.FromDefinition).ToList();

        public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default) =>
            (await FindManyAsync(filter, order, cancellationToken)).Select(PipelineDefinitionSummary.FromDefinition).ToList();

        public async Task<PipelineDefinition?> FindLastVersionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken)
        {
            using var session = OpenSession();
            var query = Filtered(session, filter);
            OrderBy(query, nameof(PipelineDefinitionDocument.Version), typeof(int), descending: true);
            var document = await query.FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task SaveAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
        {
            Stamp(definition);
            using var session = OpenSession();
            await StoreAsync(session, [PipelineDefinitionDocument.From(definition, Serializer)], insertOnly: false, cancellationToken);
        }

        public async Task SaveManyAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken = default)
        {
            var list = definitions.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(d => PipelineDefinitionDocument.From(d, Serializer)), insertOnly: false, cancellationToken);
        }

        public async Task<long> DeleteAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var matched = await Filtered(session, filter).SelectFields<DefinitionIdOnly>().ToListAsync(cancellationToken);
            var definitionIds = matched.Select(m => m.DefinitionId).Distinct(StringComparer.Ordinal).ToList();
            if (definitionIds.Count == 0)
            {
                return 0;
            }

            var everyVersion = Query<PipelineDefinitionDocument>(session, filter.TenantAgnostic);
            In(everyVersion, nameof(PipelineDefinitionDocument.DefinitionId), definitionIds);
            await DeleteAsync(session, everyVersion, cancellationToken);
            return definitionIds.Count;
        }

        public async Task<bool> AnyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await Filtered(session, filter).AnyAsync(cancellationToken);
        }

        public async Task<long> CountDistinctAsync(CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var rows = await Query<PipelineDefinitionDocument>(session).SelectFields<DefinitionIdOnly>().ToListAsync(cancellationToken);
            return rows.Select(r => r.DefinitionId).Distinct(StringComparer.Ordinal).LongCount();
        }

        public async Task<bool> GetIsNameUnique(string name, string? definitionId = null, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var query = Query<PipelineDefinitionDocument>(session);
            Equal(query, nameof(PipelineDefinitionDocument.Name), name);
            NotEqual(query, nameof(PipelineDefinitionDocument.DefinitionId), definitionId);
            return !await query.AnyAsync(cancellationToken);
        }

        private static void DefaultOrder(IAsyncDocumentQuery<PipelineDefinitionDocument> query)
        {
            OrderBy(query, nameof(PipelineDefinitionDocument.CreatedAt), typeof(DateTime));
            OrderBy(query, nameof(PipelineDefinitionDocument.Id), typeof(string));
        }

        // PipelineDefinitionFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<PipelineDefinitionDocument> Filtered(IAsyncDocumentSession session, PipelineDefinitionFilter filter)
        {
            var query = Query<PipelineDefinitionDocument>(session, filter.TenantAgnostic);
            var definitionId = filter.DefinitionId ?? filter.DefinitionHandle?.DefinitionId;
            var versionOptions = filter.VersionOptions ?? filter.DefinitionHandle?.VersionOptions;
            var id = filter.Id ?? filter.DefinitionHandle?.DefinitionVersionId;

            Equal(query, nameof(PipelineDefinitionDocument.DefinitionId), definitionId);
            In(query, nameof(PipelineDefinitionDocument.DefinitionId), filter.DefinitionIds);
            Equal(query, nameof(PipelineDefinitionDocument.Id), id);
            In(query, nameof(PipelineDefinitionDocument.Id), filter.Ids);
            if (versionOptions is { } version)
            {
                WithVersion(query, version);
            }

            Equal(query, nameof(PipelineDefinitionDocument.MaterializerName), filter.MaterializerName);
            Equal(query, nameof(PipelineDefinitionDocument.Name), filter.Name);
            In(query, nameof(PipelineDefinitionDocument.Name), filter.Names);
            Equal(query, nameof(PipelineDefinitionDocument.UsableAsStep), filter.UsableAsStep);
            if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
            {
                ContainsAny(query, filter.SearchTerm,
                    nameof(PipelineDefinitionDocument.Name), nameof(PipelineDefinitionDocument.Description),
                    nameof(PipelineDefinitionDocument.Id), nameof(PipelineDefinitionDocument.DefinitionId));
            }

            Equal(query, nameof(PipelineDefinitionDocument.IsSystem), filter.IsSystem);
            Equal(query, nameof(PipelineDefinitionDocument.IsReadonly), filter.IsReadonly);
            return query;
        }

        // VersionedEntityExtensions.WithVersion(IQueryable), clause for clause.
        private static void WithVersion(IAsyncDocumentQuery<PipelineDefinitionDocument> query, VersionOptions version)
        {
            if (version.IsDraft)
            {
                Equal(query, nameof(PipelineDefinitionDocument.IsPublished), false);
            }
            else if (version.IsLatest)
            {
                Equal(query, nameof(PipelineDefinitionDocument.IsLatest), true);
            }
            else if (version.IsPublished)
            {
                Equal(query, nameof(PipelineDefinitionDocument.IsPublished), true);
            }
            else if (version.IsLatestOrPublished)
            {
                query.OpenSubclause();
                Equal(query, nameof(PipelineDefinitionDocument.IsPublished), true);
                query.OrElse();
                Equal(query, nameof(PipelineDefinitionDocument.IsLatest), true);
                query.CloseSubclause();
            }
            else if (version.IsLatestAndPublished)
            {
                Equal(query, nameof(PipelineDefinitionDocument.IsPublished), true);
                Equal(query, nameof(PipelineDefinitionDocument.IsLatest), true);
            }
            else if (version.Version > 0)
            {
                Equal(query, nameof(PipelineDefinitionDocument.Version), version.Version);
            }
        }
    }
}
