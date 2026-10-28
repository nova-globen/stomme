#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfBookmarkStore : EfEngineStore, IBookmarkStore
    {
        public EfBookmarkStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async ValueTask SaveAsync(StoredBookmark record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async ValueTask SaveManyAsync(IEnumerable<StoredBookmark> records, CancellationToken cancellationToken)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, records.ToList(), Shadows, cancellationToken);
        }

        public async ValueTask<StoredBookmark?> FindAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async ValueTask<IEnumerable<StoredBookmark>> FindManyAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public async ValueTask<long> DeleteAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        private static IQueryable<StoredBookmark> Query(EngineDbContext db, BookmarkFilter filter) =>
            filter.Apply(Read<StoredBookmark>(db, filter.TenantAgnostic));

        private static IQueryable<Row<StoredBookmark>> Rows(IQueryable<StoredBookmark> query) =>
            query.Select(bookmark => new Row<StoredBookmark>
            {
                Entity = bookmark,
                A = EF.Property<string>(bookmark, EngineDbContext.DataColumn),
                B = EF.Property<string>(bookmark, EngineDbContext.MetadataColumn),
            });

        private StoredBookmark Hydrate(Row<StoredBookmark> row)
        {
            row.Entity.Payload = Serializer.PayloadFrom(row.A);
            row.Entity.Metadata = Serializer.PayloadFrom<Dictionary<string, string>>(row.B);
            return row.Entity;
        }

        private void Shadows(EntityEntry<StoredBookmark> entry, StoredBookmark bookmark)
        {
            SetText(entry, EngineDbContext.DataColumn, Serializer.PayloadOf(bookmark.Payload));
            SetText(entry, EngineDbContext.MetadataColumn, Serializer.PayloadOf(bookmark.Metadata));
        }
    }
}
