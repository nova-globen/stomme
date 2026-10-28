#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Metering;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Prices an AI call from a configured price list - server-side, never from the caller (D102).
    /// </summary>
    /// <remarks>
    /// The cost is the one field a producer must not supply: a caller that names its own price can under-report a
    /// bill. Prices come from <c>Metering:AiPricing</c>, keyed by the normalized provider and model, in units of
    /// currency per million tokens. An unpriced model costs zero and says so through the returned currency, which
    /// keeps an unknown model from silently inventing a number.
    /// </remarks>
    public sealed class AiCostCalculator
    {
        private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

        public AiCostCalculator(IConfiguration? configuration)
        {
            var prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
            var section = configuration?.GetSection("Metering:AiPricing");
            if (section is not null)
            {
                foreach (var entry in section.GetChildren())
                {
                    if (IsPrice(entry))
                    {
                        // A bare model: "gpt-x": { ... } - the fallback when no provider-qualified price matches.
                        prices[MetricKeys.Normalize(entry.Key)] = Read(entry);
                        continue;
                    }

                    // A PROVIDER: "openai": { "gpt-x": { ... } }. Configuration uses ':' as its path separator, so the
                    // documented "provider:model" key, written in JSON as "openai:gpt-x", ARRIVES as exactly this nested
                    // shape - and used to be read as a model called "openai" with no numbers in it. MEASURED (BL-163): the
                    // call was priced 0 USD, silently - an under-reported bill. Both spellings now mean the same thing.
                    foreach (var model in entry.GetChildren())
                    {
                        if (IsPrice(model))
                        {
                            prices[MetricKeys.Normalize(entry.Key) + ":" + MetricKeys.Normalize(model.Key)] = Read(model);
                        }
                    }
                }
            }

            _prices = prices;
        }

        /// <summary>A section is a PRICE when it carries at least one of the two rates; otherwise it is a provider.</summary>
        private static bool IsPrice(IConfigurationSection section) =>
            section.GetSection("InputPerMillion").Value is not null || section.GetSection("OutputPerMillion").Value is not null;

        private static ModelPrice Read(IConfigurationSection section) => new(
            section.GetValue<decimal?>("InputPerMillion") ?? 0m,
            section.GetValue<decimal?>("OutputPerMillion") ?? 0m,
            (section.GetValue<string>("Currency") ?? DefaultCurrency).ToUpperInvariant());

        /// <summary>The currency assumed when a price list names none.</summary>
        public const string DefaultCurrency = "USD";

        /// <summary>Prices one call. The key is "provider:model", normalized.</summary>
        public (decimal Cost, string CurrencyCode) Price(string provider, string model, long inputTokens, long outputTokens)
        {
            var key = MetricKeys.Normalize(provider) + ":" + MetricKeys.Normalize(model);
            if (!_prices.TryGetValue(key, out var price) && !_prices.TryGetValue(MetricKeys.Normalize(model), out price))
            {
                return (0m, DefaultCurrency);
            }

            var cost = (inputTokens / 1_000_000m * price.InputPerMillion) + (outputTokens / 1_000_000m * price.OutputPerMillion);
            return (decimal.Round(cost, 6, MidpointRounding.AwayFromZero), price.Currency);
        }

        private sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, string Currency);
    }
}
