namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Options for triggering pipelines.
/// </summary>
public class FindBookmarkOptions
{
    public string? BookmarkId { get; set; }
    public string? CorrelationId { get; set; }
    public string? PipelineInstanceId { get; set; }
    public string? StepInstanceId { get; set; }
}