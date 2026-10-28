using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition is about to be published.
/// </summary>
/// <param name="PipelineDefinition">The pipeline definition.</param>
[PublicAPI]
public record PipelineDefinitionPublishing(PipelineDefinition PipelineDefinition) : INotification;