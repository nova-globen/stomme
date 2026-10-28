using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Signals;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Branch execution into multiple branches.
/// </summary>
[Step("Stomme", "Control Flow", "Branch execution into multiple branches.")]
[PublicAPI]
[Browsable(false)]
public class Fork : Step
{
    /// <inheritdoc />
    public Fork([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        // Handle break signals directly instead of using the BreakBehavior. The behavior stops propagation of the signal, which is not what we want.
        OnSignalReceived<BreakSignal>(OnBreakSignalReceived);
    }

    /// <summary>
    /// Controls when this step yields control back to its parent step.
    /// </summary>
    [Input(
        Description = "Controls when this step yields control back to its parent step.",
        UIHint = InputUIHints.DropDown
    )]
    public ForkJoinMode JoinMode { get; set; } = ForkJoinMode.WaitAll;

    /// <summary>
    /// The branches to schedule.
    /// </summary>
    public ICollection<IStep> Branches { get; set; } = new List<IStep>();

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // If there are no branches, complete immediately
        if (Branches.Count == 0)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        await context.ScheduleSteps(Branches, CompleteChildAsync).ConfigureAwait(false);
    }

    private async ValueTask CompleteChildAsync(StepCompletedContext context)
    {
        var targetContext = context.TargetContext;
        var isBreaking = targetContext.GetIsBreaking();

        if (isBreaking)
        {
            await CompleteAsync(targetContext).ConfigureAwait(false);
            return;
        }

        var childContext = context.ChildContext;
        var completedChildStepId = childContext.Step.Id;

        // Append step to the set of completed steps.
        var completedStepIds = targetContext.UpdateProperty<HashSet<string>>("Completed", set =>
        {
            set ??= new();
            set.Add(completedChildStepId);
            return set;
        });

        var allChildStepIds = Branches.Select(x => x.Id).ToImmutableHashSet();
        var joinMode = JoinMode;

        switch (joinMode)
        {
            case ForkJoinMode.WaitAny:
                await CompleteAsync(targetContext).ConfigureAwait(false);
                break;
            case ForkJoinMode.WaitAll:
                var allSet = allChildStepIds.All(x => completedStepIds.Contains(x));
                if (allSet) await CompleteAsync(targetContext).ConfigureAwait(false);
                break;
        }
    }

    private void OnBreakSignalReceived(BreakSignal signal, SignalContext signalContext)
    {
        signalContext.ReceiverStepExecutionContext.SetIsBreaking();
    }
}