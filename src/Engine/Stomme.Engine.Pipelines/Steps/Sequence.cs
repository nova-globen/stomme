using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Signals;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Execute a set of steps in sequence.
/// </summary>
[Category("Pipelines")]
[Step("Stomme", "Pipelines", "Execute a set of steps in sequence.")]
[PublicAPI]
[Browsable(false)]
public class Sequence : Container
{
    private const string CurrentIndexProperty = "CurrentIndex";

    /// <inheritdoc />
    public Sequence([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        OnSignalReceived<BreakSignal>(OnBreakSignalReceived);
    }

    /// <inheritdoc />
    protected override async ValueTask ScheduleChildrenAsync(StepExecutionContext context)
    {
        await HandleItemAsync(context).ConfigureAwait(false);
    }

    private async ValueTask HandleItemAsync(StepExecutionContext context, StepExecutionContext? completedChildContext = null)
    {
        var currentIndex = context.GetProperty<int>(CurrentIndexProperty);
        var childSteps = Steps.ToList();

        if (currentIndex >= childSteps.Count)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        var nextStep = childSteps.ElementAt(currentIndex);
        var options = new ScheduleWorkOptions
        {
            CompletionCallback = OnChildCompleted,
            SchedulingStepExecutionId = completedChildContext?.Id
        };
        await context.ScheduleStepAsync(nextStep, options).ConfigureAwait(false);
        context.UpdateProperty<int>(CurrentIndexProperty, x => x + 1);
    }

    private async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        var targetContext = context.TargetContext;
        var childContext = context.ChildContext;
        var isBreaking = targetContext.GetIsBreaking();
        var completedStep = childContext.Step;

        // If the complete step is a terminal node, complete the sequence immediately.
        if (isBreaking || completedStep is ITerminalNode)
        {
            await targetContext.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        await HandleItemAsync(targetContext, childContext).ConfigureAwait(false);
    }

    private void OnBreakSignalReceived(BreakSignal signal, SignalContext signalContext)
    {
        signalContext.ReceiverStepExecutionContext.SetIsBreaking();
    }
}