using System;
using Stomme.SharedKernel.Domain;

namespace NovaGloben.Module.Blogs.Domain
{
    /// <summary>
    /// A DOMAIN EVENT raised by <see cref="BlogPost"/> when a post is published. It is dispatched IN-PROCESS,
    /// in the caller's unit of work, by the framework's <c>CurrentScopeDomainEventDispatcher</c> when
    /// <c>IUnitOfWork.SaveChangesAsync</c> drains the aggregate — the same mechanism the framework's own
    /// aggregates ride. The module authors zero dispatch code: enrolling the aggregate through
    /// <c>AddModuleAggregatePersistence</c> wires the tracker + dispatcher + unit of work for it.
    /// </summary>
    public sealed record BlogPostPublishedDomainEvent(Guid PostId, int ModuleId, string Title, string Author)
        : DomainEvent;
}
