using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepExecutionContextSchedulerStrategy : IStepExecutionContextSchedulerStrategy
{
    /// <inheritdoc />
    public async Task ScheduleStepAsync(StepExecutionContext context, IStep? step, StepExecutionContext? owner, ScheduleWorkOptions? options = null)
    {
        var stepNode = step != null
            ? context.PipelineExecutionContext.FindNodeByStep(step) ?? throw new InvalidOperationException("The specified step is not part of the pipeline.")
            : null;
        await ScheduleStepAsync(context, stepNode, owner, options).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ScheduleStepAsync(StepExecutionContext context, StepNode? stepNode, StepExecutionContext? owner = null, ScheduleWorkOptions? options = null)
    {
        var pipelineExecutionContext = context.PipelineExecutionContext;
        if (context.GetIsBackgroundExecution())
        {
            // Validate that the specified step is part of the pipeline.
            if (stepNode != null && !pipelineExecutionContext.NodeStepLookup.ContainsKey(stepNode.Step))
                throw new InvalidOperationException("The specified step is not part of the pipeline.");

            var scheduledStep = new ScheduledStep
            {
                StepNodeId = stepNode?.NodeId,
                OwnerStepInstanceId = owner?.Id,
                Options = options != null
                    ? new ScheduledStepOptions
                    {
                        CompletionCallback = options.CompletionCallback?.Method.Name,
                        Tag = options.Tag,
                        ExistingStepInstanceId = options.ExistingStepExecutionContext?.Id,
                        PreventDuplicateScheduling = options.PreventDuplicateScheduling,
                        SchedulingStepExecutionId = options.SchedulingStepExecutionId ?? context.Id,
                        Variables = options.Variables?.ToList(),
                        Input = options.Input
                    }
                    : null
            };

            var scheduledSteps = context.GetBackgroundScheduledSteps().ToList();
            scheduledSteps.Add(scheduledStep);
            context.SetBackgroundScheduledSteps(scheduledSteps);
            return;
        }

        var completionCallback = options?.CompletionCallback;
        owner ??= context;

        if (stepNode == null)
        {
            if (completionCallback != null)
            {
                context.Tag = options?.Tag;
                var completedContext = new StepCompletedContext(context, context);
                await completionCallback(completedContext).ConfigureAwait(false);
            }
            else
                await owner.CompleteStepAsync().ConfigureAwait(false);

            return;
        }

        pipelineExecutionContext.Schedule(stepNode, owner, options);
    }
}