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

// Lifted from the predecessor platform: the payload mapping is unchanged; only the namespaces moved.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// SaaS and generic-REST extension path behind the same <see cref="ICloudCostProvider"/> contract: fronts
    /// a SaaS billing source (Datadog, Snowflake, GitHub, Microsoft 365, Salesforce) or any additional REST
    /// source (<see cref="CloudProvider.GenericRest"/>) whose payload is a JSON array of generic cost rows —
    /// <c>date</c> (yyyy-MM-dd), <c>service</c>, <c>cost</c>, <c>currency</c>, optional <c>resource</c>, and
    /// an optional <c>tags</c> object. The infrastructure clouds have dedicated native-payload adapters and
    /// are rejected here. <c>AddCloudUsage</c> registers one instance per SaaS source (plus the generic-REST
    /// fallback) over the connector-backed payload source, so the seeded SaaS pipelines resolve them by provider.
    /// Structurally invalid payloads raise <see cref="CloudCostPayloadFormatException"/>; rows without a
    /// usable date, service, cost, or currency are skipped.
    /// </summary>
    public sealed class SaasCostProvider : ICloudCostProvider
    {
        private static readonly CloudProvider[] NativeAdapterProviders = [CloudProvider.Azure, CloudProvider.Aws, CloudProvider.Gcp];

        private readonly ICloudCostPayloadSource _payloadSource;
        private readonly ICloudCostNormalizer _normalizer;

        public SaasCostProvider(CloudProvider provider, ICloudCostPayloadSource payloadSource, ICloudCostNormalizer normalizer)
        {
            if (!Enum.IsDefined(provider) || NativeAdapterProviders.Contains(provider))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "SaasCostProvider fronts SaaS or generic-REST cost sources; the infrastructure clouds use their dedicated adapters.");
            }

            ArgumentNullException.ThrowIfNull(payloadSource);
            ArgumentNullException.ThrowIfNull(normalizer);
            Provider = provider;
            _payloadSource = payloadSource;
            _normalizer = normalizer;
        }

        public CloudProvider Provider { get; }

        public async Task<IReadOnlyList<CloudCostEntry>> FetchCostsAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var payload = await _payloadSource.ReadAsync(query, ct).ConfigureAwait(false);
            return MapPayload(payload);
        }

        private IReadOnlyList<CloudCostEntry> MapPayload(string payload)
        {
            using var document = CloudCostPayloadJson.Parse(payload, Provider);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new CloudCostPayloadFormatException(
                    $"{Provider} cost payload does not carry the generic cost-row shape (a JSON array of rows).");
            }

            var entries = new List<CloudCostEntry>();
            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("date", out var dateElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(dateElement) is not { } dateText ||
                    !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !row.TryGetProperty("service", out var serviceElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(serviceElement) is not { } service ||
                    !row.TryGetProperty("cost", out var costElement) ||
                    !CloudCostPayloadJson.TryGetDecimal(costElement, out var cost) ||
                    !row.TryGetProperty("currency", out var currencyElement) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(currencyElement) is not { } currency)
                {
                    continue;
                }

                var resource = row.TryGetProperty("resource", out var resourceElement)
                    ? CloudCostPayloadJson.GetNonWhiteSpaceString(resourceElement) ?? string.Empty
                    : string.Empty;

                entries.Add(_normalizer.Normalize(new RawCloudCost(date, Provider, service, resource, cost, currency, ReadTags(row))));
            }

            return entries;
        }

        private static Dictionary<string, string>? ReadTags(JsonElement row)
        {
            if (!row.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Dictionary<string, string>? tags = null;
            foreach (var property in tagsElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    CloudCostPayloadJson.GetNonWhiteSpaceString(property.Value) is not { } value)
                {
                    continue;
                }

                (tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[property.Name.Trim()] = value;
            }

            return tags;
        }
    }
}
