using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition version is about to be deleted.
/// </summary>
[PublicAPI]
public record PipelineDefinitionVersionDeleting(PipelineDefinition PipelineDefinition) : INotification;