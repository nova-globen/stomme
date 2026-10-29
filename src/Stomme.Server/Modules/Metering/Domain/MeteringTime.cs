#nullable enable
using System;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// The one way this module turns a <see cref="DateTime"/> into a UTC one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An instant read back from the store is <see cref="DateTimeKind.Unspecified"/>, and it is ALREADY UTC.</b>
    /// Every metering instant is written by the server clock in UTC, but the relational provider returns it with no
    /// kind. Calling <c>ToUniversalTime()</c> on such a value does not "make it UTC" - it treats it as LOCAL and
    /// subtracts the machine's offset, moving the instant backwards by that offset.
    /// </para>
    /// <para>
    /// <b>Measured, not theoretical.</b> The rollup pass snapped days with <c>ToUniversalTime()</c> and split two
    /// calls an hour apart across two different DAYS - one of them a day that had not happened - because the
    /// conversion pushed the earlier one across midnight. The same conversion sits under the quota window, where it
    /// would move consumption recorded just after midnight on the first of a month into the PREVIOUS month's
    /// allowance, and a fold recovered by the recovery job into the wrong window entirely. On a UTC machine every
    /// one of those bugs is invisible, which is why this is a named helper and not a local expression.
    /// </para>
    /// </remarks>
    public static class MeteringTime
    {
        /// <summary>
        /// The UTC instant <paramref name="value"/> denotes: an unspecified kind is ASSUMED UTC (the store wrote it
        /// that way), a local one is converted, a UTC one is returned unchanged.
        /// </summary>
        public static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        /// <summary>The UTC midnight beginning the day that contains <paramref name="value"/>.</summary>
        public static DateTime DayStart(DateTime value)
        {
            var utc = AsUtc(value);
            return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
