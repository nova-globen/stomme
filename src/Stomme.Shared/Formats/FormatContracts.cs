#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Formats
{
    /// <summary>What a format renders: a date, a number, a currency, or a locale (the culture alone drives rendering).</summary>
    public enum FormatKind
    {
        Date = 0,
        Number = 1,
        Currency = 2,
        Locale = 3,
    }

    /// <summary>Where a format comes from: a compiled system preset, or a definition a site's administrators saved.</summary>
    public enum FormatScope
    {
        System = 0,
        Tenant = 1,
    }

    /// <summary>
    /// A display format as consumers see it: the same shape for a system preset and a tenant definition. <see cref="Format"/>
    /// and <see cref="TryParse"/> NEVER throw - an unknown culture, a local <see cref="DateTime"/>, a bad time zone or an
    /// unconvertible value fall back to an invariant rendering (a naive implementation throws on the first two).
    /// </summary>
    public sealed record FormatDescriptor(
        Guid Id,
        string Name,
        FormatKind Kind,
        string Pattern,
        string Culture,
        FormatScope Scope,
        string TimeZone = "")
    {
        /// <summary>The culture named by <see cref="Culture"/>, or the invariant culture when the name is empty or unknown. Not serialised: a wire descriptor carries the name.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public CultureInfo CultureInfo => ResolveCulture(Culture);

        public static CultureInfo ResolveCulture(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return CultureInfo.InvariantCulture;
            }

            try
            {
                return CultureInfo.GetCultureInfo(name.Trim(), predefinedOnly: true);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }

        /// <summary>The kind's sample value rendered with this format - what a picker shows beside the name.</summary>
        public string Example() => Format(SampleValue());

        /// <summary>Renders <paramref name="value"/>; falls back to an invariant string rather than throwing.</summary>
        public string Format(object? value)
        {
            if (value is null)
            {
                return string.Empty;
            }

            var culture = CultureInfo;
            try
            {
                return Kind switch
                {
                    FormatKind.Date => InTimeZone(ToDate(value)).ToString(Pattern, culture),
                    FormatKind.Number or FormatKind.Currency => ToDecimal(value).ToString(Pattern, culture),
                    FormatKind.Locale => string.Format(culture, "{0}", value),
                    _ => Convert.ToString(value, culture) ?? string.Empty,
                };
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException
                or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                try
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
                catch (Exception inner) when (inner is FormatException or InvalidCastException)
                {
                    return value.ToString() ?? string.Empty;
                }
            }
        }

        /// <summary>Parses <paramref name="text"/> under this format; false rather than an exception for anything it cannot read.</summary>
        public bool TryParse(string? text, out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var culture = CultureInfo;
            switch (Kind)
            {
                case FormatKind.Date:
                    if (DateTimeOffset.TryParseExact(text, Pattern, culture, DateTimeStyles.AssumeUniversal, out var exact))
                    {
                        value = exact;
                        return true;
                    }

                    if (DateTimeOffset.TryParse(text, culture, DateTimeStyles.AssumeUniversal, out var loose))
                    {
                        value = loose;
                        return true;
                    }

                    return false;
                case FormatKind.Number:
                case FormatKind.Currency:
                    var styles = Kind == FormatKind.Currency ? NumberStyles.Currency : NumberStyles.Number;
                    if (decimal.TryParse(text, styles, culture, out var number))
                    {
                        value = number;
                        return true;
                    }

                    return false;
                default:
                    value = text;
                    return true;
            }
        }

        private DateTimeOffset InTimeZone(DateTimeOffset value) =>
            string.IsNullOrWhiteSpace(TimeZone) ? value : TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(TimeZone));

        private object SampleValue() => Kind switch
        {
            FormatKind.Date => new DateTimeOffset(2026, 6, 23, 14, 5, 9, TimeSpan.Zero),
            FormatKind.Number => 1234567.89m,
            FormatKind.Currency => 1234.5m,
            // A locale's example is what the locale DOES to a number, not its own name (the invariant one is empty).
            _ => 1234567.89m,
        };

        private static DateTimeOffset ToDate(object value) => value switch
        {
            DateTimeOffset dto => dto,
            // A Local DateTime carries the machine's offset; an Unspecified one is read as UTC. Passing both
            // to the (DateTime, TimeSpan.Zero) constructor would throw for a Local value off UTC.
            DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc), TimeSpan.Zero),
        };

        private static decimal ToDecimal(object value) => value switch
        {
            decimal d => d,
            string s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>What a save carries: no id creates, an id the site owns updates. The site is the request's.</summary>
    public sealed class FormatDefinitionRequest
    {
        public Guid? Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public FormatKind Kind { get; set; }

        public string? Pattern { get; set; }

        public string? Culture { get; set; }

        public string? TimeZone { get; set; }
    }

    /// <summary>The formats a site can render with: the system presets and the site's own definitions (a Shared port so a consumer needs no module reference).</summary>
    public interface IFormatService
    {
        Task<FormatDescriptor?> GetAsync(Guid id, CancellationToken ct = default);

        Task<IReadOnlyList<FormatDescriptor>> ListAsync(FormatKind? kind = null, string? culture = null, FormatScope? scope = null, CancellationToken ct = default);
    }

    /// <summary>A consumer that references formats by id names the references, so a definition in use is not deleted (no implementer until the pipelines' steps take formats).</summary>
    public interface IFormatUsageProvider
    {
        Task<IReadOnlyList<string>> FindReferencesAsync(Guid formatId, CancellationToken ct = default);
    }
}
