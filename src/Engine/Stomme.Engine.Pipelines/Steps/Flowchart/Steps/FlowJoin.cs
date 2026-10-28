using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Steps.Flowchart.Contracts;
using Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// Merge multiple branches into a single branch of execution.
/// Note that this step is no longer necessary for either AND or OR merges, because all steps inherit the Join Kind property.
/// Use this step if an explicit join step is desired.
/// </summary>
[Step("Stomme", "Branching", "Explicitly merge multiple branches into a single branch of execution.", DisplayName = "Join")]
[UsedImplicitly]
public class FlowJoin : Step, IJoinNode
{
    /// <inheritdoc />
    public FlowJoin([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The join mode determines whether this step should continue as soon as one inbound path comes in (Wait Any), or once all inbound paths have executed (Wait All).
    /// </summary>
    [Input(
        Description = "The join mode determines whether this step should continue as soon as one inbound path comes in (WaitAny), or once all inbound paths have executed (WaitAll). To wait for all activated inbound paths, set the mode to WaitAllActive.",
        DefaultValue = FlowJoinMode.WaitAny,
        UIHint = InputUIHints.DropDown
    )]
    public Input<FlowJoinMode> Mode { get; set; } = new(FlowJoinMode.WaitAny);

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        if(!Flowchart.UseTokenFlow)
            if (context.ParentStepExecutionContext != null)
                await context.ParentStepExecutionContext.CancelInboundAncestorsAsync(this).ConfigureAwait(false);

        // The join behavior is handled by Flowchart, so we can simply complete the step here.
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}