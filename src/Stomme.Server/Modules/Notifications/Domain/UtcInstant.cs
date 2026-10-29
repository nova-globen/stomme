using System;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>
    /// The notifications domain's UTC rule: every instant is a UTC <see cref="DateTime"/> - never a
    /// <c>DateTimeOffset</c> (not in the FindAsync safe set) and never a Local or Unspecified value. Inputs are
    /// REQUIRED to be <see cref="DateTimeKind.Utc"/>; persisted values are re-stamped UTC on read because a relational
    /// provider (SQLite, MEASURED) materializes a scalar DateTime column as <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    internal static class UtcInstant
    {
        public static DateTime Require(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", parameterName);
            }

            return value;
        }

        public static DateTime Stamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        public static DateTime? Stamp(DateTime? value) => value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;
    }
}
