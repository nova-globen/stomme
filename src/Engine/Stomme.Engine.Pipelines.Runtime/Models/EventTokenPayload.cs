namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents the payload of an event, serialized as a secured token.
/// </summary>
/// <param name="EventName">The name of the event.</param>
/// <param name="PipelineInstanceId">The ID of the pipeline instance to trigger with the event.</param>
public record EventTokenPayload(string EventName, string PipelineInstanceId);