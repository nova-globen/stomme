using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Testing.Shared.Steps;

/// <summary>
/// Concrete implementation of Container for testing that schedules children sequentially.
/// </summary>
public class TestContainer : Container
{
    private const string CurrentIndexProperty = "CurrentIndex";

    protected override async ValueTask ScheduleChildrenAsync(StepExecutionContext context)
    {
        await HandleItemAsync(context);
    }

    private async ValueTask HandleItemAsync(StepExecutionContext context)
    {
        var currentIndex = context.GetProperty<int>(CurrentIndexProperty);
        var childSteps = Steps.ToList();

        if (currentIndex >= childSteps.Count)
        {
            await context.CompleteStepAsync();
            return;
        }

        var nextStep = childSteps.ElementAt(currentIndex);
        await context.ScheduleStepAsync(nextStep, OnChildCompleted);
        context.UpdateProperty<int>(CurrentIndexProperty, x => x + 1);
    }

    private async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        await HandleItemAsync(context.TargetContext);
    }
}
