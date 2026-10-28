using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A pipeline activation validator controls whether new instances are allowed to be created given certain conditions.
/// </summary>
public interface IPipelineActivationStrategy
{
    /// <summary>
    /// Returns true if a new pipeline instance should be created, false otherwise.
    /// </summary>
    ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context);
}

/// <summary>
/// Provides context about the request for allowing the creation of a new pipeline instance. 
/// </summary>
public record PipelineInstantiationStrategyContext(Pipeline Pipeline, string? CorrelationId, CancellationToken CancellationToken);