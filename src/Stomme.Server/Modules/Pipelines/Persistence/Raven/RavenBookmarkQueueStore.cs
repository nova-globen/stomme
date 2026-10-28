#nullable enable
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
    internal sealed class RavenBookmarkQueueStore : RavenEngineStore, IBookmarkQueueStore
    {
        public RavenBookmarkQueueStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async Task SaveAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [BookmarkQueueDocument.From(record, Serializer)], insertOnly: false, cancellationToken);
        }

        public async Task AddAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [BookmarkQueueDocument.From(record, Serializer)], insertOnly: true, cancellationToken);
        }

        public async Task<BookmarkQueueItem?> FindAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async Task<IEnumerable<BookmarkQueueItem>> FindManyAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default) =>
            PageAsync(pageArgs, new BookmarkQueueFilter(), orderBy, cancellationToken);

        public async Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueFilter filter, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var page = await PageAsync(() => Filtered(session, filter), pageArgs, q => Order(q, orderBy), cancellationToken);
            return Page.Of(page.Items.Select(d => d.ToEntity(Serializer)).ToList(), page.TotalCount);
        }

        public async Task<long> DeleteAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        // BookmarkQueueFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<BookmarkQueueDocument> Filtered(IAsyncDocumentSession session, BookmarkQueueFilter filter)
        {
            var query = Query<BookmarkQueueDocument>(session, filter.TenantAgnostic);
            Equal(query, nameof(BookmarkQueueDocument.Id), filter.Id);
            In(query, nameof(BookmarkQueueDocument.Id), filter.Ids);
            Equal(query, nameof(BookmarkQueueDocument.BookmarkId), filter.BookmarkId);
            Equal(query, nameof(BookmarkQueueDocument.StimulusHash), filter.BookmarkHash);
            Equal(query, nameof(BookmarkQueueDocument.StepInstanceId), filter.StepInstanceId);
            Equal(query, nameof(BookmarkQueueDocument.StepTypeName), filter.StepTypeName);
            Equal(query, nameof(BookmarkQueueDocument.PipelineInstanceId), filter.PipelineInstanceId);
            Before(query, nameof(BookmarkQueueDocument.CreatedAt), filter.CreatedAtLessThan);
            return query;
        }
    }
}
