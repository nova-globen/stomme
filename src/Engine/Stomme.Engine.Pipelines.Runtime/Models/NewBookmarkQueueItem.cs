using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

public class NewBookmarkQueueItem
{
    /// <summary>
    /// The pipeline instance ID owning the bookmark to resume.
    /// </summary>
    public string? PipelineInstanceId { get; set; } = default!;

    /// <summary>
    /// The bookmark ID to resume.
    /// </summary>
    public string? BookmarkId { get; set; } = default!;

    /// <summary>
    /// A bookmark payload hash of the bookmark to resume.
    /// </summary>
    public string? StimulusHash { get; set; }

    /// <summary>
    /// The ID of the step instance associated with the bookmark.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// The correlation ID that is associated with the bookmark if any.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The type name of the step associated with the bookmark.
    /// </summary>
    public string? StepTypeName { get; set; }

    /// <summary>
    /// Any options to apply when resuming the bookmark.
    /// </summary>
    public ResumeBookmarkOptions? Options { get; set; }
}