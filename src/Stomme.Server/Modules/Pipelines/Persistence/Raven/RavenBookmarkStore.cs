#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    internal sealed class RavenBookmarkStore : RavenEngineStore, IBookmarkStore
    {
        public RavenBookmarkStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async ValueTask SaveAsync(StoredBookmark record, CancellationToken cancellationToken = default)
        {
            Stamp(record);
            using var session = OpenSession();
            await StoreAsync(session, [BookmarkDocument.From(record, Serializer)], insertOnly: false, cancellationToken);
        }

        public async ValueTask SaveManyAsync(IEnumerable<StoredBookmark> records, CancellationToken cancellationToken)
        {
            var list = records.ToList();
            list.ForEach(Stamp);
            using var session = OpenSession();
            await StoreAsync(session, list.Select(b => BookmarkDocument.From(b, Serializer)), insertOnly: false, cancellationToken);
        }

        public async ValueTask<StoredBookmark?> FindAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity(Serializer);
        }

        public async ValueTask<IEnumerable<StoredBookmark>> FindManyAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity(Serializer)).ToList();
        }

        public async ValueTask<long> DeleteAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            using var session = OpenSession();
            return await DeleteAsync(session, Filtered(session, filter), cancellationToken);
        }

        // BookmarkFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<BookmarkDocument> Filtered(IAsyncDocumentSession session, BookmarkFilter filter)
        {
            var query = Query<BookmarkDocument>(session, filter.TenantAgnostic);
            Equal(query, nameof(BookmarkDocument.Id), filter.BookmarkId);
            In(query, nameof(BookmarkDocument.Id), filter.BookmarkIds);
            Equal(query, nameof(BookmarkDocument.CorrelationId), filter.CorrelationId);
            Equal(query, nameof(BookmarkDocument.Hash), filter.Hash);
            In(query, nameof(BookmarkDocument.Hash), filter.Hashes);
            Equal(query, nameof(BookmarkDocument.PipelineInstanceId), filter.PipelineInstanceId);
            In(query, nameof(BookmarkDocument.PipelineInstanceId), filter.PipelineInstanceIds);
            Equal(query, nameof(BookmarkDocument.Name), filter.Name);
            In(query, nameof(BookmarkDocument.Name), filter.Names);
            Equal(query, nameof(BookmarkDocument.StepInstanceId), filter.StepInstanceId);
            return query;
        }
    }
}
