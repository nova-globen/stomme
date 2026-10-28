using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class PipelineExecutionContextSchedulerStrategy : IPipelineExecutionContextSchedulerStrategy
{
    /// <inheritdoc />
    public StepWorkItem Schedule(PipelineExecutionContext context, StepNode stepNode, StepExecutionContext owner, ScheduleWorkOptions? options = null)
    {
        // Validate that the specified step is part of the pipeline.
        if (!context.NodeStepLookup.ContainsKey(stepNode.Step))
            throw new InvalidOperationException("The specified step is not part of the pipeline.");

        var scheduler = context.Scheduler;

        if (options?.PreventDuplicateScheduling == true)
        {
            // Check if the step is already scheduled for the specified owner.
            var existingWorkItem = scheduler.Find(x => x.Step.NodeId == stepNode.NodeId && x.Owner == owner);

            if (existingWorkItem != null)
                return existingWorkItem;
        }

        var step = stepNode.Step;
        var tag = options?.Tag;

        // Use explicit SchedulingStepExecutionId from options, or fall back to owner context.
        var schedulingStepExecutionId = options?.SchedulingStepExecutionId ?? owner.Id;

        // Use explicit SchedulingPipelineInstanceId from options, if any.
        var schedulingPipelineInstanceId = options?.SchedulingPipelineInstanceId;

        var workItem = new StepWorkItem(
            step,
            owner,
            tag,
            options?.Variables,
            options?.ExistingStepExecutionContext,
            options?.Input,
            schedulingStepExecutionId,
            schedulingPipelineInstanceId);
        var completionCallback = options?.CompletionCallback;

        context.AddCompletionCallback(owner, stepNode, completionCallback, tag);
        scheduler.Schedule(workItem);

        return workItem;
    }
}