using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;

namespace Stomme.Engine.Pipelines.Runtime.Commands;

/// <summary>
/// Dispatches a pipeline definition.
/// </summary>
public class DispatchPipelineDefinitionCommand(string definitionVersionId) : ICommand<Unit>
{
    public string DefinitionVersionId { get; init; } = definitionVersionId;
    public string? ParentPipelineInstanceId { get; init; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public string? CorrelationId { get; set; }
    public string? InstanceId { get; set; }
    public string? TriggerStepId { get; set; }
    public string? SchedulingStepExecutionId { get; set; }
    public string? SchedulingPipelineInstanceId { get; set; }
    public int? SchedulingCallStackDepth { get; set; }
}