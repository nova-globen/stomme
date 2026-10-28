#nullable enable
namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// The granularity a rollup row covers.
    /// </summary>
    /// <remarks>
    /// Three windows rather than one raw series, because the three have genuinely different lifetimes: Minute rows
    /// are the live picture and are trimmed at 48 hours ON WRITE (so they are bounded even when the purge job is
    /// disabled, which an operator may do); Hour and Day rows are the history and age out through the contributed
    /// retention pass. Storing only raw samples and aggregating on read would put an unbounded scan behind an
    /// admin page.
    /// </remarks>
    public enum HealthRollupWindow
    {
        Minute,
        Hour,
        Day
    }
}
