#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Stomme.Models.Health
{
    /// <summary>
    /// The health page's sparklines over BL-83's rollups (re-booked): one subject's
    /// availability per rollup period as an inline SVG polyline - the oldest period at the left, 100 percent at the top,
    /// nothing drawn for fewer than two periods. BCL-only, so the page and a test draw the same line; the SVG carries
    /// no colour of its own and takes the current text colour.
    /// </summary>
    public static class HealthSparkline
    {
        public const int Width = 96;

        public const int Height = 20;

        /// <summary>The polyline's points for the subject, oldest first; empty when the subject has fewer than two periods.</summary>
        public static string Points(IEnumerable<HealthRollupView> rollups, string subject)
        {
            ArgumentNullException.ThrowIfNull(rollups);
            var series = rollups
                .Where(rollup => string.Equals(rollup.Subject, subject, StringComparison.OrdinalIgnoreCase))
                .OrderBy(rollup => rollup.PeriodStartUtc)
                .Select(rollup => Math.Clamp(rollup.AvailabilityPercent, 0, 100))
                .ToList();
            if (series.Count < 2)
            {
                return string.Empty;
            }

            var step = (double)(Width - 2) / (series.Count - 1);
            var points = new StringBuilder();
            for (var i = 0; i < series.Count; i++)
            {
                var x = 1 + i * step;
                var y = 1 + (100 - series[i]) / 100 * (Height - 2);
                if (i > 0)
                {
                    points.Append(' ');
                }

                points.Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
            }

            return points.ToString();
        }

        /// <summary>The inline SVG for the subject, or an empty string when there is nothing to draw.</summary>
        public static string Svg(IEnumerable<HealthRollupView> rollups, string subject)
        {
            var points = Points(rollups, subject);
            return points.Length == 0
                ? string.Empty
                : $"<svg class=\"db-sparkline\" viewBox=\"0 0 {Width} {Height}\" width=\"{Width}\" height=\"{Height}\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"{System.Security.SecurityElement.Escape(subject)} availability\"><polyline fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" points=\"{points}\"></polyline></svg>";
        }
    }
}
