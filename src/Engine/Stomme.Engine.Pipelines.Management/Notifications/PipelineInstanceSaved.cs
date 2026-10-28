using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline instance is saved.
/// </summary>
/// <param name="PipelineInstance">The pipeline instance.</param>
public record PipelineInstanceSaved(PipelineInstance PipelineInstance) : INotification;