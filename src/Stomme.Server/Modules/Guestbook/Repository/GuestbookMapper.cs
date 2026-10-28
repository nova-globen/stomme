using Stomme.Modules.Guestbook.Domain;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Guestbook.Persistence
{
    /// <summary>
    /// Translates between the <see cref="GuestbookEntryAggregate"/> (server domain) and the
    /// <see cref="WireModel"/> (client/server wire contract) at the application boundary — the
    /// aggregate-beside-wire-contract split (docs/decisions.md D17). Queries map aggregate → wire model for
    /// the response; there is no wire → aggregate mapping because a new signature is created through the
    /// aggregate's <c>Sign</c> factory, never rebuilt from a DTO.
    /// </summary>
    internal static class GuestbookMapper
    {
        public static WireModel ToWire(GuestbookEntryAggregate aggregate) => new()
        {
            Id = aggregate.Id,
            ModuleId = aggregate.ModuleId,
            Author = aggregate.Author.Value,
            Message = aggregate.Message,
            SignedOnUtc = aggregate.SignedOnUtc,
        };
    }
}
