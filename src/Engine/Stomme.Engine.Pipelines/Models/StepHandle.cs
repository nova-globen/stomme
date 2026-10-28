namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a handle to a step.
/// </summary>
public class StepHandle
{
    public static StepHandle FromStepId(string stepId) => new()
    {
        StepId = stepId
    };

    public static StepHandle FromStepNodeId(string stepNodeId) => new()
    {
        StepNodeId = stepNodeId
    };

    public static StepHandle FromStepInstanceId(string stepInstanceId) => new()
    {
        StepInstanceId = stepInstanceId
    };

    public static StepHandle FromStepHash(string stepHash) => new()
    {
        StepHash = stepHash
    };

    public string? StepId { get; init; }
    public string? StepNodeId { get; init; }
    public string? StepInstanceId { get; init; }
    public string? StepHash { get; init; }

    public override string ToString()
    {
        return StepId ?? (StepNodeId ?? (StepInstanceId ?? (StepHash ?? "")));
    }
}