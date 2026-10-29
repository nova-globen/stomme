#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// n8n's workflow "Timezone": the zone a pipeline reads time in (<see cref="PipelineGraph.TimeZone"/>) - its
    /// <c>{{ $now }}</c> and <c>{{ $run.startedAt }}</c> with that zone's offset, its <c>{{ $today }}</c> that zone's date, and
    /// a Schedule with no zone of its own read there. Empty is UTC, as before. An IANA name - Europe/Stockholm - followed through
    /// daylight saving; a name the server does not know is refused when the pipeline is saved.
    /// </summary>
    public static class PipelineTimeZone
    {
        /// <summary>Zones offered where one is chosen; any other IANA name the server knows is taken as well.</summary>
        public static readonly IReadOnlyList<string> Suggested = new[]
        {
            "Europe/London", "Europe/Stockholm", "Europe/Berlin", "Europe/Paris", "Europe/Helsinki", "America/New_York",
            "America/Chicago", "America/Los_Angeles", "America/Sao_Paulo", "Asia/Tehran", "Asia/Dubai", "Asia/Kolkata",
            "Asia/Singapore", "Asia/Tokyo", "Australia/Sydney",
        };

        /// <summary>The zone as a pipeline keeps it: null for UTC (nothing given, "UTC", "Etc/UTC"), else the name trimmed.</summary>
        public static string? Normalize(string? name) =>
            string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(name.Trim(), "Etc/UTC", StringComparison.OrdinalIgnoreCase)
                ? null
                : name.Trim();

        /// <summary>The zone a name is - UTC for none, and for one this machine does not know.</summary>
        public static TimeZoneInfo Find(string? name) => Normalize(name) is { } named && TryFind(named, out var zone) ? zone : TimeZoneInfo.Utc;

        /// <summary>Why a name cannot be a pipeline's zone, or null.</summary>
        public static string? Problem(string? name) =>
            Normalize(name) is { } named && !TryFind(named, out _)
                ? $"'{named}' is not a time zone this server knows - use an IANA name such as Europe/Stockholm, or leave it empty for UTC."
                : null;

        /// <summary>A moment as a pipeline reads it in its zone: ISO 8601 to the second - 'Z' in UTC, else with the zone's offset.</summary>
        public static string Moment(DateTimeOffset moment, TimeZoneInfo zone) => zone.Equals(TimeZoneInfo.Utc)
            ? moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The date a moment falls on in the zone.</summary>
        public static string Date(DateTimeOffset moment, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        private static bool TryFind(string name, out TimeZoneInfo zone)
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(name);
                return true;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                zone = TimeZoneInfo.Utc;
                return false;
            }
        }
    }
}
