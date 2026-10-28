namespace Stomme.Engine.Pipelines.Models;

public record PipelineIdentity(string DefinitionId, int Version, string Id, string? TenantId = null)
{
    public static PipelineIdentity VersionOne => new("1", 1, "1", null);
}