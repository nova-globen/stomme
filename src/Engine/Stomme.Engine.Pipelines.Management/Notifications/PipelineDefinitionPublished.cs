using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition is published.
/// </summary>
/// <param name="PipelineDefinition">The pipeline definition.</param>
/// <param name="AffectedPipelines">The affected pipelines.</param>
[PublicAPI]
public record PipelineDefinitionPublished(PipelineDefinition PipelineDefinition, AffectedPipelines AffectedPipelines) : INotification;