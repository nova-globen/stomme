using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A request to validate a pipeline definition.
/// </summary>
/// <param name="Pipeline">The pipeline materialized from the definition.</param>
/// <param name="ValidationErrors">The collection of validation errors.</param>
public record PipelineDefinitionValidating(Pipeline Pipeline, ICollection<PipelineValidationError> ValidationErrors) : INotification;