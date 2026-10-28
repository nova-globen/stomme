using System.Runtime.CompilerServices;
using System.Text.Json;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Signals;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Signals the current composite step to complete itself as a whole.
/// </summary>
[Step("Stomme", "Composition", "Signals the current composite step to complete itself as a whole.")]
[PublicAPI]
public class Complete : Step, ITerminalNode
{
    /// <inheritdoc />
    public Complete([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public Complete(IEnumerable<string> outcomes, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<object>(outcomes.ToList()), source, line)
    {
    }

    /// <inheritdoc />
    public Complete(Func<ExpressionExecutionContext, ICollection<string>> outcomes, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<object>(outcomes), source, line)
    {
    }

    /// <inheritdoc />
    public Complete(Func<ExpressionExecutionContext, string> outcome, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(context => [outcome(context)], source, line)
    {
    }

    /// <inheritdoc />
    public Complete(Input<object> outcomes, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        Outcomes = outcomes;
    }

    /// <summary>
    /// The outcome or set of outcomes to complete this step with.
    /// </summary>
    [Input(
        Description = "The outcome or set of outcomes to complete this step with.",
        UIHint = InputUIHints.OutcomePicker,
        DefaultSyntax = "Object"
    )]
    public Input<object?> Outcomes { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var outcomesValue = Outcomes.GetOrDefault(context);
        var outcomes = InterpretOutcomes(outcomesValue).ToArray();
        
        await context.SendSignalAsync(new CompleteCompositeSignal(new Outcomes(outcomes))).ConfigureAwait(false);
        await context.CompleteStepAsync().ConfigureAwait(false);
    }

    private static IEnumerable<string> InterpretOutcomes(object? outcomesValue)
    {
        switch (outcomesValue)
        {
            case string singleOutcome:
                yield return singleOutcome;
                break;
            case IEnumerable<string> outcomeStrings:
                foreach (var outcome in outcomeStrings)
                    yield return outcome;
                break;
            case IEnumerable<object> outcomeObjects:
            {
                foreach (var outcome in outcomeObjects)
                    yield return outcome.ToString()!;
                break;
            }
            case JsonElement jsonElement:
            {
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var outcomeArray = jsonElement.EnumerateArray().ToList();
                    foreach (var element in outcomeArray)
                        yield return element.ToString();
                }
                else
                    yield return jsonElement.ToString();

                break;
            }
            default:
                yield return "Done";
                break;
        }
    }
}