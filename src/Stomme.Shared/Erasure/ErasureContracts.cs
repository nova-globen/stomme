#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Erasure
{
    /// <summary>What a store does with a subject's rows when the subject is erased (D112).</summary>
    public enum ErasureHandling
    {
        /// <summary>The rows go.</summary>
        Delete = 0,

        /// <summary>The subject's identifiers are replaced by an unlinkable placeholder; totals and counts survive (GDPR Recital 26: irreversible).</summary>
        Anonymise = 1,

        /// <summary>Kept unchanged for a period under a reason from the closed set, then deleted by the same job (Article 17(3), Article 5(1)(e)).</summary>
        Retain = 2,
    }

    /// <summary>The closed set of reasons a store may retain a subject's rows (D112).</summary>
    public enum RetentionReason
    {
        LegalObligation = 0,
        LegalClaims = 1,
        Security = 2,
    }

    /// <summary>One store a participant owns and its ONE handling; a retention names its reason and period.</summary>
    public sealed record ErasureDeclaration(string Store, ErasureHandling Handling, RetentionReason? Reason = null, TimeSpan? Period = null)
    {
        public static ErasureDeclaration Delete(string store) => new(store, ErasureHandling.Delete);

        public static ErasureDeclaration Anonymise(string store) => new(store, ErasureHandling.Anonymise);

        public static ErasureDeclaration Retain(string store, RetentionReason reason, TimeSpan period)
        {
            if (period <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(period), "A retention has a period; storage limitation (Article 5(1)(e)) allows no open-ended one.");
            }

            return new ErasureDeclaration(store, ErasureHandling.Retain, reason, period);
        }
    }

    /// <summary>
    /// The subject being erased: the site, Stomme's user id, the identity provider's subject id when the account
    /// had one (the OpenIddict subject claim - captured before the identity user is deleted, which is the only time it
    /// can be), and the username (the authorship stamps name the person by it). The participants act on these; the
    /// account itself may already be gone.
    /// </summary>
    public sealed record ErasureSubject(int SiteId, int UserId, string? IdentitySubject, string? Username = null);

    /// <summary>What one participant did for one store: rows affected, and for a retention the reason and the date the rows fall due.</summary>
    public sealed record ErasureOutcome(string Store, ErasureHandling Handling, int Affected, RetentionReason? Reason = null, DateTime? RetainedUntilUtc = null);

    /// <summary>
    /// The right-to-erasure seam (BL-116, D112), beside the portability export seam: a participant names the stores it
    /// owns with ONE handling each, erases a subject's rows accordingly (idempotently - a second run finds nothing to
    /// do), and for a retention purges the rows once the period has passed. A participant that throws fails the run for
    /// that subject; nothing is reported as erased that was not.
    /// </summary>
    public interface ISubjectErasureParticipant
    {
        string Name { get; }

        IReadOnlyList<ErasureDeclaration> Declarations { get; }

        Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default);

        /// <summary>Deletes the rows a Retain declaration kept, once due. Rows affected; nothing for a participant that retains nothing.</summary>
        Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default) => Task.FromResult(0);
    }

    /// <summary>Marks a subject for erasure and queues the run (what deleting a user does, self-service or by an administrator).</summary>
    public interface IErasureQueue
    {
        Task<Guid> EnqueueAsync(ErasureSubject subject, CancellationToken ct = default);
    }
}
