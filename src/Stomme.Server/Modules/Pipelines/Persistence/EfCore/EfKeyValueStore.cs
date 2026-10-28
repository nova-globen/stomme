#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfKeyValueStore : EfEngineStore, IKeyValueStore
    {
        public EfKeyValueStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async Task SaveAsync(SerializedKeyValuePair keyValuePair, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [keyValuePair], NoShadows, cancellationToken);
        }

        public async Task<SerializedKeyValuePair?> FindAsync(KeyValueFilter filter, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await filter.Apply(Read<SerializedKeyValuePair>(db)).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<IEnumerable<SerializedKeyValuePair>> FindManyAsync(KeyValueFilter filter, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await filter.Apply(Read<SerializedKeyValuePair>(db)).ToListAsync(cancellationToken);
        }

        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            await Read<SerializedKeyValuePair>(db).Where(x => x.Id == key).ExecuteDeleteAsync(cancellationToken);
        }

        private static void NoShadows(EntityEntry<SerializedKeyValuePair> entry, SerializedKeyValuePair pair)
        {
        }
    }
}
