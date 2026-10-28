using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Published when pipeline definitions have been refreshed.
/// </summary>
public record PipelineDefinitionsRefreshed(ICollection<string> PipelineDefinitionIds) : INotification;