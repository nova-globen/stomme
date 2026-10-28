#nullable enable
using System;

namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// The one place an instant becomes UTC in this module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because of a defect that was measured, not imagined.</b> Every relational provider hands a
    /// stored instant back with <see cref="DateTimeKind.Unspecified"/>. <c>ToUniversalTime()</c> does not turn
    /// such a value into UTC — it assumes it is LOCAL and subtracts the machine's offset. In the metering module
    /// (D102) that quietly moved usage into the previous day, and would have charged consumption recorded just
    /// after midnight on the 1st to the PREVIOUS month's allowance.
    /// </para>
    /// <para>
    /// <b>It is invisible on a UTC machine</b>, which is what CI runs on, so the cover for it cannot be behavioural
    /// alone — it needs an assertion about the ticks themselves.
    /// </para>
    /// <para>
    /// <b>Known duplication, deliberately taken.</b> This is the same helper as the metering module's
    /// <c>MeteringTime</c>. The honest fix is one helper in the shared kernel used by both, but that edits a
    /// module that has already landed and been gated, so it is booked rather than smuggled into this row. The two
    /// copies are identical in behaviour, and each carries this note so a later reader knows there is a third
    /// place to change if the rule ever changes.
    /// </para>
    /// </remarks>
    public static class HealthTime
    {
        /// <summary>
        /// The instant as UTC: an unspecified instant is ASSUMED to already be UTC (it came from the store, which
        /// only ever stored UTC), a local one is converted, a UTC one is untouched.
        /// </summary>
        public static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
