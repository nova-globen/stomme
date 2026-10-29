#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Stomme.Formats;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Formats.Domain
{
    /// <summary>
    /// A site's own display format: a name, a kind, a pattern the kind can render under a culture, and an
    /// optional time zone for dates. Tenant-owned and site-scoped, named with a normalised twin so uniqueness is
    /// asked of the store on every provider the same way. The parameterless constructor is materialisation only.
    /// </summary>
    public sealed class FormatDefinitionAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxNameLength = 120;

        private FormatDefinitionAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            Pattern = FormatPattern.None;
            CultureName = CultureName.Invariant;
            TimeZoneId = TimeZoneId.None;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Name { get; private set; }

        public string NameNormalized { get; private set; }

        public FormatKind Kind { get; private set; }

        public FormatPattern Pattern { get; private set; }

        public CultureName CultureName { get; private set; }

        public TimeZoneId TimeZoneId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime ModifiedUtc { get; private set; }

        public static string NormalizeName(string name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>The only way to create a definition: the invariants hold or it throws, and the saved event is raised.</summary>
        public static FormatDefinitionAggregate Create(Guid id, TenantId tenantId, int siteId, string name, FormatKind kind, string? pattern, string? culture, string? timeZone, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A format definition belongs to a site.");
            }

            var (cleanName, cultureName, formatPattern, timeZoneId) = Validate(name, kind, pattern, culture, timeZone);
            var definition = new FormatDefinitionAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Name = cleanName,
                NameNormalized = NormalizeName(cleanName),
                Kind = kind,
                Pattern = formatPattern,
                CultureName = cultureName,
                TimeZoneId = timeZoneId,
                CreatedUtc = nowUtc,
                ModifiedUtc = nowUtc,
            };
            definition.RaiseDomainEvent(new FormatDefinitionSavedEvent(id, cleanName));
            return definition;
        }

        public void Update(string name, FormatKind kind, string? pattern, string? culture, string? timeZone, DateTime nowUtc)
        {
            var (cleanName, cultureName, formatPattern, timeZoneId) = Validate(name, kind, pattern, culture, timeZone);
            Name = cleanName;
            NameNormalized = NormalizeName(cleanName);
            Kind = kind;
            Pattern = formatPattern;
            CultureName = cultureName;
            TimeZoneId = timeZoneId;
            ModifiedUtc = nowUtc;
            RaiseDomainEvent(new FormatDefinitionSavedEvent(Id, cleanName));
        }

        public void MarkDeleted() => RaiseDomainEvent(new FormatDefinitionDeletedEvent(Id));

        public FormatDescriptor ToDescriptor() => new(Id, Name, Kind, Pattern.Value, CultureName.Value, FormatScope.Tenant, TimeZoneId.Value);

        private static (string Name, CultureName Culture, FormatPattern Pattern, TimeZoneId TimeZone) Validate(string name, FormatKind kind, string? pattern, string? culture, string? timeZone)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var cleanName = name.Trim();
            if (cleanName.Length > MaxNameLength)
            {
                throw new ArgumentException($"A format name must be at most {MaxNameLength} characters.", nameof(name));
            }

            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            var cultureName = CultureName.Of(culture);
            var formatPattern = FormatPattern.For(kind, pattern, cultureName);
            var timeZoneId = TimeZoneId.Of(timeZone);
            return (cleanName, cultureName, formatPattern, timeZoneId);
        }
    }

    /// <summary>
    /// The pattern a kind renders with. Capped at <see cref="MaxLength"/> (an unbounded pattern is refused), and proven renderable
    /// under the culture at creation so a stored pattern never throws later. Empty for the Locale kind.
    /// </summary>
    public sealed class FormatPattern : ValueObject, IStringValueObject
    {
        public const int MaxLength = 200;

        public static readonly FormatPattern None = new(string.Empty);

        private FormatPattern(string value) => Value = value;

        public string Value { get; }

        /// <summary>Rehydration from a stored value (the IStringValueObject convention on EF and RavenDB): validated when it was made, taken as it is.</summary>
        public static FormatPattern Create(string value) => new(value ?? string.Empty);

        public static FormatPattern For(FormatKind kind, string? value, CultureName culture)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (kind == FormatKind.Locale)
            {
                return None; // the culture drives rendering
            }

            if (trimmed.Length == 0)
            {
                throw new ArgumentException($"A {kind} format requires a non-empty pattern.", nameof(value));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"A format pattern must be at most {MaxLength} characters.", nameof(value));
            }

            EnsureRenderable(kind, trimmed, culture.Resolve());
            return new FormatPattern(trimmed);
        }

        private static void EnsureRenderable(FormatKind kind, string pattern, CultureInfo culture)
        {
            try
            {
                _ = kind switch
                {
                    FormatKind.Date => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToString(pattern, culture),
                    FormatKind.Number or FormatKind.Currency => 1234.5m.ToString(pattern, culture),
                    _ => string.Empty,
                };
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"'{pattern}' is not a valid {kind} pattern for culture '{culture.Name}': {ex.Message}", nameof(pattern));
            }
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>A .NET culture name validated at creation (predefined cultures only); empty is the invariant culture.</summary>
    public sealed class CultureName : ValueObject, IStringValueObject
    {
        public static readonly CultureName Invariant = new(string.Empty);

        private CultureName(string value) => Value = value;

        public string Value { get; }

        /// <summary>Rehydration from a stored value (the IStringValueObject convention): validated when it was made, taken as it is.</summary>
        public static CultureName Create(string value) => new(value ?? string.Empty);

        public static CultureName Of(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Invariant;
            }

            var trimmed = name.Trim();
            try
            {
                return new CultureName(CultureInfo.GetCultureInfo(trimmed, predefinedOnly: true).Name);
            }
            catch (CultureNotFoundException)
            {
                throw new ArgumentException($"'{trimmed}' is not a recognised .NET culture.", nameof(name));
            }
        }

        public CultureInfo Resolve() => Value.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(Value);

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value.Length == 0 ? "(invariant)" : Value;
    }

    /// <summary>A system time-zone id validated at creation; empty means "render as supplied".</summary>
    public sealed class TimeZoneId : ValueObject, IStringValueObject
    {
        public static readonly TimeZoneId None = new(string.Empty);

        private TimeZoneId(string value) => Value = value;

        public string Value { get; }

        /// <summary>Rehydration from a stored value (the IStringValueObject convention): validated when it was made, taken as it is.</summary>
        public static TimeZoneId Create(string value) => new(value ?? string.Empty);

        public static TimeZoneId Of(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return None;
            }

            var trimmed = id.Trim();
            try
            {
                return new TimeZoneId(TimeZoneInfo.FindSystemTimeZoneById(trimmed).Id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ArgumentException($"'{trimmed}' is not a recognised time zone.", nameof(id));
            }
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value.Length == 0 ? "(none)" : Value;
    }

    public sealed record FormatDefinitionSavedEvent(Guid FormatId, string Name) : DomainEvent;

    public sealed record FormatDefinitionDeletedEvent(Guid FormatId) : DomainEvent;
}
