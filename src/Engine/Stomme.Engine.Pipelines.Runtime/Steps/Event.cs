using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Wait for an event to be triggered.
/// </summary>
[Step("Stomme", "Primitives", "Wait for an event to be published.")]
[UsedImplicitly]
public class Event : Trigger<object?>
{
    public const string EventInputPipelineInputKey = "__EventPayloadPipelineInput";

    /// <inheritdoc />
    internal Event([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public Event(string eventName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Literal<string>(eventName), source, line)
    {
    }

    /// <inheritdoc />
    public Event(Func<string> eventName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<string>(Expression.DelegateExpression(eventName), new()), source, line)
    {
    }

    /// <inheritdoc />
    public Event(Func<ExpressionExecutionContext, string?> eventName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null)
        : this(new Input<string>(Expression.DelegateExpression(eventName), new()), source, line)
    {
    }

    /// <inheritdoc />
    public Event(Variable<string> variable, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        EventName = new(variable);
    }

    /// <inheritdoc />
    public Event(Literal<string> literal, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        EventName = new(literal);
    }

    /// <inheritdoc />
    public Event(Input<string> eventName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        EventName = eventName;
    }

    /// <summary>
    /// The name of the event to listen for.
    /// </summary>
    [Input(Description = "The name of the event to listen for.")]
    public Input<string> EventName { get; set; } = null!;

    /// <inheritdoc />
    protected override object GetTriggerPayload(TriggerIndexingContext context)
    {
        var eventName = EventName.Get(context.ExpressionExecutionContext);
        return context.GetEventStimulus(eventName);
    }

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var eventName = context.Get(EventName)!;
        context.WaitForEvent(eventName, CompleteInternalAsync);
    }

    private async ValueTask CompleteInternalAsync(StepExecutionContext context)
    {
        context.SetResult(context.GetEventInput());
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}