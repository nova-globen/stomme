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

// JSON reading helpers shared by the cost providers.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>Shared defensive-parsing helpers for the native billing-payload adapters.</summary>
    internal static class CloudCostPayloadJson
    {
        /// <summary>Parses <paramref name="payload"/>, translating empty/non-JSON input into the adapter format exception.</summary>
        public static JsonDocument Parse(string payload, CloudProvider provider)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                throw new CloudCostPayloadFormatException($"{provider} cost payload is empty.");
            }

            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException exception)
            {
                throw new CloudCostPayloadFormatException($"{provider} cost payload is not valid JSON.", exception);
            }
        }

        /// <summary>Reads a decimal from a JSON number or an invariant-culture numeric string.</summary>
        public static bool TryGetDecimal(JsonElement element, out decimal value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0m;
            return false;
        }

        /// <summary>Returns the trimmed string value, or null when the element is not a non-whitespace string.</summary>
        public static string? GetNonWhiteSpaceString(JsonElement element) =>
            element.ValueKind == JsonValueKind.String && element.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;
    }
}
