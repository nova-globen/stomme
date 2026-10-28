using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Published when the specified pipeline's triggers have been indexed.
/// </summary>
/// <param name="IndexedPipelineTriggers">Contains the pipeline that was indexed and the resulting triggers.</param>
public record PipelineTriggersIndexed(IndexedPipelineTriggers IndexedPipelineTriggers) : INotification;