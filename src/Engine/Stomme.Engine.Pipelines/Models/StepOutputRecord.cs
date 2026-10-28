namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a record of a step's output.
/// </summary>
/// <param name="ContainerId">The ID of the container that contains the step.</param>
/// <param name="StepId">The ID of the step.</param>
/// <param name="StepInstanceId">The ID of the step instance.</param>
/// <param name="OutputName">The name of the output.</param>
/// <param name="Value">The output value.</param>
public record StepOutputRecord(string ContainerId, string StepId, string StepInstanceId, string OutputName, object? Value);