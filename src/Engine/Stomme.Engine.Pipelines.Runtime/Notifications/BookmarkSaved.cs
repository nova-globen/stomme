using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Triggered when a bookmark has been saved.
/// </summary>
/// <param name="Bookmark">The bookmark being saved.</param>
public record BookmarkSaved(StoredBookmark Bookmark) : INotification;