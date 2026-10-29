#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Microsoft.Extensions.Configuration;

// Lifted from the predecessor platform: the classification is unchanged. CHANGED: the options are read from the
// HOST's configuration section CostOptimization:ModelTiers, not bound through IOptions.
namespace Stomme.Modules.CostOptimization.Application
{
    /// <summary>
    /// The rule-based <see cref="IModelTierRouter"/> (spec §"Model Auto-Tiering"): classifies each request's
    /// complexity from cheap, deterministic signals — prompt-token estimate, available tool count, and whether
    /// the request demands schema-constrained output — then maps the tier to the admin-configured model from
    /// <see cref="ModelTiersOptions"/> (<c>CostOptimization:ModelTiers</c>). Rules, cheapest tier first:
    /// a per-request <see cref="ModelTierRequest.UserOverride"/> always wins over classification; otherwise
    /// Complex when the token estimate reaches <see cref="ModelTiersOptions.ComplexMinTokens"/> or the tool
    /// count reaches <see cref="ModelTiersOptions.ComplexMinToolCount"/>; otherwise at least Medium when the
    /// request demands structured output or exceeds <see cref="ModelTiersOptions.SimpleMaxTokens"/>; otherwise
    /// Simple. A tier with no configured model resolves with a <c>null</c> model, so the caller keeps its own
    /// default — leaving the map empty makes the router a transparent no-op.
    /// </summary>
    public sealed class RuleBasedModelTierRouter : IModelTierRouter
    {
        private readonly ModelTiersOptions _options;

        /// <summary>Creates the router over the bound <see cref="ModelTiersOptions"/>.</summary>
        public RuleBasedModelTierRouter(ModelTiersOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options;
        }

        /// <inheritdoc />
        public Task<ModelTierDecision> RouteAsync(ModelTierRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var options = _options;
            var tier = request.UserOverride ?? Classify(request, options);
            var model = tier switch
            {
                ModelTier.Simple => options.Simple,
                ModelTier.Medium => options.Medium,
                ModelTier.Complex => options.Complex,
                _ => null,
            };

            return Task.FromResult(new ModelTierDecision(tier, string.IsNullOrWhiteSpace(model) ? null : model));
        }

        private static ModelTier Classify(ModelTierRequest request, ModelTiersOptions options)
        {
            if (request.TokenEstimate >= options.ComplexMinTokens || request.ToolCount >= options.ComplexMinToolCount)
            {
                return ModelTier.Complex;
            }

            // Schema-constrained output needs at least the mid-range model, as does a prompt beyond the Simple budget.
            if (request.HasStructuredOutput || request.TokenEstimate > options.SimpleMaxTokens)
            {
                return ModelTier.Medium;
            }

            return ModelTier.Simple;
        }
    }
}
