using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Features;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for configuring commit strategies on <see cref="PipelinesFeature"/>.
/// </summary>
public static class PipelinesFeatureCommitStateExtensions
{
    /// <summary>
    /// Configures commit strategies for pipelines.
    /// </summary>
    /// <param name="pipelinesFeature">The pipelines feature.</param>
    /// <param name="configure">An optional configuration delegate for the commit strategies feature.</param>
    /// <returns>The pipelines feature for chaining.</returns>
    public static PipelinesFeature UseCommitStrategies(this PipelinesFeature pipelinesFeature, Action<CommitStrategiesFeature>? configure = null)
    {
        pipelinesFeature.Module.Use(configure);
        return pipelinesFeature;
    }

    /// <summary>
    /// Sets the specified pipeline commit strategy as the global default for all pipelines that do not specify their own strategy.
    /// The strategy will not be added to the registry and serves only as a fallback.
    /// </summary>
    /// <param name="pipelinesFeature">The pipelines feature.</param>
    /// <param name="strategy">The pipeline commit strategy instance to use as the default.</param>
    /// <returns>The pipelines feature for chaining.</returns>
    public static PipelinesFeature WithDefaultPipelineCommitStrategy(this PipelinesFeature pipelinesFeature, IPipelineCommitStrategy strategy)
    {
        pipelinesFeature.Module.Use<CommitStrategiesFeature>(feature => feature.SetDefaultPipelineCommitStrategy(strategy));
        return pipelinesFeature;
    }

    /// <summary>
    /// Sets the specified step commit strategy as the global default for all steps that do not specify their own strategy.
    /// The strategy will not be added to the registry and serves only as a fallback.
    /// </summary>
    /// <param name="pipelinesFeature">The pipelines feature.</param>
    /// <param name="strategy">The step commit strategy instance to use as the default.</param>
    /// <returns>The pipelines feature for chaining.</returns>
    public static PipelinesFeature WithDefaultStepCommitStrategy(this PipelinesFeature pipelinesFeature, IStepCommitStrategy strategy)
    {
        pipelinesFeature.Module.Use<CommitStrategiesFeature>(feature => feature.SetDefaultStepCommitStrategy(strategy));
        return pipelinesFeature;
    }
}