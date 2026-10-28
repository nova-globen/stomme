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

// Lifted from the predecessor platform (BL-97 increment 3): the payload mapping is unchanged; only the namespaces moved.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// SDK-free AWS Cost Explorer adapter: maps the <c>GetCostAndUsage</c> payload shape
    /// (<c>ResultsByTime[]</c> with <c>TimePeriod.Start</c>, and <c>Groups[]</c> carrying <c>Keys[]</c> —
    /// first key the service, further <c>key$value</c> keys resource tags — plus
    /// <c>Metrics.UnblendedCost.Amount/Unit</c>) to Unified Cost Schema entries through the domain
    /// normalizer. Cost Explorer reports at service granularity, so the resource identifier is empty.
    /// Structurally invalid payloads raise <see cref="CloudCostPayloadFormatException"/>; time blocks and
    /// groups without a usable date, service, cost, or currency are skipped.
    /// </summary>
    public sealed class AwsCostProvider : ICloudCostProvider
    {
        private readonly ICloudCostPayloadSource _payloadSource;
        private readonly ICloudCostNormalizer _normalizer;

        public AwsCostProvider(ICloudCostPayloadSource payloadSource, ICloudCostNormalizer normalizer)
        {
            ArgumentNullException.ThrowIfNull(payloadSource);
            ArgumentNullException.ThrowIfNull(normalizer);
            _payloadSource = payloadSource;
            _normalizer = normalizer;
        }

        public CloudProvider Provider => CloudProvider.Aws;

        public async Task<IReadOnlyList<CloudCostEntry>> FetchCostsAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var payload = await _payloadSource.ReadAsync(query, ct).ConfigureAwait(false);
            return MapPayload(payload);
        }

        private IReadOnlyList<CloudCostEntry> MapPayload(string payload)
        {
            using var document = CloudCostPayloadJson.Parse(payload, CloudProvider.Aws);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ResultsByTime", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                throw new CloudCostPayloadFormatException(
                    "AWS cost payload does not carry the Cost Explorer GetCostAndUsage shape (a 'ResultsByTime' array).");
            }

            var entries = new List<CloudCostEntry>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object ||
                    !TryGetStartDate(result, out var date) ||
                    !result.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object ||
                        !group.TryGetProperty("Keys", out var keys) || keys.ValueKind != JsonValueKind.Array ||
                        keys.GetArrayLength() == 0 ||
                        CloudCostPayloadJson.GetNonWhiteSpaceString(keys[0]) is not { } service ||
                        !TryGetUnblendedCost(group, out var cost, out var currency))
                    {
                        continue;
                    }

                    entries.Add(_normalizer.Normalize(new RawCloudCost(
                        date, CloudProvider.Aws, service, string.Empty, cost, currency, ReadTagKeys(keys))));
                }
            }

            return entries;
        }

        private static bool TryGetStartDate(JsonElement result, out DateOnly date)
        {
            date = default;
            return result.TryGetProperty("TimePeriod", out var period) && period.ValueKind == JsonValueKind.Object &&
                period.TryGetProperty("Start", out var start) &&
                CloudCostPayloadJson.GetNonWhiteSpaceString(start) is { } text &&
                DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static bool TryGetUnblendedCost(JsonElement group, out decimal cost, out string currency)
        {
            cost = 0m;
            currency = string.Empty;
            if (!group.TryGetProperty("Metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object ||
                !metrics.TryGetProperty("UnblendedCost", out var unblended) || unblended.ValueKind != JsonValueKind.Object ||
                !unblended.TryGetProperty("Amount", out var amount) || !CloudCostPayloadJson.TryGetDecimal(amount, out cost) ||
                !unblended.TryGetProperty("Unit", out var unit) || CloudCostPayloadJson.GetNonWhiteSpaceString(unit) is not { } unitText)
            {
                return false;
            }

            currency = unitText;
            return true;
        }

        /// <summary>Reads the additional <c>key$value</c> tag group keys Cost Explorer emits when grouped by tag.</summary>
        private static Dictionary<string, string>? ReadTagKeys(JsonElement keys)
        {
            Dictionary<string, string>? tags = null;
            for (var i = 1; i < keys.GetArrayLength(); i++)
            {
                if (CloudCostPayloadJson.GetNonWhiteSpaceString(keys[i]) is not { } key)
                {
                    continue;
                }

                var separator = key.IndexOf('$', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var value = key[(separator + 1)..].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                (tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key[..separator].Trim()] = value;
            }

            return tags;
        }
    }
}
