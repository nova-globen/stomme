using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A domain event that is published before a burst of execution begins.  
/// </summary>
public record PipelineExecuting(Pipeline Pipeline, PipelineExecutionContext PipelineExecutionContext) : INotification;