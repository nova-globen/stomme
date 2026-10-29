#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// How the Wait step (n8n's Wait node) reads its time - ONE reading, for the step when it runs and for the editor's
    /// problems before publish: "For" as a person writes a duration (<c>30 minutes</c>, <c>1.5 hours</c>, <c>45s</c>, <c>1 week</c>)
    /// or as d.hh:mm:ss (<c>1.12:00:00</c>); "Until" as a moment (<c>2026-12-24T09:00:00Z</c>; no offset is UTC). Until wins.
    /// </summary>
    public static class PipelineWaits
    {
        /// <summary>The step's type name.</summary>
        public const string StepType = "Stomme.Wait";

        public const string ForInput = "For";

        public const string UntilInput = "Until";

        private static readonly Regex Spoken = new(@"^\s*(?<amount>\d+(?:[.,]\d+)?)\s*(?<unit>[a-zA-Z]+)\s*$", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, TimeSpan> Units = new(StringComparer.OrdinalIgnoreCase)
        {
            ["s"] = TimeSpan.FromSeconds(1), ["sec"] = TimeSpan.FromSeconds(1), ["secs"] = TimeSpan.FromSeconds(1), ["second"] = TimeSpan.FromSeconds(1), ["seconds"] = TimeSpan.FromSeconds(1),
            ["m"] = TimeSpan.FromMinutes(1), ["min"] = TimeSpan.FromMinutes(1), ["mins"] = TimeSpan.FromMinutes(1), ["minute"] = TimeSpan.FromMinutes(1), ["minutes"] = TimeSpan.FromMinutes(1),
            ["h"] = TimeSpan.FromHours(1), ["hr"] = TimeSpan.FromHours(1), ["hrs"] = TimeSpan.FromHours(1), ["hour"] = TimeSpan.FromHours(1), ["hours"] = TimeSpan.FromHours(1),
            ["d"] = TimeSpan.FromDays(1), ["day"] = TimeSpan.FromDays(1), ["days"] = TimeSpan.FromDays(1),
            ["w"] = TimeSpan.FromDays(7), ["week"] = TimeSpan.FromDays(7), ["weeks"] = TimeSpan.FromDays(7),
        };

        /// <summary>A duration as a person writes one, or as d.hh:mm:ss; never negative. The error says how to write one.</summary>
        public static bool TryParseDuration(string? text, out TimeSpan duration, out string? error)
        {
            duration = TimeSpan.Zero;
            error = null;
            var trimmed = (text ?? "").Trim();
            var spoken = Spoken.Match(trimmed);
            if (spoken.Success && Units.TryGetValue(spoken.Groups["unit"].Value, out var unit)
                && double.TryParse(spoken.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                duration = TimeSpan.FromTicks((long)(unit.Ticks * amount));
                return true;
            }

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero)
            {
                duration = span;
                return true;
            }

            error = $"'{trimmed}' is not a duration - write it as 30 minutes, 2 hours, 1 day, or 1.12:00:00.";
            return false;
        }

        /// <summary>A moment; one with no offset is UTC. The error says how to write one.</summary>
        public static bool TryParseMoment(string? text, out DateTimeOffset moment, out string? error)
        {
            error = null;
            if (DateTimeOffset.TryParse((text ?? "").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out moment))
            {
                return true;
            }

            error = $"'{(text ?? "").Trim()}' is not a moment - write it as 2026-12-24T09:00:00Z.";
            return false;
        }

        /// <summary>When the wait ends - Until, else now plus For - or null with the reason.</summary>
        public static DateTimeOffset? ResumeAt(string? until, string? duration, DateTimeOffset now, out string? error)
        {
            if (!string.IsNullOrWhiteSpace(until))
            {
                return TryParseMoment(until, out var moment, out error) ? moment : null;
            }

            if (string.IsNullOrWhiteSpace(duration))
            {
                error = "The step needs For (how long) or Until (a moment).";
                return null;
            }

            return TryParseDuration(duration, out var span, out error) ? now + span : null;
        }
    }
}
