using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A domain event that is published everytime a burst of execution completes.  
/// </summary>
public record PipelineExecuted(Pipeline Pipeline, PipelineState PipelineState, PipelineExecutionContext PipelineExecutionContext) : INotification;