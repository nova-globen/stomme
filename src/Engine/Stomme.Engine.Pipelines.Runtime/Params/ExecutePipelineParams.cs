using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Params;

[Obsolete("This type is obsolete.")]
public class ExecutePipelineParams
{
    public string? CorrelationId { get; set; }
    public string? BookmarkId { get; set; }
    public StepHandle? StepHandle { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public string? TriggerStepId { get; set; }
    public string? ParentPipelineInstanceId { get; set; }
    public CancellationToken CancellationToken { get; set; }
}