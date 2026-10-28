using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Guestbook.Persistence;
using Stomme.Mediator;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// CQRS QUERY: list the signatures for one guestbook (module instance), newest first. A query has no
    /// validator and raises no events — it reads through the module-specific repository method whose
    /// <c>ModuleId</c> filter runs in the database. Dispatched through <c>IMediator.Send</c>.
    /// </summary>
    public sealed record ListGuestbookEntriesQuery(int ModuleId) : IQuery<IReadOnlyList<WireModel>>;

    public sealed class ListGuestbookEntriesQueryHandler(IGuestbookRepository repository)
        : IQueryHandler<ListGuestbookEntriesQuery, IReadOnlyList<WireModel>>
    {
        public async Task<IReadOnlyList<WireModel>> Handle(ListGuestbookEntriesQuery request, CancellationToken ct)
        {
            var entries = await repository.GetByModuleAsync(request.ModuleId, ct);
            return entries.Select(GuestbookMapper.ToWire).ToList();
        }
    }
}
