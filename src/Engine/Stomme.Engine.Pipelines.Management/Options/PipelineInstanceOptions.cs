using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Options;

/// <summary>
/// Represents parameters for creating a pipeline instance.
/// </summary>
[UsedImplicitly]
public class PipelineInstanceOptions
{
    /// <summary>
    /// The correlation ID of the pipeline, if any.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The name of the pipeline instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The input to the pipeline instance, if any.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Any properties to assign to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// A pre-created pipeline instance ID that the new pipeline instance should use. If not provided, a new ID will be generated.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// The ID of the parent pipeline instance, if any.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }
}