namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// A dispatch request that indicates that one or more pipelines are requested to be cancelled.
/// </summary>
public class DispatchCancelPipelineRequest
{
    /// <summary>
    /// The ID of the pipeline instance to cancel.
    /// </summary>
    public string PipelineInstanceId { get; set; } = default!;
}