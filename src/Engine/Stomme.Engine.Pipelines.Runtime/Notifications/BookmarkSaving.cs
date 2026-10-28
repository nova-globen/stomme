using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Triggered when a bookmark is being saved.
/// </summary>
/// <param name="Bookmark">The bookmark being saved.</param>
public record BookmarkSaving(StoredBookmark Bookmark) : INotification;