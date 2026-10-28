using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Defines a strategy for scheduling steps in a pipeline execution context.
/// </summary>
public interface IStepExecutionContextSchedulerStrategy
{
    /// <summary>
    /// Schedules a step for execution based on the specified context, step, owner, and scheduling options.
    /// </summary>
    /// <param name="context">The execution context of the step.</param>
    /// <param name="step">The step to be scheduled. Can be null if no specific step is targeted.</param>
    /// <param name="owner">The context of the owning step, if applicable. Can be null.</param>
    /// <param name="options">Optional scheduling options for the step.</param>
    Task ScheduleStepAsync(
        StepExecutionContext context,
        IStep? step,
        StepExecutionContext? owner,
        ScheduleWorkOptions? options = null);

    /// <summary>
    /// Schedules a step node for execution within the specified step execution context.
    /// </summary>
    /// <param name="context">The execution context in which the step node will be scheduled.</param>
    /// <param name="stepNode">The step node to be scheduled. Can be null if no specific node is targeted.</param>
    /// <param name="owner">The context of the owning step execution, if applicable. Can be null.</param>
    /// <param name="options">Optional settings for scheduling the step node.</param>
    Task ScheduleStepAsync(
        StepExecutionContext context,
        StepNode? stepNode,
        StepExecutionContext? owner = null,
        ScheduleWorkOptions? options = null);
}