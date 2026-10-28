#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Engine.Pipelines.Runtime.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    internal sealed class EfBookmarkQueueStore : EfEngineStore, IBookmarkQueueStore
    {
        public EfBookmarkQueueStore(IDbContextFactory<EngineDbContext> contexts, EngineEntitySerializer serializer)
            : base(contexts, serializer)
        {
        }

        public async Task SaveAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await UpsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async Task AddAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            await InsertAsync(db, [record], Shadows, cancellationToken);
        }

        public async Task<BookmarkQueueItem?> FindAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var row = await Rows(Query(db, filter)).FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : Hydrate(row);
        }

        public async Task<IEnumerable<BookmarkQueueItem>> FindManyAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var rows = await Rows(Query(db, filter)).ToListAsync(cancellationToken);
            return rows.Select(Hydrate).ToList();
        }

        public Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default) =>
            PageAsync(pageArgs, new BookmarkQueueFilter(), orderBy, cancellationToken);

        public async Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueFilter filter, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            var query = Query(db, filter);
            var count = await query.LongCountAsync(cancellationToken);
            var rows = await Rows(query.OrderBy(orderBy).Paginate(pageArgs)).ToListAsync(cancellationToken);
            return Page.Of(rows.Select(Hydrate).ToList(), count);
        }

        public async Task<long> DeleteAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default)
        {
            await using var db = await OpenAsync(cancellationToken);
            return await Query(db, filter).ExecuteDeleteAsync(cancellationToken);
        }

        private static IQueryable<BookmarkQueueItem> Query(EngineDbContext db, BookmarkQueueFilter filter) =>
            filter.Apply(Read<BookmarkQueueItem>(db, filter.TenantAgnostic));

        private static IQueryable<Row<BookmarkQueueItem>> Rows(IQueryable<BookmarkQueueItem> query) =>
            query.Select(item => new Row<BookmarkQueueItem>
            {
                Entity = item,
                A = EF.Property<string>(item, EngineDbContext.DataColumn),
            });

        private BookmarkQueueItem Hydrate(Row<BookmarkQueueItem> row)
        {
            row.Entity.Options = Serializer.PayloadFrom<ResumeBookmarkOptions>(row.A);
            return row.Entity;
        }

        private void Shadows(EntityEntry<BookmarkQueueItem> entry, BookmarkQueueItem item) =>
            SetText(entry, EngineDbContext.DataColumn, Serializer.PayloadOf(item.Options));
    }
}
