namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents the outputs of a step.
/// </summary>
/// <param name="StepId">The step ID.</param>
/// <param name="StepName">The step name.</param>
/// <param name="OutputNames">The output names.</param>
public record StepOutputs(string StepId, string StepName, ICollection<string> OutputNames);