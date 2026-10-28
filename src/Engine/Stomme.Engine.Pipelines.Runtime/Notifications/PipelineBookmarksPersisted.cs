using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Published when the bookmarks of a pipeline instance have been persisted.
/// </summary>
/// <param name="Diff">The bookmarks that were added, removed, or unchanged.</param>
public record PipelineBookmarksPersisted(Diff<Bookmark> Diff) : INotification;