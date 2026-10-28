using System.ComponentModel;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Pipelines.IncidentStrategies;

/// <summary>
/// An incident strategy that puts the entire pipeline in the <c>Faulted</c> state.
/// </summary>
[PublicAPI]
[Description("An incident strategy that puts the entire pipeline in the Faulted state.")]
public class FaultStrategy : IIncidentStrategy
{
    /// <inheritdoc />
    public void HandleIncident(StepExecutionContext context)
    {
        var pipelineExecutionContext = context.PipelineExecutionContext;
        
        if(pipelineExecutionContext.CanTransitionTo(PipelineSubStatus.Faulted))
            pipelineExecutionContext.TransitionTo(PipelineSubStatus.Faulted);
    }
}