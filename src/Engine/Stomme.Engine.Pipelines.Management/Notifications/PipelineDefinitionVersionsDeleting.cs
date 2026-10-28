using Stomme.Engine.Mediator.Contracts;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when specific pipeline definition versions are about to be deleted.
/// </summary>
/// <param name="Ids">The IDs of the pipeline definitions.</param>
[PublicAPI]
public record PipelineDefinitionVersionsDeleting(ICollection<string> Ids) : INotification;