namespace Stomme.Modules.Metering.Domain
{
    /// <summary>The window a quota counts over (D102).</summary>
    /// <remarks>
    /// the predecessor platform had TWO rival authorities with different windows: <c>Quota.IsPeriodElapsed</c> slid from the period
    /// start, while the AI guard counted the raw ledger from a CALENDAR month start. One had to win, and the
    /// calendar window did: it is the one an operator can reason about ("this month"), and it is the one a repair
    /// pass can recompute from the ledger without knowing when the quota row was created.
    /// </remarks>
    public enum QuotaPeriod
    {
        /// <summary>Counts for ever; the window never resets - and so a purged ledger can never repair it.</summary>
        Total = 0,

        /// <summary>Resets at UTC midnight.</summary>
        Daily = 1,

        /// <summary>Resets on the first of the month, UTC.</summary>
        Monthly = 2
    }

    /// <summary>Whether a quota is enforced.</summary>
    public enum QuotaStatus
    {
        Active = 0,

        /// <summary>Recorded but not enforced - consumption still accrues.</summary>
        Suspended = 1
    }

    /// <summary>
    /// Whether a ledger row has been folded into its quota counter (D102).
    /// </summary>
    /// <remarks>
    /// The append and the fold are two commits on every provider, so this flag is what makes the pair recoverable:
    /// a row that is <see cref="Pending"/> after the grace period was appended but never counted, and the recovery
    /// job folds it. Without it a crash between the two would silently under-count for ever.
    /// </remarks>
    public enum FoldState
    {
        Pending = 0,
        Applied = 1
    }
}
