#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using CoreFeatureEvaluator = Stomme.FeatureManagement.Application.IFeatureEvaluator;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Bridges the <see cref="IFeatureEvaluator"/> port (the one-method contract a
    /// <c>[Requires(Feature = "...")]</c> is evaluated through, kept in <c>Stomme.Security.Abstractions</c>
    /// so a gated context need not reference the Feature Management module) onto Stomme's own
    /// FeatureManagement evaluator, which carries the same <c>IsEnabledAsync(key, ct)</c> member.
    /// </summary>
    /// <remarks>
    /// FeatureManagement is opt-in, so its evaluator is resolved OPTIONALLY per call — and when it is absent
    /// the answer is FALSE. This bridge fails CLOSED: the spike's bridge answered <c>true</c> here,
    /// which turned every feature-gated request into an ungated one on any host without FeatureManagement
    /// (F11). A feature that cannot be evaluated is a feature that is off.
    /// </remarks>
    public sealed class FeatureEvaluatorBridge : IFeatureEvaluator
    {
        private readonly IServiceProvider _services;

        public FeatureEvaluatorBridge(IServiceProvider services)
        {
            _services = services;
        }

        public Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
        {
            var evaluator = _services.GetService<CoreFeatureEvaluator>();
            return evaluator is null ? Task.FromResult(false) : evaluator.IsEnabledAsync(key, ct);
        }
    }
}
