using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

/// <summary>
/// Represents a command that dispatches a pipeline instance.
/// </summary>
public class DispatchTriggerPipelinesCommand(string stepTypeName, object stimulus) : ICommand<Unit>
{
    public string StepTypeName { get; init; } = stepTypeName;
    public object Stimulus { get; init; } = stimulus;
    public string? CorrelationId { get; set; }
    public string? PipelineInstanceId { get; set; }
    public string? StepInstanceId { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
}