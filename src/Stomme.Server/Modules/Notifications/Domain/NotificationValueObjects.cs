#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stomme.Infrastructure.Security;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    // The eight notification value objects, lifted from the predecessor platform NotificationValueObjects.cs (BL-78) and reshaped per
    // D100 point 7: each is an IStringValueObject with a canonical Value and a public static Create(string), so EF
    // (a plain string column, the D79 convention), RavenDB and the File store all persist the SAME scalar and
    // rebuild through the SAME factory - no per-provider converter, no fragile private-constructor binding. The
    // public constructors the predecessor platform exposed are kept (the domain API is unchanged); Create(string) parses the
    // canonical form and then runs the same constructor, so a persisted value cannot bypass an invariant.

    /// <summary>
    /// A delivery target: a site user (by int id), a literal e-mail address, or a literal endpoint.
    /// <para>
    /// Canonical <see cref="Value"/>: <c>"{Kind}:{Address}"</c> - <c>"User:42"</c>, <c>"Email:ops@example.com"</c>.
    /// The kind is split at the FIRST colon, so an address may itself contain colons. the predecessor platform's <c>Value</c> (the
    /// bare address) is <see cref="Address"/> here, because <c>Value</c> is the persistence contract.
    /// </para>
    /// </summary>
    public sealed class Recipient : ValueObject, IStringValueObject
    {
        public Recipient(RecipientKind kind, string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("A recipient address is required.", nameof(address));
            }

            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), $"Unknown recipient kind '{kind}'.");
            }

            var trimmed = address.Trim();
            if (kind == RecipientKind.User
                && (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0))
            {
                // Stomme users are int-keyed (BL-78 F10); the predecessor platform addressed a user by Guid.
                throw new ArgumentException("A user recipient must be a positive int user id.", nameof(address));
            }

            Kind = kind;
            Address = trimmed;
        }

        public RecipientKind Kind { get; }

        /// <summary>The address, interpreted per <see cref="Kind"/> (a user id, an e-mail address, or an endpoint).</summary>
        public string Address { get; }

        /// <summary>The canonical persisted form, <c>"{Kind}:{Address}"</c>.</summary>
        public string Value => $"{Kind}:{Address}";

        public static Recipient Create(string value)
        {
            var separator = (value ?? string.Empty).IndexOf(':');
            if (separator <= 0 || !Enum.TryParse<RecipientKind>(value![..separator], ignoreCase: false, out var kind)
                || !Enum.IsDefined(kind) || int.TryParse(value[..separator], out _))
            {
                throw new ArgumentException($"'{value}' is not a canonical recipient (\"Kind:address\").", nameof(value));
            }

            return new Recipient(kind, value[(separator + 1)..]);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Kind;
            yield return Address;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// The rendered content actually delivered for one attempt: a subject (may be empty) and a body.
    /// Canonical <see cref="Value"/>: compact JSON <c>{"subject":"...","body":"..."}</c> (members in that order,
    /// System.Text.Json default escaping).
    /// </summary>
    public sealed class Payload : ValueObject, IStringValueObject
    {
        public Payload(string subject, string body)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new ArgumentException("A payload body is required.", nameof(body));
            }

            Subject = subject;
            Body = body;
        }

        public string Subject { get; }

        public string Body { get; }

        public string Value => CanonicalJson.SubjectAndBody(Subject, Body);

        public static Payload Create(string value)
        {
            var (subject, body) = CanonicalJson.ReadSubjectAndBody(value, nameof(value));
            return new Payload(subject, body);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Subject;
            yield return Body;
        }
    }

    /// <summary>A single non-secret configuration entry within <see cref="TransportSettings.Options"/>.</summary>
    public sealed record TransportOption(string Key, string Value);

    /// <summary>
    /// Kind-specific transport configuration for a <see cref="NotificationChannelAggregate"/>.
    /// <para>
    /// <b>No inline secret (kept from the predecessor platform).</b> An option key that looks like a secret is rejected and the type
    /// carries no field for a raw credential. A credential lives in the shared per-site secret store (D101,
    /// <c>ISiteSecretStore</c>); the transport stores only its REFERENCE KEY (<see cref="SecretReference"/>, e.g.
    /// <c>notifications.channel.{id:N}.credential</c>), which must be a well-formed reference key - so a pasted token
    /// or a <c>vault://</c> URI in that slot is rejected too.
    /// </para>
    /// <para>
    /// <b>Relaxed (D100 point 7).</b> the predecessor platform REQUIRED a vault reference for Email and SMS, which a credential-free
    /// e-mail channel (the existing SMTP queue) cannot supply. The requirement now belongs to the channel PROVIDER
    /// that declares it (webhook optional, e-mail none); the domain requires a reference for no kind. Webhook still
    /// requires an absolute http(s) endpoint.
    /// </para>
    /// <para>
    /// Canonical <see cref="Value"/>: compact JSON
    /// <c>{"kind":"Webhook","endpoint":"https://...","secretReference":"notifications.channel.x.credential","options":[{"key":"k","value":"v"}]}</c>
    /// - members in that order, <c>endpoint</c>/<c>secretReference</c> omitted when null, <c>options</c> always present.
    /// </para>
    /// </summary>
    public sealed class TransportSettings : ValueObject, IStringValueObject
    {
        /// <summary>Option-key fragments that denote a secret and must therefore go through the secret store, not inline.</summary>
        private static readonly string[] SecretLikeKeyFragments =
            ["password", "secret", "apikey", "api-key", "api_key", "token", "credential"];

        public TransportSettings(ChannelKind kind, string? endpoint, string? secretReference, IReadOnlyList<TransportOption> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), $"Unknown channel kind '{kind}'.");
            }

            foreach (var option in options)
            {
                ArgumentNullException.ThrowIfNull(option, nameof(options));
                if (string.IsNullOrWhiteSpace(option.Key))
                {
                    throw new ArgumentException("A transport option needs a key.", nameof(options));
                }

                foreach (var fragment in SecretLikeKeyFragments)
                {
                    if (option.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            $"Transport option '{option.Key}' looks like a secret; store it in the site secret store and keep only its reference key.",
                            nameof(options));
                    }
                }
            }

            var reference = string.IsNullOrWhiteSpace(secretReference) ? null : secretReference.Trim();
            if (reference is not null && !SiteSecretStore.IsValidReferenceKey(reference))
            {
                throw new ArgumentException(
                    "The secret reference must be a site secret store reference key (lower-case dotted segments), never the secret itself.",
                    nameof(secretReference));
            }

            if (kind == ChannelKind.Webhook
                && (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                throw new ArgumentException("A webhook channel requires an absolute http(s) endpoint URL.", nameof(endpoint));
            }

            Kind = kind;
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
            SecretReference = reference;
            Options = options.Select(o => new TransportOption(o.Key.Trim(), o.Value)).ToList();
        }

        public ChannelKind Kind { get; }

        /// <summary>The transport endpoint (webhook URL, SMTP host) - kind-specific; may be null.</summary>
        public string? Endpoint { get; }

        /// <summary>An <c>ISiteSecretStore</c> reference key for this channel's credential. Never a raw secret value.</summary>
        public string? SecretReference { get; }

        /// <summary>Non-secret kind-specific options.</summary>
        public IReadOnlyList<TransportOption> Options { get; }

        public string Value
        {
            get
            {
                return CanonicalJson.Write(writer =>
                {
                    writer.WriteString("kind", Kind.ToString());
                    if (Endpoint is not null)
                    {
                        writer.WriteString("endpoint", Endpoint);
                    }

                    if (SecretReference is not null)
                    {
                        writer.WriteString("secretReference", SecretReference);
                    }

                    writer.WriteStartArray("options");
                    foreach (var option in Options)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("key", option.Key);
                        writer.WriteString("value", option.Value);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
            }
        }

        public static TransportSettings Create(string value)
        {
            try
            {
                using var document = JsonDocument.Parse(value ?? string.Empty);
                var root = document.RootElement;
                if (!Enum.TryParse<ChannelKind>(root.GetProperty("kind").GetString(), ignoreCase: false, out var kind))
                {
                    throw new FormatException("Unknown kind.");
                }

                var options = root.GetProperty("options").EnumerateArray()
                    .Select(option => new TransportOption(
                        option.GetProperty("key").GetString()!,
                        option.TryGetProperty("value", out var optionValue) ? optionValue.GetString()! : null!))
                    .ToList();

                return new TransportSettings(
                    kind,
                    root.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() : null,
                    root.TryGetProperty("secretReference", out var reference) ? reference.GetString() : null,
                    options);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new ArgumentException($"'{value}' is not canonical transport settings JSON.", nameof(value), ex);
            }
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Kind;
            yield return Endpoint;
            yield return SecretReference;
            foreach (var option in Options)
            {
                yield return option.Key;
                yield return option.Value;
            }
        }
    }

    /// <summary>
    /// An optional throughput cap on a <see cref="NotificationChannelAggregate"/> (max sends per rolling window).
    /// Canonical <see cref="Value"/>: <c>"{max}/{window:c}"</c> - <c>"10/00:05:00"</c>, <c>"100/1.00:00:00"</c>.
    /// </summary>
    public sealed class RateLimit : ValueObject, IStringValueObject
    {
        /// <summary>int.MaxValue (10) + '/' + TimeSpan.MaxValue in the "c" format (25).</summary>
        public const int MaxLength = 36;

        public RateLimit(int maxPerWindow, TimeSpan window)
        {
            if (maxPerWindow <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPerWindow), "The rate limit must allow at least one send.");
            }

            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window), "The rate-limit window must be positive.");
            }

            MaxPerWindow = maxPerWindow;
            Window = window;
        }

        public int MaxPerWindow { get; }

        public TimeSpan Window { get; }

        public string Value =>
            $"{MaxPerWindow.ToString(CultureInfo.InvariantCulture)}/{Window.ToString("c", CultureInfo.InvariantCulture)}";

        public static RateLimit Create(string value)
        {
            var parts = (value ?? string.Empty).Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
                || !TimeSpan.TryParseExact(parts[1], "c", CultureInfo.InvariantCulture, out var window))
            {
                throw new ArgumentException($"'{value}' is not a canonical rate limit (\"max/d.hh:mm:ss\").", nameof(value));
            }

            return new RateLimit(max, window);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return MaxPerWindow;
            yield return Window;
        }
    }

    /// <summary>
    /// A daily window during which a user defers non-critical notifications, in the user's local time via a fixed
    /// <see cref="UtcOffset"/>; it may wrap midnight (22:00-07:00). A fixed offset (not an IANA zone) keeps evaluation
    /// deterministic; DST-aware zones are a later refinement (the booked quiet-hours residue).
    /// <para>
    /// Canonical <see cref="Value"/>: <c>"HH:mm-HH:mm{+|-}hh:mm"</c> - <c>"22:00-07:00+02:00"</c>. Minute precision
    /// is therefore an invariant the string form adds (the predecessor platform accepted seconds). Instants are UTC
    /// <see cref="DateTime"/>s (the predecessor platform took <c>DateTimeOffset</c>).
    /// </para>
    /// </summary>
    public sealed class QuietHours : ValueObject, IStringValueObject
    {
        /// <summary><c>"23:59-00:00-14:00"</c>.</summary>
        public const int MaxLength = 17;

        private static readonly Regex CanonicalPattern = new(
            "^([0-9]{2}):([0-9]{2})-([0-9]{2}):([0-9]{2})([+-])([0-9]{2}):([0-9]{2})$",
            RegexOptions.CultureInvariant);

        public QuietHours(TimeOnly start, TimeOnly end, TimeSpan utcOffset)
        {
            if (start == end)
            {
                throw new ArgumentException("Quiet hours must span a non-empty window (start and end cannot be equal).", nameof(end));
            }

            if (utcOffset < TimeSpan.FromHours(-14) || utcOffset > TimeSpan.FromHours(14))
            {
                throw new ArgumentOutOfRangeException(nameof(utcOffset), "The UTC offset must be within +/-14 hours.");
            }

            if (start.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("Quiet hours are minute-precision.", nameof(start));
            }

            if (end.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("Quiet hours are minute-precision.", nameof(end));
            }

            if (utcOffset.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("The UTC offset is minute-precision.", nameof(utcOffset));
            }

            Start = start;
            End = end;
            UtcOffset = utcOffset;
        }

        /// <summary>The local time the quiet window opens (inclusive).</summary>
        public TimeOnly Start { get; }

        /// <summary>The local time the quiet window closes (exclusive).</summary>
        public TimeOnly End { get; }

        /// <summary>The recipient's fixed offset from UTC.</summary>
        public TimeSpan UtcOffset { get; }

        public string Value =>
            Start.ToString("HH\\:mm", CultureInfo.InvariantCulture) + "-" +
            End.ToString("HH\\:mm", CultureInfo.InvariantCulture) +
            (UtcOffset < TimeSpan.Zero ? "-" : "+") +
            UtcOffset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);

        public static QuietHours Create(string value)
        {
            var match = CanonicalPattern.Match(value ?? string.Empty);
            if (!match.Success)
            {
                throw new ArgumentException($"'{value}' is not canonical quiet hours (\"HH:mm-HH:mm+hh:mm\").", nameof(value));
            }

            int Part(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
            try
            {
                var offset = new TimeSpan(Part(6), Part(7), 0);
                return new QuietHours(
                    new TimeOnly(Part(1), Part(2)),
                    new TimeOnly(Part(3), Part(4)),
                    match.Groups[5].Value == "-" ? offset.Negate() : offset);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new ArgumentException($"'{value}' is not canonical quiet hours.", nameof(value), ex);
            }
        }

        /// <summary>True when the UTC <paramref name="instantUtc"/> falls inside the quiet window in the user's local time.</summary>
        public bool IsQuietAt(DateTime instantUtc)
        {
            var localTime = TimeOnly.FromDateTime(UtcInstant.Require(instantUtc, nameof(instantUtc)).Add(UtcOffset));
            return Start <= End
                ? localTime >= Start && localTime < End
                : localTime >= Start || localTime < End; // window wraps past midnight
        }

        /// <summary>
        /// The UTC instant at which the quiet window containing <paramref name="instantUtc"/> next closes; the instant
        /// unchanged when it is not within a quiet window (no deferral needed).
        /// </summary>
        public DateTime NextEnd(DateTime instantUtc)
        {
            if (!IsQuietAt(instantUtc))
            {
                return instantUtc;
            }

            var local = instantUtc.Add(UtcOffset);
            var localTime = TimeOnly.FromDateTime(local);

            // For a wrapping window the close is on the next day only in the pre-midnight segment (localTime >= Start).
            var closesNextDay = Start > End && localTime >= Start;
            var date = closesNextDay ? local.Date.AddDays(1) : local.Date;
            return DateTime.SpecifyKind(date.Add(End.ToTimeSpan()).Subtract(UtcOffset), DateTimeKind.Utc);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Start;
            yield return End;
            yield return UtcOffset;
        }
    }

    /// <summary>
    /// A BCP-47 locale tag (<c>en</c>, <c>en-US</c>) keying a template's localized <see cref="Content"/>.
    /// Canonical <see cref="Value"/>: the trimmed tag as written (<c>"en-US"</c>); equality is case-insensitive.
    /// </summary>
    public sealed class Locale : ValueObject, IStringValueObject
    {
        /// <summary>The BCP-47 recommended maximum tag length.</summary>
        public const int MaxLength = 35;

        private static readonly Regex TagPattern = new("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant);

        public Locale(string code)
        {
            var trimmed = (code ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLength || !TagPattern.IsMatch(trimmed))
            {
                throw new ArgumentException($"A locale is a BCP-47 tag of 1..{MaxLength} letters, digits and hyphens.", nameof(code));
            }

            Code = trimmed;
        }

        public string Code { get; }

        public string Value => Code;

        public static Locale Create(string value) => new(value);

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Code.ToLowerInvariant();
        }

        public override string ToString() => Code;
    }

    /// <summary>
    /// A template's subject + body for one locale (Liquid source, rendered at send time).
    /// Canonical <see cref="Value"/>: compact JSON <c>{"subject":"...","body":"..."}</c> (as <see cref="Payload"/>).
    /// </summary>
    public sealed class Content : ValueObject, IStringValueObject
    {
        public Content(string subject, string body)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new ArgumentException("A content body is required.", nameof(body));
            }

            Subject = subject;
            Body = body;
        }

        /// <summary>The subject line (empty for channels without one).</summary>
        public string Subject { get; }

        /// <summary>The message body (Liquid template source).</summary>
        public string Body { get; }

        public string Value => CanonicalJson.SubjectAndBody(Subject, Body);

        public static Content Create(string value)
        {
            var (subject, body) = CanonicalJson.ReadSubjectAndBody(value, nameof(value));
            return new Content(subject, body);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Subject;
            yield return Body;
        }
    }

    /// <summary>One locale's <see cref="Content"/> within a <see cref="NotificationTemplateAggregate"/> (the persisted list element).</summary>
    public sealed record LocalizedContent(Locale Locale, Content Content);

    /// <summary>
    /// The set of placeholder names a <see cref="NotificationTemplateAggregate"/> declares (the renderer rejects an
    /// undeclared <c>{{ root }}</c> at save time).
    /// Canonical <see cref="Value"/>: the trimmed names joined by commas in declaration order - <c>"a,b,c"</c>; the
    /// empty schema is <c>""</c>. A name therefore cannot contain a comma (an invariant the string form adds).
    /// </summary>
    public sealed class PlaceholderSchema : ValueObject, IStringValueObject
    {
        public PlaceholderSchema(IReadOnlyList<string> placeholders)
        {
            ArgumentNullException.ThrowIfNull(placeholders);

            var normalized = new List<string>();
            foreach (var name in placeholders)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("A placeholder name is required.", nameof(placeholders));
                }

                var trimmed = name.Trim();
                if (trimmed.Contains(','))
                {
                    throw new ArgumentException($"The placeholder '{trimmed}' contains a comma.", nameof(placeholders));
                }

                if (normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"The placeholder '{trimmed}' is declared more than once.", nameof(placeholders));
                }

                normalized.Add(trimmed);
            }

            Placeholders = normalized;
        }

        public IReadOnlyList<string> Placeholders { get; }

        public string Value => string.Join(",", Placeholders);

        public static PlaceholderSchema Create(string value) =>
            new(string.IsNullOrEmpty(value) ? [] : value.Split(','));

        /// <summary>True when <paramref name="name"/> is a declared placeholder (case-insensitive).</summary>
        public bool Declares(string name) =>
            Placeholders.Contains(name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            foreach (var name in Placeholders)
            {
                yield return name.ToLowerInvariant();
            }
        }
    }

    /// <summary>The compact-JSON canonical forms shared by <see cref="Content"/>, <see cref="Payload"/> and <see cref="TransportSettings"/>.</summary>
    internal static class CanonicalJson
    {
        public static string Write(Action<Utf8JsonWriter> members)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                members(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static string SubjectAndBody(string subject, string body) => Write(writer =>
        {
            writer.WriteString("subject", subject);
            writer.WriteString("body", body);
        });

        public static (string Subject, string Body) ReadSubjectAndBody(string value, string parameterName)
        {
            try
            {
                using var document = JsonDocument.Parse(value ?? string.Empty);
                return (document.RootElement.GetProperty("subject").GetString()!, document.RootElement.GetProperty("body").GetString()!);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new ArgumentException($"'{value}' is not canonical subject/body JSON.", parameterName, ex);
            }
        }
    }
}
