using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;
using NovaGloben.Module.Blogs.Domain;

namespace NovaGloben.Module.Blogs.Application
{
    /// <summary>
    /// The FRAMEWORK domain-event handler for <see cref="BlogPostPublishedDomainEvent"/>, dispatched by the
    /// framework's <c>CurrentScopeDomainEventDispatcher</c> when <c>IUnitOfWork.SaveChangesAsync</c> drains the
    /// aggregate — DISTINCT from the mediator's <c>BlogPostPublishedNotification</c>. Both fire on one publish,
    /// showing the two in-process fan-out paths coexisting.
    /// <para>
    /// It obeys the handler contract: it observes only — no <c>SaveChanges</c>, no new DI scope, no disposing
    /// the ambient session.
    /// </para>
    /// </summary>
    public sealed class BlogPostPublishedDomainEventHandler(
        ILogger<BlogPostPublishedDomainEventHandler> logger)
        : IDomainEventHandler<BlogPostPublishedDomainEvent>
    {
        public Task HandleAsync(BlogPostPublishedDomainEvent domainEvent, CancellationToken ct = default)
        {
            logger.LogInformation(
                "[Blogs] FRAMEWORK domain event: {Author} published \"{Title}\" to blog module {ModuleId} (post {PostId}).",
                domainEvent.Author, domainEvent.Title, domainEvent.ModuleId, domainEvent.PostId);
            return Task.CompletedTask;
        }
    }
}
