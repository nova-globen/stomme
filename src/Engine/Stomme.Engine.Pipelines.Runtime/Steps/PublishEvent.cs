using Stomme.Engine.Extensions;
using JetBrains.Annotations;
using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Faults the pipeline.
/// </summary>
[Step("Stomme", "Primitives", "Publishes an event.")]
[UsedImplicitly]
public class PublishEvent([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : Step(source, line)
{
    /// <summary>
    /// The name of the event to publish.
    /// </summary>
    [Input(Description = "The name of the event to publish.")]
    public Input<string> EventName { get; set; } = null!;

    /// <summary>
    /// The correlation ID to scope the event to.
    /// </summary>
    [Input(Description = "The correlation ID to scope the event to.")]
    public Input<string?> CorrelationId { get; set; } = null!;

    /// <summary>
    /// Whether the event is local to the pipeline.
    /// </summary>
    [Input(DisplayName = "Local event", Description = "Whether the event is local to the pipeline. When checked, the event will be delivered to this pipeline instance only.")]
    public Input<bool> IsLocalEvent { get; set; } = null!;

    /// <summary>
    /// The input to send as the event body.
    /// </summary>
    [Input(Description = "The payload to send as the event body.")]
    public Input<object?> Payload { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var eventName = EventName.Get(context);
        var correlationId = CorrelationId.GetOrDefault(context).NullIfWhiteSpace();
        var isLocalEvent = IsLocalEvent.GetOrDefault(context);
        var pipelineInstanceId = isLocalEvent ? context.PipelineExecutionContext.Id : null;
        var payload = Payload.GetOrDefault(context);
        var publisher = context.GetRequiredService<IEventPublisher>();

        await publisher.PublishAsync(eventName, correlationId, pipelineInstanceId, null, payload, true, context.CancellationToken).ConfigureAwait(false);
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}