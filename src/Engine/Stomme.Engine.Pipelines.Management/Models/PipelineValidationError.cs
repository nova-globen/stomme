namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents a validation error.
/// </summary>
/// <param name="Message">The error message.</param>
/// <param name="StepId">The Id of the step that caused the error, if any.</param>
public record PipelineValidationError(string Message, string? StepId = null);