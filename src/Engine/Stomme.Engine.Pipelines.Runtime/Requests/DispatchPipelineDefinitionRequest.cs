using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// A request to dispatch a pipeline definition for execution.
/// </summary>
public class DispatchPipelineDefinitionRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchPipelineDefinitionRequest"/> class.
    /// </summary>
    [JsonConstructor]
    public DispatchPipelineDefinitionRequest()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchPipelineDefinitionRequest"/> class.
    /// </summary>
    /// <param name="definitionVersionId">The ID of the pipeline definition version to dispatch.</param>
    public DispatchPipelineDefinitionRequest(string definitionVersionId)
    {
        DefinitionVersionId = definitionVersionId;
    }

    /// <summary>
    /// The ID of the pipeline definition version to dispatch.
    /// </summary>
    public string DefinitionVersionId { get; set; } = default!;

    /// <summary>
    /// The ID of the parent pipeline instance.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }

    /// <summary>
    /// Any input to pass to the pipeline.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Any properties to attach to the pipeline.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// The correlation ID to use when dispatching the pipeline.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The ID to use when creating an instance of the pipeline to dispatch.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// The ID of the step that triggered the pipeline.
    /// </summary>
    public string? TriggerStepId { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this pipeline execution (for cross-pipeline call stack tracking).
    /// This is set when a parent pipeline invokes this pipeline via DispatchPipeline.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the parent pipeline that scheduled this pipeline execution.
    /// This is set when crossing pipeline boundaries.
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }

    /// <summary>
    /// The call stack depth of the scheduling step execution context.
    /// This is used to calculate the call stack depth when the scheduling context is not present
    /// in StepExecutionContexts (e.g., for cross-pipeline invocations).
    /// Should be set to the depth of the scheduling step (not depth + 1, as the increment is applied automatically).
    /// </summary>
    public int? SchedulingCallStackDepth { get; set; }
}