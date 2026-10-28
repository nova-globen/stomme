using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// A request to dispatch a pipeline instance for execution.
/// </summary>
public class DispatchPipelineInstanceRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchPipelineInstanceRequest"/> class.
    /// </summary>
    [JsonConstructor]
    public DispatchPipelineInstanceRequest()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchPipelineInstanceRequest"/> class.
    /// </summary>
    /// <param name="instanceId">The ID of the pipeline instance to dispatch.</param>
    public DispatchPipelineInstanceRequest(string instanceId)
    {
        InstanceId = instanceId;
    }
    
    /// <summary>
    /// The ID of the pipeline instance to dispatch.
    /// </summary>
    public string InstanceId { get; init; } = null!;
    
    /// <summary>
    /// The ID of the bookmark to resume.
    /// </summary>
    public string? BookmarkId { get; set; }
    public StepHandle? StepHandle { get; init; }
    public IDictionary<string, object>? Input { get; init; }
    public IDictionary<string, object>? Properties { get; init; }
    public string? CorrelationId { get; init; }
}