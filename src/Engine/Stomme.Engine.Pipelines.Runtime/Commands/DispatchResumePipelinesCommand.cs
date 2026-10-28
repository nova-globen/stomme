using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

/// <summary>
/// Represents a command that dispatches a pipeline instance.
/// </summary>
/// <param name="stepTypeName">The name of the step type to dispatch.</param>
/// <param name="stimulus">The payload to match the bookmark.</param>
public class DispatchResumePipelinesCommand(string stepTypeName, object stimulus) : ICommand<Unit>
{
    /// <summary>The name of the step type to dispatch.</summary>
    public string StepTypeName { get; init; } = stepTypeName;

    /// <summary>The payload to match the bookmark.</summary>
    public object Stimulus { get; init; } = stimulus;

    /// <summary>The correlation ID of the pipeline instance to dispatch.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The ID of the pipeline instance to dispatch.</summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>The ID of the step instance to dispatch.</summary>
    public string? StepInstanceId { get; set; }

    /// <summary>The input to pass to the step.</summary>
    public IDictionary<string, object>? Input { get; set; }
    
    /// <summary>
    /// Any properties to attach to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }
}