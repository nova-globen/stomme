#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Lifted from the predecessor platform (BL-97 increment 8): the tier model and the router port are unchanged.
namespace Stomme.CostOptimization
{
    /// <summary>
    /// Complexity tier an AI request is classified into. Values start at 1 so the CLR default (0) is not
    /// a defined tier. Each tier maps to an admin-configured model, cheapest first.
    /// </summary>
    public enum ModelTier
    {
        /// <summary>Low-complexity request; routes to the cheapest configured model.</summary>
        Simple = 1,

        /// <summary>Moderate request; routes to the mid-range configured model.</summary>
        Medium = 2,

        /// <summary>High-complexity request; routes to the most capable configured model.</summary>
        Complex = 3,
    }

    /// <summary>
    /// Cheap, provider-neutral complexity signals for one AI request, as seen at the AI assistant (BL-97 increments 9-10)
    /// provider seam before the model is chosen.
    /// </summary>
    /// <param name="TokenEstimate">Estimated prompt token count.</param>
    /// <param name="ToolCount">Number of tools available to the request.</param>
    /// <param name="HasStructuredOutput">Whether the request demands schema-constrained output.</param>
    /// <param name="UserOverride">Explicit per-request tier choice; when set, it always wins over classification.</param>
    public sealed record ModelTierRequest(
        int TokenEstimate,
        int ToolCount,
        bool HasStructuredOutput,
        ModelTier? UserOverride = null);

    /// <summary>
    /// Outcome of routing one AI request: the resolved tier and, when the tier map is configured, the
    /// concrete model identifier to invoke. A null <paramref name="Model"/> means "tier resolved, no
    /// model mapped" — the caller falls back to its own default model.
    /// </summary>
    /// <param name="Tier">The complexity tier the request resolved to.</param>
    /// <param name="Model">The configured model identifier for the tier, if mapped.</param>
    public sealed record ModelTierDecision(ModelTier Tier, string? Model = null);

    /// <summary>
    /// Model auto-tiering port resolved per request by the AI assistant (BL-97 increments 9-10) provider seam: classifies
    /// the request's complexity from cheap signals and maps the tier to the cheapest appropriate model.
    /// Cost Optimization owns the routing policy; the assistant owns provider invocation.
    /// </summary>
    public interface IModelTierRouter
    {
        /// <summary>Routes one AI request to a model tier, honoring any per-request user override.</summary>
        Task<ModelTierDecision> RouteAsync(ModelTierRequest request, CancellationToken ct = default);
    }
}
