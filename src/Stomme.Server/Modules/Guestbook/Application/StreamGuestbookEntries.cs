using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Stomme.Guestbook.Persistence;
using Stomme.Mediator;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// CQRS STREAM REQUEST: stream a guestbook's signatures one at a time via
    /// <c>IMediator.CreateStream</c>/<see cref="IStreamRequestHandler{TRequest, TResponse}"/>. This showcases
    /// the mediator's streaming path (an <see cref="IAsyncEnumerable{T}"/> response rather than a single
    /// value) — the controller exposes it as an async-streaming JSON endpoint, so a large book can be sent
    /// incrementally instead of buffered whole.
    /// </summary>
    public sealed record StreamGuestbookEntriesQuery(int ModuleId) : IStreamRequest<WireModel>;

    public sealed class StreamGuestbookEntriesQueryHandler(IGuestbookRepository repository)
        : IStreamRequestHandler<StreamGuestbookEntriesQuery, WireModel>
    {
        public async IAsyncEnumerable<WireModel> Handle(
            StreamGuestbookEntriesQuery request, [EnumeratorCancellation] CancellationToken ct)
        {
            var entries = await repository.GetByModuleAsync(request.ModuleId, ct);
            foreach (var entry in entries.OrderBy(entry => entry.SignedOnUtc))
            {
                ct.ThrowIfCancellationRequested();
                yield return GuestbookMapper.ToWire(entry);
            }
        }
    }
}
