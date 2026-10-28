using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Validates a pipeline definition.
/// </summary>
public interface IPipelineValidator
{
    /// <summary>
    /// Validates a pipeline.
    /// </summary>
    /// <param name="pipeline">The pipeline to validate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of validation errors.</returns>
    Task<IEnumerable<PipelineValidationError>> ValidateAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
}