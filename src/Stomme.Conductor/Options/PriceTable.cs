using System.Globalization;

namespace Stomme.Conductor.Options;

/// <summary>The price of one model per million tokens (ADR 0008). Output includes thinking tokens.</summary>
/// <param name="Model">The model id as the adapter sends it (an alias such as jev-latest is matched too).</param>
/// <param name="InputPerMillion">US dollars per million input tokens that missed the provider's cache.</param>
/// <param name="CachedInputPerMillion">US dollars per million input tokens the provider served from its cache.</param>
/// <param name="OutputPerMillion">US dollars per million output tokens.</param>
/// <param name="Note">Where the price came from, or a caveat.</param>
public sealed record ModelPrice(
    string Model, decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion, string? Note = null)
{
    public decimal CostOf(long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var uncached = Math.Max(0, inputTokens - cachedInputTokens);
        return (uncached * InputPerMillion + cachedInputTokens * CachedInputPerMillion + outputTokens * OutputPerMillion)
            / 1_000_000m;
    }
}

/// <summary>Prices read on a date, kept as configuration because they go stale (ADR 0008).</summary>
public sealed class PriceTable
{
    public required DateOnly ReadOn { get; init; }

    public required IReadOnlyList<ModelPrice> Prices { get; init; }

    /// <summary>The prices in docs/research/2026-11-03-cost-estimate.md.</summary>
    public static PriceTable Default { get; } = new()
    {
        ReadOn = new DateOnly(2026, 11, 3),
        Prices =
        [
            new("mimo-v2.6-pro", 0.435m, 0.0036m, 0.87m, "mimo.mi.com pay-as-you-go; thinking billed as output"),
            new("mimo-v2.6-flash", 0.14m, 0.0028m, 0.28m, "mimo.mi.com pay-as-you-go"),
            new("jev-latest", 0.042m, 0.042m, 0m, "docs.typesafe.ai/models; output free"),
            new("jev-1.14.0", 0.042m, 0.042m, 0m, "docs.typesafe.ai/models; output free"),
            new("jev-1.13.0", 0.042m, 0.042m, 0m, "docs.typesafe.ai/models; output free"),
            new("claude-haiku-5-5", 0.10m, 0.01m, 0.50m, "third-party trackers, to verify; higher above 100k-token prompts"),
            new("claude-sonnet-5-5", 2.00m, 0.20m, 10.00m, "third-party trackers, to verify"),
            new("claude-opus-5-5", 4.00m, 0.40m, 20.00m, "third-party trackers, to verify"),
        ],
    };

    /// <summary>The price for a model, matched exactly, then by the model id starting with the priced name (so a dated id finds its family).</summary>
    public ModelPrice? Find(string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        return Prices.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase))
            ?? Prices.FirstOrDefault(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase));
    }

    public static string Dollars(decimal usd) => "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);
}
