using Stomme.Engine.Mediator.Contracts;
using JetBrains.Annotations;
using StepCompleted = Stomme.Engine.Pipelines.Signals.StepCompleted;

namespace Stomme.Engine.Pipelines.Behaviors;

/// <summary>
/// Implements a behavior that invokes "child completed" callbacks on parent steps.
/// </summary>
[UsedImplicitly]
public class ScheduledChildCallbackBehavior : Behavior
{
    /// <inheritdoc />
    public ScheduledChildCallbackBehavior(IStep owner) : base(owner)
    {
        OnSignalReceived<StepCompleted>(OnStepCompletedAsync);
    }

    private async ValueTask OnStepCompletedAsync(StepCompleted signal, SignalContext context)
    {
        var stepExecutionContext = context.ReceiverStepExecutionContext;
        var childStepExecutionContext = context.SenderStepExecutionContext;
        var childStepNode = childStepExecutionContext.StepNode;
        var callbackEntry = stepExecutionContext.PipelineExecutionContext.PopCompletionCallback(stepExecutionContext, childStepNode);

        if (callbackEntry?.CompletionCallback != null)
        {
            var completedContext = new StepCompletedContext(stepExecutionContext, childStepExecutionContext, signal.Result);
            var tag = callbackEntry.Tag;
            completedContext.TargetContext.Tag = tag;

            var mediator = stepExecutionContext.GetRequiredService<IMediator>();
            var invokingStepCallbackNotification = new Notifications.InvokingStepCallback(stepExecutionContext, childStepExecutionContext);
            await mediator.SendAsync(invokingStepCallbackNotification, context.CancellationToken).ConfigureAwait(false);
            await callbackEntry.CompletionCallback(completedContext).ConfigureAwait(false);
        }
    }
}