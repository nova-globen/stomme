using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// Branch execution into multiple branches that will be executed in parallel.
/// </summary>
[Step("Stomme", "Branching", "Branch execution into multiple branches that will be executed in parallel.", DisplayName = "Fork (flow)")]
[PublicAPI]
public class FlowFork : Step
{
    /// <inheritdoc />
    public FlowFork([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// A list of expected outcomes to handle.
    /// </summary>
    [Input(
        Description = "A list of expected outcomes to handle.",
        UIHint = InputUIHints.DynamicOutcomes
    )]
    public Input<ICollection<string>?> Branches { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var outcomes = Branches.GetOrDefault(context)?.ToArray() ?? ["Done"];

        await context.CompleteStepWithOutcomesAsync(outcomes).ConfigureAwait(false);
    }
}
