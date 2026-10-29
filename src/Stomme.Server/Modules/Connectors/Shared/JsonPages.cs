#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>One Microsoft Graph collection page: the <c>value</c> array as records (top-level properties typed; nested objects and arrays kept as raw JSON text) and <c>@odata.nextLink</c> for paging. Shared by Intune and Entra ID.</summary>
    public static class GraphPage
    {
        /// <summary>
        /// The page read from <paramref name="current"/>, its next link followed only while it stays on the same collection -
        /// its scheme, host and path - and names another page, as Graph's own links do: anything else is refused
        /// before the token is sent there.
        /// </summary>
        public static (List<ConnectorRecord> Records, string? NextLink) Read(string payload, string current)
        {
            var (records, nextLink) = Read(payload);
            if (nextLink is not null && !(Stomme.Shared.Utilities.TryCreateAbsoluteUri(nextLink, out var next) && Stomme.Shared.Utilities.TryCreateAbsoluteUri(current, out var page)
                && next.Scheme == page.Scheme && string.Equals(next.Authority, page.Authority, StringComparison.OrdinalIgnoreCase)
                && string.Equals(next.AbsolutePath, page.AbsolutePath, StringComparison.OrdinalIgnoreCase) && nextLink != current))
            {
                throw new InvalidOperationException("Microsoft Graph named a next page outside the collection - it is not followed.");
            }

            return (records, nextLink);
        }

        private static (List<ConnectorRecord> Records, string? NextLink) Read(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The Graph response carries no 'value' array.");
            }

            var records = new List<ConnectorRecord>();
            foreach (var element in rows.EnumerateArray())
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        fields[property.Name] = JsonRecords.ToClrValue(property.Value);
                    }
                }
                else
                {
                    fields["value"] = JsonRecords.ToClrValue(element);
                }

                records.Add(new ConnectorRecord(fields));
            }

            string? nextLink = null;
            if (root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String)
            {
                nextLink = next.GetString();
            }

            return (records, nextLink);
        }
    }

    /// <summary>
    /// One JSON row flattened with the legacy bridges' column semantics: nested objects flatten recursively to
    /// <c>parent_child</c> columns, scalar arrays join to a comma-separated string, arrays of objects stay as raw JSON text.
    /// Shared by Taegis, Cylance and Flexera One so the three share one copy.
    /// </summary>
    public static class FlattenedRows
    {
        /// <param name="skipProperty">A property name dropped at every level (Rapid7's <c>links</c>).</param>
        /// <param name="collapseReferences">Collapse a <c>{display_value, value, link}</c> reference object to one <c>{name}_value</c> column (ServiceNow's Table API).</param>
        public static ConnectorRecord Flatten(JsonElement row, string? skipProperty = null, bool collapseReferences = false)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row.ValueKind == JsonValueKind.Object)
            {
                Walk(row, prefix: null, fields, skipProperty, collapseReferences);
            }
            else
            {
                fields["value"] = JsonRecords.ToClrValue(row);
            }

            return new ConnectorRecord(fields);
        }

        private static void Walk(JsonElement element, string? prefix, Dictionary<string, object?> fields, string? skipProperty, bool collapseReferences)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (skipProperty is not null && property.NameEquals(skipProperty))
                {
                    continue;
                }

                var name = prefix is null ? property.Name : $"{prefix}_{property.Name}";
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object when collapseReferences && TryCollapseReference(property.Value, out var collapsed):
                        fields[$"{name}_value"] = collapsed;
                        break;
                    case JsonValueKind.Object:
                        Walk(property.Value, name, fields, skipProperty, collapseReferences);
                        break;
                    case JsonValueKind.Array:
                        fields[name] = FlattenArray(property.Value);
                        break;
                    default:
                        fields[name] = JsonRecords.ToClrValue(property.Value);
                        break;
                }
            }
        }

        private static bool TryCollapseReference(JsonElement element, out string? collapsed)
        {
            if (element.TryGetProperty("display_value", out var display) && display.ValueKind == JsonValueKind.String)
            {
                collapsed = display.GetString();
                return true;
            }

            if (element.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                collapsed = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                return true;
            }

            collapsed = null;
            return false;
        }

        private static object? FlattenArray(JsonElement array)
        {
            var elements = array.EnumerateArray().ToList();
            if (elements.All(e => e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                return string.Join(", ", elements.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
            }

            return array.GetRawText();
        }
    }

    /// <summary>The authenticated JSON client of an API connector: the egress's client, the credential on every request, JSON accepted; a GET through the family's throttling policy.</summary>
    public static class BearerClient
    {
        public static HttpClient Create(IConnectorEgress egress, TimeSpan timeout, string token, bool acceptJson = false) =>
            Create(egress, timeout, new AuthenticationHeaderValue("Bearer", token), acceptJson);

        public static HttpClient Create(IConnectorEgress egress, TimeSpan timeout, AuthenticationHeaderValue authorization, bool acceptJson = false)
        {
            ArgumentNullException.ThrowIfNull(egress);
            var client = egress.CreateHttpClient(timeout);
            client.DefaultRequestHeaders.Authorization = authorization;
            if (acceptJson)
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            return client;
        }

        /// <summary>The Basic scheme's <c>username</c> and <c>password</c> as an Authorization header; a missing field fails naming it and the vendor, before any call.</summary>
        public static AuthenticationHeaderValue Basic(IReadOnlyDictionary<string, string> credentials, string vendor)
        {
            ArgumentNullException.ThrowIfNull(credentials);
            var username = Values.Get(credentials, "username") ?? throw new InvalidOperationException($"The {vendor} connection is missing 'username' - check the connection's credentials.");
            var password = credentials.TryGetValue("password", out var secret) && !string.IsNullOrEmpty(secret) ? secret : throw new InvalidOperationException($"The {vendor} connection is missing 'password' - check the connection's credentials.");
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));
        }

        /// <summary>GETs <paramref name="url"/> through the retry policy; the body on success, <paramref name="failure"/>'s message for the status otherwise.</summary>
        public static async Task<string> GetAsync(HttpClient client, string url, Func<System.Net.HttpStatusCode, string> failure, CancellationToken cancellationToken)
        {
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri(url)), cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(failure(response.StatusCode));
            }

            return payload;
        }
    }

    /// <summary>The setting and parameter readers the family's API connectors share.</summary>
    public static class Values
    {
        public static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        public static string GetOrDefault(IReadOnlyDictionary<string, string> values, string key, string fallback) => Get(values, key) ?? fallback;

        public static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue) =>
            Get(values, key) is { } value ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) : defaultValue;

        /// <summary>The endpoint as an absolute http(s) base URL without a trailing slash; <paramref name="fallback"/> when empty.</summary>
        public static string BaseUrl(string endpoint, string fallback)
        {
            var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint.Trim().TrimEnd('/');
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(baseUrl, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"The endpoint '{endpoint}' is not an absolute http(s) URL.");
            }

            return baseUrl;
        }
    }
}
