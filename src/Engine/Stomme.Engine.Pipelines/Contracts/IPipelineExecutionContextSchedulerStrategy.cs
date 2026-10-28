using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Defines a strategy interface for scheduling steps within the context of a pipeline execution.
/// </summary>
public interface IPipelineExecutionContextSchedulerStrategy
{
    /// <summary>
    /// Schedules an <see cref="StepWorkItem"/> in the context of the provided pipeline execution.
    /// </summary>
    /// <param name="context">The execution context of the pipeline in which the step is being scheduled.</param>
    /// <param name="stepNode">The node representing the step to be scheduled.</param>
    /// <param name="owner">The <see cref="StepExecutionContext"/> that owns the scheduled step.</param>
    /// <param name="options">Optional parameters for scheduling the work item.</param>
    /// <returns>The scheduled <see cref="StepWorkItem"/>.</returns>
    StepWorkItem Schedule(PipelineExecutionContext context,
        StepNode stepNode,
        StepExecutionContext owner,
        ScheduleWorkOptions? options = null);
}