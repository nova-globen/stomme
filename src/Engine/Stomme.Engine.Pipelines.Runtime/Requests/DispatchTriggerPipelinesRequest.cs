namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// Represents a dispatch request to trigger all pipelines using the provided information.
/// </summary>
/// <param name="stepTypeName">The type name of the step to trigger.</param>
/// <param name="bookmarkPayload">AnyAsync bookmark payload to use to find the pipelines to trigger.</param>
public class DispatchTriggerPipelinesRequest(string stepTypeName, object bookmarkPayload)
{
    /// <summary>The type name of the step to trigger.</summary>
    public string StepTypeName { get; init; } = stepTypeName;

    /// <summary>AnyAsync bookmark payload to use to find the pipelines to trigger.</summary>
    public object BookmarkPayload { get; init; } = bookmarkPayload;

    /// <summary>AnyAsync correlation ID to use to find the pipelines to trigger.</summary>
    public string? CorrelationId { get; set; }

    public string? PipelineInstanceId { get; set; }
    public string? StepInstanceId { get; set; }

    /// <summary>Any input to send along.</summary>
    public IDictionary<string, object>? Input { get; set; }
    
    /// <summary>
    /// Any properties to attach to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }
}