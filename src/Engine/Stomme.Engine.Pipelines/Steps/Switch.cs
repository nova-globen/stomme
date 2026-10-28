using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// The Switch step is an approximation of the `switch` construct in C#.
/// When a case evaluates to true, the associated step is then scheduled for execution.
/// </summary>
[Step("Stomme", "Branching", "Evaluate a set of case conditions and schedule the step for a matching case.")]
[PublicAPI]
public class Switch : Step
{
    /// <inheritdoc />
    public Switch([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The value to switch on, made available as output for capturing.
    /// </summary>
    public Output<object>? Output { get; set; }

    /// <summary>
    /// The cases to evaluate.
    /// </summary>
    [Input(
        Description = "The cases to evaluate.",
        UIHint = "switch-editor"
    )]
    public ICollection<SwitchCase> Cases { get; set; } = new List<SwitchCase>();

    /// <summary>
    /// The switch mode determines whether the first match should be scheduled, or all matches.
    /// </summary>
    [Input(
        Description = "The switch mode determines whether the first match should be scheduled, or all matches.",
        UIHint = InputUIHints.DropDown
    )]
    public Input<SwitchMode> Mode { get; set; } = new(SwitchMode.MatchFirst);

    /// <summary>
    /// The default step to schedule when no case matches.
    /// </summary>
    [Port] public IStep? Default { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var matchingCases = (await FindMatchingCasesAsync(context.ExpressionExecutionContext).ConfigureAwait(false)).ToList();
        var hasAnyMatches = matchingCases.Any();
        var mode = context.Get(Mode);
        var results = mode == SwitchMode.MatchFirst 
            ? hasAnyMatches 
                ? new[] { matchingCases.First() } 
                : Array.Empty<SwitchCase>() 
            : matchingCases.ToArray();

        var scheduledStepIds = new HashSet<string>();

        if (hasAnyMatches)
        {
            foreach (var result in results)
            {
                if (result.Step == null)
                    continue;

                await context.ScheduleStepAsync(result.Step, OnChildStepCompletedAsync).ConfigureAwait(false);
                scheduledStepIds.Add(result.Step.Id);
            }
        }
        else if (Default != null)
        {
            await context.ScheduleStepAsync(Default, OnChildStepCompletedAsync).ConfigureAwait(false);
            scheduledStepIds.Add(Default.Id);
        }

        context.SetProperty("ScheduledStepIds", scheduledStepIds);

        if (!scheduledStepIds.Any())
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }

    private async Task<IEnumerable<SwitchCase>> FindMatchingCasesAsync(ExpressionExecutionContext context)
    {
        var matchingCases = new List<SwitchCase>();
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

    /// <summary>
    /// Tracks the completion of scheduled child steps and completes the Switch step only after all scheduled children have finished executing.
    /// </summary>
    private async ValueTask OnChildStepCompletedAsync(StepCompletedContext context)
    {
        var scheduledStepIds = context.TargetContext.GetProperty<HashSet<string>>("ScheduledStepIds");

        if (scheduledStepIds != null 
            && scheduledStepIds.Remove(context.ChildContext.Step.Id) 
            && scheduledStepIds.Count == 0)
        {
            await context.TargetContext.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Represents an individual case of the <see cref="Switch"/> step.
/// </summary>
public class SwitchCase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwitchCase"/> class.
    /// </summary>
    [JsonConstructor]
    public SwitchCase()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SwitchCase"/> class.
    /// </summary>
    /// <param name="label">The label of the case.</param>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="step">The step to schedule when the condition evaluates to true.</param>
    public SwitchCase(string label, Expression condition, IStep step)
    {
        Label = label;
        Condition = condition;
        Step = step;
    }
    
    /// <inheritdoc />
    public SwitchCase(string label, Func<ExpressionExecutionContext, ValueTask<bool>> condition, IStep step) : this(label, Expression.DelegateExpression(condition), step)
    {
    }

    /// <inheritdoc />
    public SwitchCase(string label, Func<ValueTask<bool>> condition, IStep step) : this(label, Expression.DelegateExpression(condition), step)
    {
    }

    /// <inheritdoc />
    public SwitchCase(string label, Func<ExpressionExecutionContext, bool> condition, IStep step) : this(label, Expression.DelegateExpression(condition), step)
    {
    }

    /// <inheritdoc />
    public SwitchCase(string label, Func<bool> condition, IStep step) : this(label, Expression.DelegateExpression(condition), step)
    {
    }

    /// <summary>
    /// The label of the case.
    /// </summary>
    public string Label { get; set; } = null!;

    /// <summary>
    /// The condition to evaluate.
    /// </summary>
    public Expression Condition { get; set; } = Expression.LiteralExpression(false);

    /// <summary>
    /// The step to schedule when the condition evaluates to true.
    /// </summary>
    public IStep? Step { get; set; }
}