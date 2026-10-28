namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Can be implemented by steps that need to do work after their bookmarks have been persisted.
/// </summary>
public interface IBookmarksPersistedHandler
{
    /// <summary>
    /// Invoked when bookmarks have been persisted.
    /// </summary>
    ValueTask BookmarksPersistedAsync(StepExecutionContext context);
}