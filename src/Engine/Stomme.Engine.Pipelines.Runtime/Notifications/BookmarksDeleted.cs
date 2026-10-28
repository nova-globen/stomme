using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Triggered when bookmarks have been deleted.
/// </summary>
/// <param name="Bookmarks">The bookmarks that have been deleted.</param>
public record BookmarksDeleted(ICollection<StoredBookmark> Bookmarks) : INotification;