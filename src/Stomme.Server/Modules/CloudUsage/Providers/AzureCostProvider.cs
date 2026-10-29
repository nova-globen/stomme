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
    /// SDK-free Azure Cost Management adapter: maps the query-result payload shape
    /// (<c>properties.columns[]</c> naming the cells of <c>properties.rows[][]</c> — PreTaxCost, UsageDate,
    /// ServiceName, ResourceId, Currency) to Unified Cost Schema entries through the domain normalizer.
    /// Columns outside that schema are treated as tag columns (department/project/environment extraction).
    /// Structurally invalid payloads raise <see cref="CloudCostPayloadFormatException"/>; rows without a
    /// usable cost, date, service, or currency cell are skipped.
    /// </summary>
    public sealed class AzureCostProvider : ICloudCostProvider
    {
        private readonly ICloudCostPayloadSource _payloadSource;
        private readonly ICloudCostNormalizer _normalizer;

        public AzureCostProvider(ICloudCostPayloadSource payloadSource, ICloudCostNormalizer normalizer)
        {
            ArgumentNullException.ThrowIfNull(payloadSource);
            ArgumentNullException.ThrowIfNull(normalizer);
            _payloadSource = payloadSource;
            _normalizer = normalizer;
        }

        public CloudProvider Provider => CloudProvider.Azure;

        public async Task<IReadOnlyList<CloudCostEntry>> FetchCostsAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var payload = await _payloadSource.ReadAsync(query, ct).ConfigureAwait(false);
            return MapPayload(payload);
        }

        private IReadOnlyList<CloudCostEntry> MapPayload(string payload)
        {
            using var document = CloudCostPayloadJson.Parse(payload, CloudProvider.Azure);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array ||
                !properties.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new CloudCostPayloadFormatException(
                    "Azure cost payload does not carry the Cost Management query-result shape ('properties.columns' + 'properties.rows').");
            }

            var columnNames = ReadColumnNames(columns);
            int IndexOf(params string[] candidates) =>
                columnNames.FindIndex(name => candidates.Contains(name, StringComparer.OrdinalIgnoreCase));

            var costIndex = IndexOf("PreTaxCost", "Cost");
            var dateIndex = IndexOf("UsageDate");
            var serviceIndex = IndexOf("ServiceName");
            var currencyIndex = IndexOf("Currency");
            var resourceIndex = IndexOf("ResourceId");
            if (costIndex < 0 || dateIndex < 0 || serviceIndex < 0 || currencyIndex < 0)
            {
                throw new CloudCostPayloadFormatException(
                    "Azure cost payload is missing a required column (PreTaxCost/Cost, UsageDate, ServiceName, Currency).");
            }

            var schemaIndexes = new HashSet<int> { costIndex, dateIndex, serviceIndex, currencyIndex, resourceIndex };
            var entries = new List<CloudCostEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columnNames.Count)
                {
                    continue;
                }

                if (!CloudCostPayloadJson.TryGetDecimal(row[costIndex], out var cost) ||
                    !TryGetUsageDate(row[dateIndex], out var date) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(row[serviceIndex]) is not { } service ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(row[currencyIndex]) is not { } currency)
                {
                    continue;
                }

                var resource = resourceIndex >= 0
                    ? CloudCostPayloadJson.GetNonWhiteSpaceString(row[resourceIndex]) ?? string.Empty
                    : string.Empty;

                Dictionary<string, string>? tags = null;
                for (var i = 0; i < columnNames.Count; i++)
                {
                    if (schemaIndexes.Contains(i) || CloudCostPayloadJson.GetNonWhiteSpaceString(row[i]) is not { } tagValue)
                    {
                        continue;
                    }

                    (tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[columnNames[i]] = tagValue;
                }

                entries.Add(_normalizer.Normalize(new RawCloudCost(date, CloudProvider.Azure, service, resource, cost, currency, tags)));
            }

            return entries;
        }

        private static List<string> ReadColumnNames(JsonElement columns)
        {
            var names = new List<string>();
            foreach (var column in columns.EnumerateArray())
            {
                if (column.ValueKind != JsonValueKind.Object ||
                    !column.TryGetProperty("name", out var name) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(name) is not { } columnName)
                {
                    throw new CloudCostPayloadFormatException("Azure cost payload contains a column without a name.");
                }

                names.Add(columnName);
            }

            return names;
        }

        private static bool TryGetUsageDate(JsonElement element, out DateOnly date)
        {
            date = default;
            var text = element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt64(out var numeric) => numeric.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => element.GetString()?.Trim(),
                _ => null,
            };
            return text is not null &&
                DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}
