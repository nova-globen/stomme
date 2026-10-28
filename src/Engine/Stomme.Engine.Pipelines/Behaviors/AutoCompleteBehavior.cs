using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Behaviors;

/// <summary>
/// Automatically completes the currently executing step.
/// </summary>
[UsedImplicitly]
public class AutoCompleteBehavior : Behavior
{
    /// <inheritdoc />
    public AutoCompleteBehavior(IStep owner) : base(owner)
    {
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // If the step created any bookmarks, do not complete. 
        if (context.NewBookmarks.Any(x => x.StepId == context.Step.Id))
            return;
        
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}