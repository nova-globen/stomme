#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

// Lifted from the predecessor platform: the classification is unchanged. CHANGED: the options are read from the
// HOST's configuration section CostOptimization:ModelTiers, not bound through IOptions.
namespace Stomme.Modules.CostOptimization.Application
{
    /// <summary>
    /// Admin configuration for model auto-tiering, bound from <c>CostOptimization:ModelTiers</c> in the
    /// host's configuration. It carries the
    /// tier→model map — which models the <see cref="RuleBasedModelTierRouter"/> routes Simple/Medium/Complex
    /// requests to — and the classifier thresholds. An unset tier means "no model mapped": the router returns
    /// a <c>null</c> model and the caller keeps its own default. All thresholds have sensible defaults and are
    /// individually overridable.
    /// </summary>
    public sealed class ModelTiersOptions
    {
        /// <summary>Configuration section that binds these options.</summary>
        public const string SectionName = "CostOptimization:ModelTiers";

        /// <summary>Model id for <see cref="Abstractions.ModelTier.Simple"/> requests (the cheapest configured model). Unset → caller default.</summary>
        public string? Simple { get; set; }

        /// <summary>Model id for <see cref="Abstractions.ModelTier.Medium"/> requests (the mid-range configured model). Unset → caller default.</summary>
        public string? Medium { get; set; }

        /// <summary>Model id for <see cref="Abstractions.ModelTier.Complex"/> requests (the most capable configured model). Unset → caller default.</summary>
        public string? Complex { get; set; }

        /// <summary>Requests at or below this prompt-token estimate classify as Simple (unless another rule raises the tier). Default 500.</summary>
        public int SimpleMaxTokens { get; set; } = 500;

        /// <summary>Requests at or above this prompt-token estimate classify as Complex. Default 4000.</summary>
        public int ComplexMinTokens { get; set; } = 4000;

        /// <summary>Requests with at least this many tools available classify as Complex. Default 5.</summary>
        public int ComplexMinToolCount { get; set; } = 5;

        /// <summary>Reads the options from the HOST's configuration; defaults when the section is absent.</summary>
        public static ModelTiersOptions From(IConfiguration? configuration)
        {
            var options = new ModelTiersOptions();
            configuration?.GetSection(SectionName).Bind(options);
            return options;
        }
    }
}
