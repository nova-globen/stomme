using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Modules.Guestbook.Application;
using Stomme.Modules.Guestbook.Services;
using Stomme.Shared;
using GuestbookEntry = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IGuestbookService"/>. Every call is a CQRS dispatch through the
    /// <see cref="IMediator"/> — a query for reads, a command for the write, a stream for incremental reads —
    /// so the business logic lives in mediator handlers behind the behavior pipeline, and this service is a
    /// thin transport shim. This is exactly the shape the parked <c>FeatureAdminService</c> anticipated
    /// ("when the mediator lands, these become thin wrappers", docs/decisions.md D14); the Guestbook module
    /// is the first place in the host where it is real.
    /// <para>
    /// It is an <see cref="ITransientService"/>, registered explicitly in the module's <c>ServerStartup</c>
    /// so it wins over the client HTTP implementation on the server (the same pattern HtmlText uses).
    /// </para>
    /// </summary>
    public sealed class ServerGuestbookService(IMediator mediator) : IGuestbookService, ITransientService
    {
        public async Task<List<GuestbookEntry>> ListEntriesAsync(int moduleId)
        {
            var entries = await mediator.Send(new ListGuestbookEntriesQuery(moduleId));
            return entries.ToList();
        }

        public Task<GuestbookEntry> SignAsync(GuestbookEntry entry) =>
            mediator.Send(new SignGuestbookCommand(entry.ModuleId, entry.Author, entry.Message));

        public IAsyncEnumerable<GuestbookEntry> StreamEntries(int moduleId, CancellationToken ct = default) =>
            mediator.CreateStream(new StreamGuestbookEntriesQuery(moduleId), ct);
    }
}
