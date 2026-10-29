using System;

namespace Stomme.Modules.Guestbook.Models
{
    /// <summary>
    /// The wire contract for a guestbook signature — the shape that crosses the client/server boundary
    /// (component → <c>IGuestbookService</c> → controller → back). It is DELIBERATELY separate from the
    /// server-side <c>GuestbookEntryAggregate</c> (the DDD aggregate root): the aggregate carries behavior,
    /// invariants, a value object and a version token, none of which belong on the DTO. A mapper translates
    /// between them at the repository boundary — the same aggregate-beside-wire-contract split the framework
    /// store uses.
    /// </summary>
    public class GuestbookEntry
    {
        /// <summary>Stable identity of the signature (the aggregate id).</summary>
        public Guid Id { get; set; }

        /// <summary>The module instance this signature belongs to; each placed Guestbook module is its own book.</summary>
        public int ModuleId { get; set; }

        /// <summary>Display name of the signer.</summary>
        public string Author { get; set; }

        /// <summary>The message left in the guestbook.</summary>
        public string Message { get; set; }

        /// <summary>When the guestbook was signed (UTC).</summary>
        public DateTime SignedOnUtc { get; set; }
    }
}
