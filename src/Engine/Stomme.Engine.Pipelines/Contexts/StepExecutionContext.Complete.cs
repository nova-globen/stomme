using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Signals;

namespace Stomme.Engine.Pipelines;

public partial class StepExecutionContext
{
    /// <summary>
    /// Complete the current step. This should only be called by steps that explicitly suppress automatic-completion.
    /// </summary>
    public async ValueTask CompleteStepAsync(object? result = null)
    {
        var outcomes = result as Outcomes;

        // If the step is executing in the background, simply capture the result and return.
        if (this.GetIsBackgroundExecution())
        {
            if (outcomes != null)
                this.SetBackgroundOutcomes(outcomes.Names);
            else
                this.SetBackgroundCompletion();
            return;
        }

        // If the step is not running, do nothing.
        if (Status != StepStatus.Running)
            return;

        // Cancel any non-completed child steps.
        var childContexts = Children.Where(x => x.CanCancelStep()).ToList();
        
        foreach (var childContext in childContexts)
            await childContext.CancelStepAsync().ConfigureAwait(false);

        // Mark the step as complete.
        TransitionTo(StepStatus.Completed);

        // Record the outcomes, if any.
        if (outcomes != null)
            JournalData["Outcomes"] = outcomes.Names;

        // Add an execution log entry.
        AddExecutionLogEntry("Completed");

        // Send a signal.
        await this.SendSignalAsync(new StepCompleted(result)).ConfigureAwait(false);

        // Clear bookmarks.
        ClearBookmarks();
        PipelineExecutionContext.Bookmarks.RemoveWhere(x => x.StepInstanceId == Id);

        // Remove completion callbacks.
        ClearCompletionCallbacks();

        // Remove all associated variables, unless this is the root context - in which case we want to keep the variables since we're not deleting that one.
        if (ParentStepExecutionContext != null)
        {
            var variablePersistenceManager = GetRequiredService<IVariablePersistenceManager>();
            await variablePersistenceManager.DeleteVariablesAsync(this).ConfigureAwait(false);
        }

        // Update the completed at timestamp.
        CompletedAt = PipelineExecutionContext.SystemClock.UtcNow;
    }

    /// <summary>
    /// Complete the current step with the specified outcomes.
    /// </summary>
    public ValueTask CompleteStepWithOutcomesAsync(params string[] outcomes)
    {
        return CompleteStepAsync(new Outcomes(outcomes));
    }

    /// <summary>
    /// Complete the current composite step with the specified outcome.
    /// </summary>
    public async ValueTask CompleteCompositeAsync(params string[] outcomes)
    {
        await this.SendSignalAsync(new CompleteCompositeSignal(new Outcomes(outcomes))).ConfigureAwait(false);
    }
}