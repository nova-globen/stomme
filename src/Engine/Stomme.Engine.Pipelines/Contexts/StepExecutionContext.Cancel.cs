using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Signals;

namespace Stomme.Engine.Pipelines;

public partial class StepExecutionContext
{
    private readonly INotificationSender _publisher;

    private bool CanCancelStep()
    {
        return Status is not StepStatus.Canceled and not StepStatus.Completed;
    }

    private async Task CancelStepAsync()
    {
        if(!CanCancelStep())
            return;
        
        TransitionTo(StepStatus.Canceled);
        ClearBookmarks();
        ClearCompletionCallbacks();
        PipelineExecutionContext.Bookmarks.RemoveWhere(x => x.StepNodeId == NodeId);
        AddExecutionLogEntry("Canceled");
        await this.SendSignalAsync(new CancelSignal()).ConfigureAwait(false);
        await CancelChildStepsAsync().ConfigureAwait(false);
        
        // ReSharper disable once MethodSupportsCancellation
        await _publisher.SendAsync(new StepCancelled(this)).ConfigureAwait(false);
    }
    
    private async Task CancelChildStepsAsync()
    {
        var childContexts = PipelineExecutionContext.StepExecutionContexts.Where(x => x.ParentStepExecutionContext == this && x.CanCancelStep()).ToList();

        foreach (var childContext in childContexts)
            await childContext.CancelStepAsync().ConfigureAwait(false);
    }
}