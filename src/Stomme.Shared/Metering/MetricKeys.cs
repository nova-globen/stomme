using System;

namespace Stomme.Metering
{
    /// <summary>
    /// What a metric key and a usage source may look like (D102).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A metric key names a quota, so it is compared, stored and filtered - and a value that only DIFFERS by case
    /// or whitespace would silently become a second quota. Keys are therefore canonical by construction:
    /// lowercase, and drawn from a small alphabet. A non-conforming value is REJECTED, never rewritten, because a
    /// rewritten key charges a quota the caller did not name.
    /// </para>
    /// <para>
    /// <c>Source</c> gets the same treatment for a different reason: it is producer-written free text that lands in
    /// an append-only ledger kept for months. the predecessor platform filled it with a composed "provider:model:agent" key, but
    /// nothing stopped a caller putting an email address, a file name or a prompt fragment there - which is how a
    /// usage ledger quietly becomes a personal-data store.
    /// </para>
    /// </remarks>
    public static class MetricKeys
    {
        /// <summary>The longest a metric key may be; it is stored in a normalized column.</summary>
        public const int MaxMetricLength = 100;

        /// <summary>The longest a source key may be.</summary>
        public const int MaxSourceLength = 100;

        /// <summary>Whether <paramref name="metric"/> is a canonical metric key.</summary>
        public static bool IsMetric(string? metric) => IsCanonical(metric, MaxMetricLength);

        /// <summary>Whether <paramref name="source"/> is a canonical source key (null and empty are allowed).</summary>
        public static bool IsSource(string? source) => string.IsNullOrEmpty(source) || IsCanonical(source, MaxSourceLength);

        /// <summary>Throws unless <paramref name="metric"/> is canonical.</summary>
        public static string RequireMetric(string? metric) =>
            IsMetric(metric)
                ? metric!
                : throw new ArgumentException(
                    $"'{metric}' is not a metric key: use lowercase letters, digits, '.', '_', ':' or '-', at most {MaxMetricLength} characters.",
                    nameof(metric));

        /// <summary>Throws unless <paramref name="source"/> is canonical or absent.</summary>
        public static string? RequireSource(string? source) =>
            IsSource(source)
                ? source
                : throw new ArgumentException(
                    $"'{source}' is not a usage source: use lowercase letters, digits, '.', '_', ':' or '-', at most {MaxSourceLength} characters. " +
                    "The source names the PRODUCER; it is not a place for user data.",
                    nameof(source));

        /// <summary>
        /// The normalized twin of a raw value, for the D49 pair (raw kept for display, normalized for filtering).
        /// </summary>
        /// <remarks>
        /// D49 is a PAIR, not a replacement: lower-casing in place would destroy the spelling a vendor uses
        /// ("gpt-4o-mini", "Claude-Sonnet-4.5") on the very dashboard that renders it. Filters use this twin;
        /// the UI renders the raw value.
        /// </remarks>
        public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static bool IsCanonical(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            {
                return false;
            }

            foreach (var character in value)
            {
                var allowed = (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.' || character == '_' || character == ':' || character == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
