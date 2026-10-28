using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Models;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Published when pipeline definitions have been reloaded.
/// </summary>
public record PipelineDefinitionsReloaded(ICollection<ReloadedPipelineDefinition> ReloadedPipelineDefinitions) : INotification;