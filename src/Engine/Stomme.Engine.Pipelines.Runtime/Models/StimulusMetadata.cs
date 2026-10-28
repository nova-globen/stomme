using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents information about how to route a stimulus to a step and pipeline instance.
/// </summary>
[UsedImplicitly]
public class StimulusMetadata
{
    /// <summary>
    /// The ID of the pipeline instance to route the stimulus to.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// The correlation ID of the pipeline instance to route the stimulus to.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The handle of the step to route the stimulus to.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// The ID of the bookmark created by the step to route the stimulus to.
    /// </summary>
    public string? BookmarkId { get; set; }
    
    /// <summary>
    /// The ID of the pipeline instance that sent the stimulus.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }
    
    /// <summary>
    /// Any input that was provided by the sender.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }
    
    /// <summary>
    /// Any properties that were provided by the sender.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }
}