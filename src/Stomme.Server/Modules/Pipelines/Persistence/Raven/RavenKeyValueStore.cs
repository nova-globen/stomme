#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    internal sealed class RavenKeyValueStore : RavenEngineStore, IKeyValueStore
    {
        public RavenKeyValueStore(IDocumentStore store, ITenantContext tenantContext, IRavenTenantDatabaseResolver databases, ITenantAccessor tenants, EngineEntitySerializer serializer)
            : base(store, tenantContext, databases, tenants, serializer)
        {
        }

        public async Task SaveAsync(SerializedKeyValuePair keyValuePair, CancellationToken cancellationToken)
        {
            Stamp(keyValuePair);
            using var session = OpenSession();
            await StoreAsync(session, [KeyValueDocument.From(keyValuePair)], insertOnly: false, cancellationToken);
        }

        public async Task<SerializedKeyValuePair?> FindAsync(KeyValueFilter filter, CancellationToken cancellationToken)
        {
            using var session = OpenSession();
            var document = await Filtered(session, filter).FirstOrDefaultAsync(cancellationToken);
            return document?.ToEntity();
        }

        public async Task<IEnumerable<SerializedKeyValuePair>> FindManyAsync(KeyValueFilter filter, CancellationToken cancellationToken)
        {
            using var session = OpenSession();
            var documents = await Filtered(session, filter).ToListAsync(cancellationToken);
            return documents.Select(d => d.ToEntity()).ToList();
        }

        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            using var session = OpenSession();
            session.Delete(DocumentId<KeyValueDocument>(key));
            await session.SaveChangesAsync(cancellationToken);
        }

        // KeyValueFilter.Apply, clause for clause.
        private IAsyncDocumentQuery<KeyValueDocument> Filtered(IAsyncDocumentSession session, KeyValueFilter filter)
        {
            var query = Query<KeyValueDocument>(session);
            if (filter.Key is not null)
            {
                if (filter.StartsWith)
                {
                    query.WhereStartsWith(nameof(KeyValueDocument.Id), filter.Key, exact: true);
                }
                else
                {
                    Equal(query, nameof(KeyValueDocument.Id), filter.Key);
                }
            }

            In(query, nameof(KeyValueDocument.Id), filter.Keys);
            return query;
        }
    }
}
