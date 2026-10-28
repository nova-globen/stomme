namespace Stomme.Engine.Pipelines.Models;

public record PipelinePublication(bool IsLatest, bool IsPublished)
{
    public static PipelinePublication LatestAndPublished => new(true, true);
    public static PipelinePublication LatestDraft => new(true, false);
    public static PipelinePublication Draft => new(false, false);
}