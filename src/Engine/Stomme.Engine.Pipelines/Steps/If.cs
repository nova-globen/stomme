using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Evaluate a Boolean condition to determine which step to execute next.
/// </summary>
[Step("Stomme", "Branching", "Evaluate a Boolean condition to determine which step to execute next.")]
[PublicAPI]
public class If : Step<bool>
{
    /// <inheritdoc />
    public If([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public If(Input<bool> condition, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Condition = condition;
    }

    /// <inheritdoc />
    public If(Func<ExpressionExecutionContext, bool> condition, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Condition = new Input<bool>(condition);
    }

    /// <inheritdoc />
    public If(Func<bool> condition, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Condition = new Input<bool>(condition);
    }

    /// <summary>
    /// The condition to evaluate.
    /// </summary>
    [Input(UIHint = InputUIHints.SingleLine)]
    public Input<bool> Condition { get; set; } = new(new Literal<bool>(false));

    /// <summary>
    /// The step to execute when the condition evaluates to true.
    /// </summary>
    [Port]
    public IStep? Then { get; set; }

    /// <summary>
    /// The step to execute when the condition evaluates to false.
    /// </summary>
    [Port]
    public IStep? Else { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var result = context.Get(Condition);
        var nextStep = result ? Then : Else;

        context.Set(Result, result);
        await context.ScheduleStepAsync(nextStep, OnChildCompleted).ConfigureAwait(false);
    }

    private async ValueTask OnChildCompleted(StepCompletedContext context)
    {
        await context.TargetContext.CompleteStepAsync().ConfigureAwait(false);
    }
}