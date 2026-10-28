using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Execute a set of steps in parallel.
/// </summary>
[Step("Stomme", "Pipelines", "Execute a set of steps in parallel.")]
[Browsable(false)] // Hidden from the designer until we have Sequential step designer support.
public class Parallel : Container
{
    private const string ScheduledChildrenProperty = "ScheduledChildren";

    /// <inheritdoc />
    public Parallel([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
    
    /// <inheritdoc />
    public Parallel(params IStep[] steps) : this()
    {
        Steps = steps;
    }

    /// <inheritdoc />
    protected override async ValueTask ScheduleChildrenAsync(StepExecutionContext context)
    {
        // If there are no steps, complete immediately
        if (Steps.Count == 0)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        context.SetProperty(ScheduledChildrenProperty, Steps.Count);

        // For Parallel, all children are scheduled by the parent step (this), so the scheduling step is the Parallel itself
        var options = new ScheduleWorkOptions
        {
            CompletionCallback = OnChildCompleted,
            SchedulingStepExecutionId = context.Id
        };

        foreach (var step in Steps)
            await context.ScheduleStepAsync(step, options).ConfigureAwait(false);
    }
    
    private static async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        var remainingChildren = context.TargetContext.UpdateProperty<int>(ScheduledChildrenProperty, scheduledChildren => scheduledChildren - 1);
        
        if (remainingChildren == 0)
            await context.TargetContext.CompleteStepAsync().ConfigureAwait(false);
    }
}