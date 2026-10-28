using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Models;

public class ScheduledStepOptions
{
    public string? CompletionCallback { get; set; }
    public object? Tag { get; set; }
    public ICollection<Variable>? Variables { get; set; }
    public string? ExistingStepInstanceId { get; set; }
    public bool PreventDuplicateScheduling { get; set; }
    public IDictionary<string,object>? Input { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this step.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }
}