using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Testing.Shared;

/// <summary>
/// Bypasses validation and schedules the step immediately.
/// </summary>
public class FakePipelineExecutionContextSchedulerStrategy : IPipelineExecutionContextSchedulerStrategy
{
    /// <summary>
    /// Schedules a step within the pipeline execution context.
    /// </summary>
    /// <param name="context">The pipeline execution context within which the step will be scheduled.</param>
    /// <param name="stepNode">The step node representing the step to be scheduled.</param>
    /// <param name="owner">The execution context owning the step execution.</param>
    /// <param name="options">Optional scheduling options for configuring the behavior of the scheduling process.</param>
    /// <returns>Returns an <see cref="StepWorkItem"/> representing the scheduled step work item.</returns>
    public StepWorkItem Schedule(PipelineExecutionContext context, StepNode stepNode, StepExecutionContext owner, ScheduleWorkOptions? options = null)
    {
        var workItem = new StepWorkItem(stepNode.Step, owner);
        context.Scheduler.Schedule(workItem);
        return workItem;
    }
}