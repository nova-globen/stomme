using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// Performs a boolean condition and returns an outcome based on the result.
/// </summary>
[FlowNode("True", "False")]
[Step("Stomme", "Branching", "Evaluate a Boolean condition to determine which path to execute next.", DisplayName = "Decision")]
[PublicAPI]
public class FlowDecision : Step
{
    /// <inheritdoc />
    public FlowDecision([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public FlowDecision(Func<ExpressionExecutionContext, bool> condition, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Condition = new(condition);
    }
    
    /// <inheritdoc />
    public FlowDecision(Func<ExpressionExecutionContext, ValueTask<bool>> condition, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Condition = new(condition);
    }
    
    /// <summary>
    /// The condition to evaluate.
    /// </summary>
    [Input(UIHint = InputUIHints.SingleLine)]
    public Input<bool> Condition { get; set; } = new(new Literal<bool>(false));

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var result = context.Get(Condition);
        var outcome = result ? "True" : "False";

        await context.CompleteStepWithOutcomesAsync(outcome).ConfigureAwait(false);
    }
}