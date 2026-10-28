using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Triggered when bookmarks are being deleted.
/// </summary>
/// <param name="Bookmarks">The bookmarks being deleted.</param>
public record BookmarksDeleting(ICollection<StoredBookmark> Bookmarks) : INotification;