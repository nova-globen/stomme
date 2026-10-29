#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;

// Maps the provider's native cost payload onto the Unified Cost Schema.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// SDK-free GCP Cloud Billing adapter: maps the BigQuery billing-export row shape (rows with
    /// <c>service.description</c>, <c>cost</c>, <c>currency</c>, <c>usage_start_time</c>, <c>project.id</c>,
    /// optional <c>resource.name</c>, and <c>labels[]</c> key/value pairs) to Unified Cost Schema entries
    /// through the domain normalizer. Labels flow through tag extraction; <c>project.id</c> is surfaced as a
    /// <c>project</c> tag when the labels do not already carry one. Structurally invalid payloads raise
    /// <see cref="CloudCostPayloadFormatException"/>; rows without a usable service, cost, currency, or
    /// usage start time are skipped.
    /// </summary>
    public sealed class GcpCostProvider : ICloudCostProvider
    {
        private readonly ICloudCostPayloadSource _payloadSource;
        private readonly ICloudCostNormalizer _normalizer;

        public GcpCostProvider(ICloudCostPayloadSource payloadSource, ICloudCostNormalizer normalizer)
        {
            ArgumentNullException.ThrowIfNull(payloadSource);
            ArgumentNullException.ThrowIfNull(normalizer);
            _payloadSource = payloadSource;
            _normalizer = normalizer;
        }

        public CloudProvider Provider => CloudProvider.Gcp;

        public async Task<IReadOnlyList<CloudCostEntry>> FetchCostsAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var payload = await _payloadSource.ReadAsync(query, ct).ConfigureAwait(false);
            return MapPayload(payload);
        }

        private IReadOnlyList<CloudCostEntry> MapPayload(string payload)
        {
            using var document = CloudCostPayloadJson.Parse(payload, CloudProvider.Gcp);
            var root = document.RootElement;
            var rows = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("rows", out var rowsProperty) &&
                    rowsProperty.ValueKind == JsonValueKind.Array => rowsProperty,
                _ => throw new CloudCostPayloadFormatException(
                    "GCP cost payload does not carry the billing-export row shape (a 'rows' array)."),
            };

            var entries = new List<CloudCostEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    GetNestedString(row, "service", "description") is not { } service ||
                    !row.TryGetProperty("cost", out var costElement) ||
                    !CloudCostPayloadJson.TryGetDecimal(costElement, out var cost) ||
                    !row.TryGetProperty("currency", out var currencyElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(currencyElement) is not { } currency ||
                    !TryGetUsageDate(row, out var date))
                {
                    continue;
                }

                var resource = GetNestedString(row, "resource", "name") ?? string.Empty;
                var tags = ReadLabels(row);
                if (GetNestedString(row, "project", "id") is { } projectId)
                {
                    tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    tags.TryAdd("project", projectId);
                }

                entries.Add(_normalizer.Normalize(new RawCloudCost(date, CloudProvider.Gcp, service, resource, cost, currency, tags)));
            }

            return entries;
        }

        private static bool TryGetUsageDate(JsonElement row, out DateOnly date)
        {
            date = default;
            if (!row.TryGetProperty("usage_start_time", out var start) ||
                CloudCostPayloadJson.GetNonWhiteSpaceString(start) is not { } text ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return false;
            }

            date = DateOnly.FromDateTime(timestamp.UtcDateTime);
            return true;
        }

        private static string? GetNestedString(JsonElement row, string objectName, string propertyName) =>
            row.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object &&
            nested.TryGetProperty(propertyName, out var property)
                ? CloudCostPayloadJson.GetNonWhiteSpaceString(property)
                : null;

        private static Dictionary<string, string>? ReadLabels(JsonElement row)
        {
            if (!row.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            Dictionary<string, string>? tags = null;
            foreach (var label in labels.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.Object ||
                    !label.TryGetProperty("key", out var keyElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(keyElement) is not { } key ||
                    !label.TryGetProperty("value", out var valueElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(valueElement) is not { } value)
                {
                    continue;
                }

                (tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = value;
            }

            return tags;
        }
    }
}
