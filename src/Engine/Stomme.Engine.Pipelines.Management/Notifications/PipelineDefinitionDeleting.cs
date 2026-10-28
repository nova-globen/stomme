using Stomme.Engine.Mediator.Contracts;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition is about to be deleted.
/// </summary>
/// <param name="DefinitionId">The ID of the pipeline definition.</param>
[PublicAPI]
public record PipelineDefinitionDeleting(string DefinitionId) : INotification;