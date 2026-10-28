using Stomme.Engine.Mediator.Contracts;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when a pipeline definition is deleted.
/// </summary>
/// <param name="DefinitionId">The ID of the pipeline definition.</param>
[PublicAPI]
public record PipelineDefinitionDeleted(string DefinitionId) : INotification;