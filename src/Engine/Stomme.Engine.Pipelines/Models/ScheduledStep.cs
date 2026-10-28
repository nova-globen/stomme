namespace Stomme.Engine.Pipelines.Models;

public class ScheduledStep
{
    public string? StepNodeId { get; set; }
    public string? OwnerStepInstanceId { get; set; }
    public ScheduledStepOptions? Options { get; set; }
}