using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Testing.Shared;

/// <summary>
/// Provides a fake implementation of the <see cref="IStepExecutionContextSchedulerStrategy"/> interface,
/// designed for testing purposes. This class allows scheduling of steps within a step execution context
/// and enables control over pipelines in a simulated environment.
/// </summary>
public class FakeStepExecutionContextSchedulerStrategy : IStepExecutionContextSchedulerStrategy
{
    /// <summary>
    /// Schedules the provided step for execution within the given step execution context.
    /// </summary>
    /// <param name="context">The current step execution context in which to schedule the step.</param>
    /// <param name="step">The step to be scheduled. Can be null, in which case no operation is performed.</param>
    /// <param name="owner">The owner step execution context, if any.</param>
    /// <param name="options">Optional scheduling options to influence how the step is scheduled.</param>
    public Task ScheduleStepAsync(StepExecutionContext context, IStep? step, StepExecutionContext? owner, ScheduleWorkOptions? options = null)
    {
        if (step == null)
            return Task.CompletedTask;
        
        var stepNode = new StepNode(step!, "");
        return ScheduleStepAsync(context, stepNode, owner, options);
    }

    /// <summary>
    /// Schedules the provided step for execution within the specified step execution context using the given scheduling strategy.
    /// </summary>
    /// <param name="context">The step execution context in which the step will be scheduled.</param>
    /// <param name="stepNode">The step node to be scheduled. If null, no operation is performed.</param>
    /// <param name="owner">The owner step execution context, if any, that triggered the scheduling request.</param>
    /// <param name="options">Optional scheduling parameters to customize the scheduling behavior.</param>
    public Task ScheduleStepAsync(StepExecutionContext context, StepNode? stepNode, StepExecutionContext? owner = null, ScheduleWorkOptions? options = null)
    {
        if (stepNode == null)
            return Task.CompletedTask;
        
        context.PipelineExecutionContext.Schedule(stepNode!, owner ?? context, options);
        return Task.CompletedTask;
    }
}