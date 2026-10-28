#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Mcp.Domain
{
    /// <summary>
    /// Where an external MCP server is reached: an absolute http(s) URL (D101 item 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reshaped from the predecessor platform's <c>McpServerTransport</c>, a multi-field <c>ValueObject</c> the shared module context
    /// REFUSES (only a string value object maps by convention). Canonical <see cref="Value"/>: the URL itself.
    /// </para>
    /// <para>
    /// <b>There is no stdio transport, and that is a decision, not an omission.</b> the predecessor platform's second transport launched
    /// an administrator-supplied COMMAND on the host - and seeded a demo of it. It does not travel (D101 item 5), and
    /// this type has nowhere to put one: a value that is not an http(s) URL cannot be constructed, so it cannot be
    /// stored, so nothing downstream ever has to refuse it.
    /// </para>
    /// <para>
    /// This says the URL is WELL-FORMED. It says nothing about whether it is SAFE to dial - a name that resolves to a
    /// private address is a perfectly good URL. That is decided where the connection is made, against the address
    /// actually being connected to (the connect-time guard of a later increment); a check here would be the pre-flight
    /// DNS look-up D101 falsified as TOCTOU.
    /// </para>
    /// </remarks>
    public sealed class McpServerTransport : ValueObject, IStringValueObject
    {
        public const int MaxLength = 2048;

        private McpServerTransport(string endpoint) => Value = endpoint;

        /// <summary>The absolute http(s) endpoint.</summary>
        public string Value { get; }

        public Uri Endpoint() => new(Value, UriKind.Absolute);

        /// <summary>Parses the canonical form, re-running every rule - a stored value cannot bypass one.</summary>
        public static McpServerTransport Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLength
                || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"An MCP server endpoint is an absolute http(s) URL of at most {MaxLength} characters.", nameof(value));
            }

            // Credentials in a URL end up in logs, in the audit trail and on the admin page. The key has a place of its
            // own: the site secret store.
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("An MCP server endpoint carries no user name or password; the API key is stored separately.", nameof(value));
            }

            return new McpServerTransport(trimmed);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// The limits this site puts on ONE external server (D101 item 6). Canonical <see cref="Value"/>:
    /// <c>"rpm=60"</c>, or <c>"rpm=-"</c> for no cap.
    /// </summary>
    /// <remarks>
    /// Reshaped from a predecessor platform record. Its second field, <c>AllowSensitivePayloads</c>, is NOT here: it configured the
    /// egress/PII guard, which does not travel in Wave 1 (BL-97), and a flag nothing reads is a switch an operator can
    /// turn believing it does something. The canonical string is a <c>key=value</c> list precisely so that a later
    /// release can add one without a schema step.
    /// </remarks>
    public sealed class McpGuardrail : ValueObject, IStringValueObject
    {
        public const int MaxLength = 32;

        private McpGuardrail(int? maxRequestsPerMinute) => MaxRequestsPerMinute = maxRequestsPerMinute;

        public static McpGuardrail Default { get; } = new(null);

        /// <summary>Calls per minute to this server, or null for no cap.</summary>
        public int? MaxRequestsPerMinute { get; }

        public string Value => "rpm=" + (MaxRequestsPerMinute?.ToString(CultureInfo.InvariantCulture) ?? "-");

        public static McpGuardrail Of(int? maxRequestsPerMinute)
        {
            if (maxRequestsPerMinute is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRequestsPerMinute), "A request cap is positive when set.");
            }

            return new McpGuardrail(maxRequestsPerMinute);
        }

        public static McpGuardrail Create(string value)
        {
            const string prefix = "rpm=";
            var text = value ?? string.Empty;
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                var number = text.Substring(prefix.Length);
                if (number == "-")
                {
                    return Default;
                }

                if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var cap) && cap > 0)
                {
                    return new McpGuardrail(cap);
                }
            }

            throw new ArgumentException($"'{value}' is not a canonical guardrail (\"rpm=60\" or \"rpm=-\").", nameof(value));
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return MaxRequestsPerMinute;
        }
    }

    /// <summary>One tool an external server said it offers.</summary>
    public sealed record DiscoveredTool(string Name, string? Description, string? ParametersJsonSchema);

    /// <summary>
    /// The tools last discovered on an external server, as ONE value (D101 item 6). Canonical <see cref="Value"/>: a
    /// compact JSON array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reshaped from a predecessor platform collection property, which the shared context would have handed to EF's conventions
    /// inside a swallowed initializer. <see cref="Tools"/> is a METHOD: both document resolvers serialise read-only
    /// properties, so a computed property would be written into the document.
    /// </para>
    /// <para>
    /// <b>Everything in here was said by SOMEBODY ELSE'S SERVER.</b> So it is bounded before it is kept: at most
    /// <see cref="MaximumTools"/> tools, a name of at most <see cref="MaximumNameLength"/>, a description cut to
    /// <see cref="MaximumDescriptionLength"/>, and a schema over <see cref="MaximumSchemaLength"/> DROPPED rather than
    /// cut (half a JSON schema is not a schema). the predecessor platform kept whatever arrived; a server answering with a million tools,
    /// or one ten-megabyte schema, would have been written into the row and rendered on the admin page. A name that
    /// repeats, ignoring case, is refused outright - two tools one name is how a caller is pointed at the wrong one.
    /// </para>
    /// </remarks>
    public sealed class DiscoveredToolSet : ValueObject, IStringValueObject
    {
        public const int MaximumTools = 256;
        public const int MaximumNameLength = 128;
        public const int MaximumDescriptionLength = 1000;
        public const int MaximumSchemaLength = 16_384;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private readonly IReadOnlyList<DiscoveredTool> _tools;

        private DiscoveredToolSet(IReadOnlyList<DiscoveredTool> tools)
        {
            _tools = tools;
            Value = JsonSerializer.Serialize(tools, Json);
        }

        public static DiscoveredToolSet Empty { get; } = new(Array.Empty<DiscoveredTool>());

        public string Value { get; }

        /// <summary>A method, not a property - see the type remarks.</summary>
        public IReadOnlyList<DiscoveredTool> Tools() => _tools;

        public static DiscoveredToolSet Of(IEnumerable<DiscoveredTool> tools)
        {
            ArgumentNullException.ThrowIfNull(tools);

            var kept = new List<DiscoveredTool>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools)
            {
                if (tool is null)
                {
                    throw new ArgumentException("A discovered tool set cannot contain a null entry.", nameof(tools));
                }

                var name = (tool.Name ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > MaximumNameLength)
                {
                    throw new ArgumentException($"A discovered tool has a name of 1 to {MaximumNameLength} characters.", nameof(tools));
                }

                if (!names.Add(name))
                {
                    throw new ArgumentException($"The discovered tool name '{name}' occurs more than once.", nameof(tools));
                }

                if (kept.Count == MaximumTools)
                {
                    throw new ArgumentException($"An external server may offer at most {MaximumTools} tools.", nameof(tools));
                }

                var description = string.IsNullOrWhiteSpace(tool.Description) ? null : tool.Description.Trim();
                if (description is { Length: > MaximumDescriptionLength })
                {
                    description = description.Substring(0, MaximumDescriptionLength);
                }

                var schema = string.IsNullOrWhiteSpace(tool.ParametersJsonSchema) ? null : tool.ParametersJsonSchema.Trim();
                if (schema is { Length: > MaximumSchemaLength })
                {
                    schema = null;
                }

                kept.Add(new DiscoveredTool(name, description, schema));
            }

            return kept.Count == 0 ? Empty : new DiscoveredToolSet(kept);
        }

        /// <summary>Parses the canonical form THROUGH <see cref="Of"/>, so a stored value is held to the same bounds.</summary>
        public static DiscoveredToolSet Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Empty;
            }

            List<DiscoveredTool>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<DiscoveredTool>>(value, Json);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("The stored tool set is not valid JSON.", nameof(value), ex);
            }

            return Of(parsed ?? Enumerable.Empty<DiscoveredTool>());
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }
    }
}
