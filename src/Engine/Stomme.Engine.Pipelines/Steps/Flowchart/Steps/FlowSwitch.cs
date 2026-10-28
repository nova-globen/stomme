using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// Evaluates the specified case conditions and schedules the one that evaluates to <code>true</code>.
/// </summary>
[FlowNode("Default")]
[Step("Stomme", "Branching", "Evaluate a set of case conditions and schedule the step for a matching case.", DisplayName = "Switch (flow)")]
[PublicAPI]
public class FlowSwitch : Step
{
    /// <inheritdoc />
    public FlowSwitch([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The possible cases to evaluate.
    /// </summary>
    [Input(UIHint = "flow-switch-editor")]
    public ICollection<FlowSwitchCase> Cases { get; set; } = new List<FlowSwitchCase>();

    /// <summary>
    /// The switch mode determines whether the first match should be scheduled, or all matches.
    /// </summary>
    [Input(
        Description = "The switch mode determines whether the first match should be scheduled, or all matches.",
        UIHint = InputUIHints.DropDown
    )]
    public Input<SwitchMode> Mode { get; set; } = new(SwitchMode.MatchFirst);

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var matchingCases = (await FindMatchingCasesAsync(context.ExpressionExecutionContext).ConfigureAwait(false)).ToList();
        var hasAnyMatches = matchingCases.Any();
        var mode = context.Get(Mode);
        var results = mode == SwitchMode.MatchFirst ? hasAnyMatches ? [matchingCases.First()] : Array.Empty<FlowSwitchCase>() : matchingCases.ToArray();
        var outcomes = hasAnyMatches ? results.Select(r => r.Label).ToArray() : ["Default"];

        await context.CompleteStepAsync(new Outcomes(outcomes)).ConfigureAwait(false);
    }

    private async Task<IEnumerable<FlowSwitchCase>> FindMatchingCasesAsync(ExpressionExecutionContext context)
    {
        var matchingCases = new List<FlowSwitchCase>();
        var expressionEvaluator = context.GetRequiredService<IExpressionEvaluator>();

        foreach (var switchCase in Cases)
        {
            var result = await expressionEvaluator.EvaluateAsync<bool?>(switchCase.Condition, context).ConfigureAwait(false);

            if (result == true)
            {
                matchingCases.Add(switchCase);
            }
        }

        return matchingCases;
    }
}