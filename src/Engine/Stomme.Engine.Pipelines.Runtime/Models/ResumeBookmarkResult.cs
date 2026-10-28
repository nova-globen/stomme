using Stomme.Engine.Pipelines.Runtime.Messages;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents the result of resuming a bookmark.
/// </summary>
/// <param name="Matched">Whether the bookmark was matched.</param>
/// <param name="Response">The response from running the pipeline instance. This is only set if the bookmark was matched.</param>
public record ResumeBookmarkResult(bool Matched, RunPipelineInstanceResponse? Response = null)
{
    /// <summary>
    /// Represents the result of resuming a bookmark when the bookmark was not found.
    /// </summary>
    public static ResumeBookmarkResult NotFound() => new(false);
    
    /// <summary>
    /// Represents the result of resuming a bookmark when the bookmark was not found.
    /// </summary>
    public static ResumeBookmarkResult Found(RunPipelineInstanceResponse response) => new(true, response);
}