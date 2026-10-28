using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime;

public class ResumeBookmarkRequest
{
    public string PipelineInstanceId { get; set; } = null!;

    /// The ID of the bookmark that triggered the pipeline instance, if any.
    public string BookmarkId { get; set; } = null!;
    
    /// The handle of the step to schedule, if any.
    [Obsolete("Use StepInstanceId instead")]
    public StepHandle? StepHandle { get; set; }
    
    /// <summary>
    /// The ID of the step instance to resume, if any.
    /// </summary>
    public string? StepInstanceId { get; set; }
    
    /// Any additional properties to associate with the pipeline instance.
    public IDictionary<string, object>? Properties { get; set; }
    
    /// The input to the pipeline instance, if any.
    public IDictionary<string, object>? Input { get; set; }
}