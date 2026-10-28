using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition is retracted.
/// </summary>
/// <param name="PipelineDefinition">The pipeline definition.</param>
[PublicAPI]
public record PipelineDefinitionRetracted(PipelineDefinition PipelineDefinition) : INotification;