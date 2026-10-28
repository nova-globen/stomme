using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Behaviors;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Execute a step while a given condition evaluates to true.
/// </summary>
[Step("Stomme", "Looping", "Execute a step while a given condition evaluates to true.")]
[PublicAPI]
public class While : Step
{
    /// <summary>
    /// Creates a <see cref="While"/> step that loops forever.
    /// </summary>
    public static While True(IStep body) => new(body)
    {
        Condition = new(true)
    };

    /// <inheritdoc />
    public While([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        Behaviors.Add<BreakBehavior>(this);
    }

    /// <inheritdoc />
    public While(IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Body = body;
    }

    /// <inheritdoc />
    public While(Input<bool> condition, IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(body, source, line)
    {
        Condition = condition;
    }

    /// <inheritdoc />
    public While(Func<ExpressionExecutionContext, ValueTask<bool>> condition, IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<bool>(condition), body, source, line)
    {
    }

    /// <inheritdoc />
    public While(Func<ExpressionExecutionContext, bool> condition, IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<bool>(condition), body, source, line)
    {
    }

    /// <inheritdoc />
    public While(Func<ValueTask<bool>> condition, IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<bool>(condition), body, source, line)
    {
    }

    /// <inheritdoc />
    public While(Func<bool> condition, IStep? body = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<bool>(condition), body, source, line)
    {
    }

    /// <summary>
    /// The condition to evaluate.
    /// </summary>
    [Input(AutoEvaluate = false, UIHint = InputUIHints.SingleLine)]
    public Input<bool> Condition { get; set; } = new(false);

    /// <summary>
    /// The <see cref="IStep"/> to execute on every iteration.
    /// </summary>
    [Port]
    public IStep? Body { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context) => await HandleIterationAsync(context).ConfigureAwait(false);

    private async ValueTask OnBodyCompleted(StepCompletedContext context)
    {
        await HandleIterationAsync(context.TargetContext, context.ChildContext).ConfigureAwait(false);
    }

    private async ValueTask HandleIterationAsync(StepExecutionContext context, StepExecutionContext? completedChildContext = null)
    {
        var isBreaking = context.GetIsBreaking();
        var loop = !isBreaking && await context.EvaluateInputPropertyAsync<While, bool>(x => x.Condition).ConfigureAwait(false);

        if (loop)
        {
            var options = new ScheduleWorkOptions
            {
                CompletionCallback = OnBodyCompleted,
                SchedulingStepExecutionId = completedChildContext?.Id
            };
            await context.ScheduleStepAsync(Body, options).ConfigureAwait(false);
        }
        else
            await context.CompleteStepAsync().ConfigureAwait(false);
    }
}