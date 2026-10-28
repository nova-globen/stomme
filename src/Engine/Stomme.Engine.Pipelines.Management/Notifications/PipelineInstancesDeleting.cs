using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when pipeline instances are about to be deleted.
/// </summary>
/// <param name="Ids">The IDs of the pipeline instances.</param>
public record PipelineInstancesDeleting(ICollection<string> Ids) : INotification;