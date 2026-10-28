using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Behaviors;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Iterate over a sequence of steps between a start and an end number.
/// </summary>
[Step("Stomme", "Looping", "Iterate over a sequence of steps between a start and an end number.")]
[PublicAPI]
public class For : Step
{
    private const string CurrentStepProperty = "CurrentStep";

    /// <inheritdoc />
    public For([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        Behaviors.Add<BreakBehavior>(this);
    }

    /// <inheritdoc />
    public For(int start, int end, int step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Start = new(start);
        End = new(end);
        Step = new(step);
    }

    /// <summary>
    /// The start step.
    /// </summary>
    [Input(Description = "The start step.")]
    public Input<int> Start { get; set; } = new(0);

    /// <summary>
    /// The end step.
    /// </summary>
    [Input(Description = "The end step.")]
    public Input<int> End { get; set; } = new(0);

    /// <summary>
    /// The step size. To count down, enter a negative number.
    /// </summary>
    [Input(Description = "The step size. To count down, enter a negative number.")]
    public Input<int> Step { get; set; } = new(1);

    /// <summary>
    /// Controls whether the end step is upper/lowerbound inclusive or exclusive. True (inclusive) by default.
    /// </summary>
    [Input(Description = "Controls whether the end step is upper/lowerbound inclusive or exclusive. True (inclusive) by default.", DefaultValue = true)]
    public Input<bool> OuterBoundInclusive { get; set; } = new(true);

    /// <summary>
    /// The step to execute for each iteration.
    /// </summary>
    [Port]
    public IStep? Body { get; set; }

    /// <summary>
    /// Stores the current value for each iteration. 
    /// </summary>
    [Output]
    public Output<object?> CurrentValue { get; set; } = new();

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var iterateNode = Body;

        if (iterateNode == null)
            return;

        await HandleIteration(context).ConfigureAwait(false);
    }

    private async ValueTask HandleIteration(StepExecutionContext context)
    {
        var iterateNode = Body;
        var end = context.Get(End);
        var currentValue = context.GetProperty<int?>(CurrentStepProperty);
        var start = context.Get(Start);
        var step = context.Get(Step);
        var inclusive = context.Get(OuterBoundInclusive);
        var increment = step >= 0;

        currentValue = currentValue == null ? start : currentValue + step;

        var isBreaking = context.GetIsBreaking();

        var loop =
            !isBreaking && (increment && inclusive ? currentValue <= end
                : increment && !inclusive ? currentValue < end
                : !increment && inclusive ? currentValue >= end
                : !increment && !inclusive && currentValue > end);

        if (loop)
        {
            if (iterateNode != null)
            {
                var variables = new[]
                {
                    new Variable("CurrentValue", currentValue)
                };
                await context.ScheduleStepAsync(iterateNode, OnChildComplete, variables: variables).ConfigureAwait(false);
            }
            

            // Update internal step.
            context.SetProperty(CurrentStepProperty, currentValue);

            // Update loop variable.
            context.Set(CurrentValue, currentValue);
        }
        else
        {
            // Report step completion.
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask OnChildComplete(StepCompletedContext context) => await HandleIteration(context.TargetContext).ConfigureAwait(false);
}