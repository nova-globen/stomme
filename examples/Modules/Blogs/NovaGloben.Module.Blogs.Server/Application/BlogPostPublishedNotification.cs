using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Microsoft.Extensions.Logging;

namespace NovaGloben.Module.Blogs.Application
{
    /// <summary>
    /// The MEDIATOR notification published by <c>CreatePostCommandHandler</c> after a post is written, via
    /// <c>IMediator.Publish</c> — the mediator's <see cref="INotification"/> fan-out path. It coexists with the
    /// framework domain-event dispatcher (<c>BlogPostPublishedDomainEvent</c>); the two in-process mechanisms
    /// run side by side, deliberately.
    /// </summary>
    public sealed record BlogPostPublishedNotification(Guid PostId, int ModuleId, string Title, string Author)
        : INotification;

    /// <summary>
    /// An in-process handler for the notification. A real module would ping subscribers, warm a cache or
    /// enqueue a webhook here; this one logs, which is enough to make the mediator's publish path observable
    /// when a post is created.
    /// </summary>
    public sealed class BlogPostPublishedNotificationHandler(ILogger<BlogPostPublishedNotificationHandler> logger)
        : INotificationHandler<BlogPostPublishedNotification>
    {
        public Task Handle(BlogPostPublishedNotification notification, CancellationToken ct)
        {
            logger.LogInformation(
                "[Blogs] MEDIATOR INotification: {Author} published \"{Title}\" to blog module {ModuleId} (post {PostId}).",
                notification.Author, notification.Title, notification.ModuleId, notification.PostId);
            return Task.CompletedTask;
        }
    }
}
