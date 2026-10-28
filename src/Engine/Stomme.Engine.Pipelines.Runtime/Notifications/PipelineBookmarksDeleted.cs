using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

public record PipelineBookmarksDeleted(List<Bookmark> Bookmarks) : INotification;