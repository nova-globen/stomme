namespace Stomme.Engine.Pipelines.Runtime.Parameters;

/// <summary>
/// Represents options for starting a pipeline host.
/// </summary>
public class StartPipelineHostParams
{
    /// <summary>
    /// An optional pipeline instance ID. If not specified, a new ID will be generated.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Indicates whether the pipeline instance to start is a new instance or an existing one.
    /// </summary>
    public bool IsExistingInstance { get; set; }

    /// <summary>
    /// An optional correlation ID.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Optional input to pass to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Any properties to attach to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// The ID of the step that triggered the pipeline instance.
    /// </summary>
    public string? TriggerStepId { get; set; }

    /// <summary>Cancellation tokens that can be used to cancel the pipeline instance without cancelling system-level operations.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Callback method that will be called when the status of the pipeline has been updated
    /// </summary>
    public Action<PipelineExecutionContext>? StatusUpdatedCallback { get; set; }

    /// <summary>
    /// The ID of the parent pipeline instance.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }
}