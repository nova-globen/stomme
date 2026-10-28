#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
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
    internal sealed class RavenTriggerStore : RavenEngineStore, ITriggerStore
    {
        public RavenTriggerStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async ValueTask SaveAsync(StoredTrigger record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [TriggerDocument.From(record, Serializer)], insertOnly: false, cancellationToken);
        }

        public async ValueTask SaveManyAsync(IEnumerable<StoredTrigger> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(t => TriggerDocument.From(t, Serializer)), insertOnly: false, cancellationToken);
        }

        public async ValueTask<StoredTrigger?> FindAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async ValueTask<IEnumerable<StoredTrigger>> FindManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public ValueTask<Page<StoredTrigger>> FindManyAsync(TriggerFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default) =>
            FindManyAsync(filter, pageArgs, new StoredTriggerOrder<string>(x => x.Id, OrderDirection.Ascending), cancellationToken);

        public async ValueTask<Page<StoredTrigger>> FindManyAsync<TProp>(TriggerFilter filter, PageArgs pageArgs, StoredTriggerOrder<TProp> order, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, order), cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async ValueTask ReplaceAsync(IEnumerable<StoredTrigger> removed, IEnumerable<StoredTrigger> added, CancellationToken cancellationToken = default)
        {
            var toAdd = added.ToList();
            toAdd.ForEach(Stamp);
            using var session = OpenSession();
            foreach (var trigger in removed)
            {
                session.Delete(DocumentId<TriggerDocument>(trigger.Id));
            }

            await StoreAsync(session, toAdd.Select(t => TriggerDocument.From(t, Serializer)), insertOnly: false, cancellationToken);
        }

        public async ValueTask<long> DeleteManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        // TriggerFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<TriggerDocument> Filtered(IAsyncDocumentSession session, TriggerFilter filter)
        {
            var query = Query<TriggerDocument>(session, filter.TenantAgnostic);
            Equal(query, nameof(TriggerDocument.Id), filter.Id);
            In(query, nameof(TriggerDocument.Id), filter.Ids);
            Equal(query, nameof(TriggerDocument.PipelineDefinitionId), filter.PipelineDefinitionId);
            In(query, nameof(TriggerDocument.PipelineDefinitionId), filter.PipelineDefinitionIds);
            Equal(query, nameof(TriggerDocument.PipelineDefinitionVersionId), filter.PipelineDefinitionVersionId);
            In(query, nameof(TriggerDocument.PipelineDefinitionVersionId), filter.PipelineDefinitionVersionIds);
            Equal(query, nameof(TriggerDocument.Name), filter.Name);
            In(query, nameof(TriggerDocument.Name), filter.Names);
            Equal(query, nameof(TriggerDocument.Hash), filter.Hash);
            return query;
        }
    }
}
