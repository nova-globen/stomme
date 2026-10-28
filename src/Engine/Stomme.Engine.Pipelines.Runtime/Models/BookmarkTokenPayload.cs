namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents the payload for a bookmark token, including the bookmark identifier and the associated pipeline instance identifier.
/// </summary>
public record BookmarkTokenPayload(string BookmarkId, string PipelineInstanceId);