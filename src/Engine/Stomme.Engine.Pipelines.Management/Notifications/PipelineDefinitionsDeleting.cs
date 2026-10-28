using Stomme.Engine.Mediator.Contracts;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when pipeline definitions are about to be deleted.
/// </summary>
/// <param name="DefinitionIds">The IDs of the pipeline definitions.</param>
[PublicAPI]
public record PipelineDefinitionsDeleting(ICollection<string> DefinitionIds) : INotification;