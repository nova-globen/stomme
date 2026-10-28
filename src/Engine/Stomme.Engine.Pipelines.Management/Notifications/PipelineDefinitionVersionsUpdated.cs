using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Notifications;

/// <summary>
/// A notification that is sent when specific pipeline definition versions are updated.
/// </summary>
[PublicAPI]
public record PipelineDefinitionVersionsUpdated(IEnumerable<PipelineDefinition> PipelineDefinitions) : INotification;