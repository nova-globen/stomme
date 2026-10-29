#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// The request shaping every HTTP connector of the family shares (lifted from the predecessor platform's REST
    /// connector): header lines, the content-type routing, the JSON records reader. None of it opens a connection - the
    /// client is always the one the host's egress hands the connector, which is what <c>ConnectorEgressOnlyTests</c> pins.
    /// </summary>
    public static class HeaderLines
    {
        /// <summary>The connection's <c>headers</c> setting overlaid by the operation's <c>headers</c> parameter (same name, case-insensitively: the operation wins).</summary>
        public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string>? operationParameters)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings.TryGetValue("headers", out var connectionHeaders))
            {
                Parse(connectionHeaders, "headers setting", headers);
            }

            if (operationParameters is not null && operationParameters.TryGetValue("headers", out var operationHeaders))
            {
                Parse(operationHeaders, "headers parameter", headers);
            }

            return headers;
        }

        /// <summary>One header per line as <c>Name: value</c>; blank lines are ignored; a line without a name fails naming the line.</summary>
        public static void Parse(string? text, string source, Dictionary<string, string> into)
        {
            ArgumentNullException.ThrowIfNull(into);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                var name = separator < 0 ? string.Empty : line[..separator].Trim();
                if (name.Length == 0)
                {
                    throw new InvalidOperationException($"Invalid header line '{line}' in the {source}: expected 'Name: value'.");
                }

                into[name] = line[(separator + 1)..].Trim();
            }
        }

        /// <summary>Applies the resolved headers: Content-Type describes the body so it goes on the content (and is dropped without one); other content-scoped names are routed the same way.</summary>
        public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(headers);
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null)
                    {
                        request.Content.Headers.ContentType = MediaTypes.Parse(value, "Content-Type header");
                    }

                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }
    }

    public static class MediaTypes
    {
        public static MediaTypeHeaderValue Parse(string value, string source)
        {
            try
            {
                return MediaTypeHeaderValue.Parse(value);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"The {source} value '{value}' is not a valid media type.", ex);
            }
        }
    }

    /// <summary>JSON only: a top-level array, a named <c>records_path</c> property, or a conventional <c>values</c>/<c>value</c>/<c>data</c> node; each element becomes a record.</summary>
    public static class JsonRecords
    {
        public static List<ConnectorRecord> Parse(string payload, IReadOnlyDictionary<string, string> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            JsonElement rows;
            if (parameters.TryGetValue("records_path", out var recordsPath) && !string.IsNullOrWhiteSpace(recordsPath) &&
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(recordsPath, out var atPath))
            {
                rows = atPath;
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                rows = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     (root.TryGetProperty("values", out var conventional) || root.TryGetProperty("value", out conventional) || root.TryGetProperty("data", out conventional)))
            {
                rows = conventional;
            }
            else
            {
                throw new InvalidOperationException("Response JSON is neither an array nor an object with a records array.");
            }

            if (rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The resolved records node is not a JSON array.");
            }

            var records = new List<ConnectorRecord>();
            foreach (var element in rows.EnumerateArray())
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        fields[property.Name] = ToClrValue(property.Value);
                    }
                }
                else
                {
                    fields["value"] = ToClrValue(element);
                }

                records.Add(new ConnectorRecord(fields));
            }

            return records;
        }

        public static object? ToClrValue(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            // The integer branch is cast to object so the ternary's common type stays object: long/double would widen every integer to double.
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }
}
