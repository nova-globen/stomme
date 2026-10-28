using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using [Owner].Module.[Module].Domain;
using [Owner].Module.[Module].Models;
using [Owner].Module.[Module].Persistence;

namespace [Owner].Module.[Module].Application
{
    // The module's commands and queries. Each carries [Requires] with one of the module's keys (D99): the host's authorization
    // behaviour refuses a caller without it before the handler runs, whether the request came from the API, a page or a job.
    // Every one works through the REQUEST's site - another site's rows do not exist for it.

    [Requires([Module]Permissions.View)]
    public sealed record List[Module]EntriesQuery : IQuery<IReadOnlyList<[Module]EntryModel>>;

    public sealed class List[Module]EntriesQueryHandler(I[Module]EntryRepository entries, SiteState siteState)
        : IQueryHandler<List[Module]EntriesQuery, IReadOnlyList<[Module]EntryModel>>
    {
        public async Task<IReadOnlyList<[Module]EntryModel>> Handle(List[Module]EntriesQuery request, CancellationToken ct) =>
            (await entries.ListForSiteAsync([Module]Scope.Site(siteState), ct)).Select([Module]Scope.ToModel).ToList();
    }

    [Requires([Module]Permissions.Edit)]
    public sealed record Add[Module]EntryCommand(string Text) : ICommand<[Module]EntryModel>;

    public sealed class Add[Module]EntryCommandHandler(
        I[Module]EntryRepository entries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        ICurrentUser currentUser,
        TimeProvider clock) : ICommandHandler<Add[Module]EntryCommand, [Module]EntryModel>
    {
        public async Task<[Module]EntryModel> Handle(Add[Module]EntryCommand request, CancellationToken ct)
        {
            // Tenant-owned by contract (BL-111): the row is stamped with the ambient tenant, and refused when there is none.
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("An entry belongs to a tenant; none is resolved.");
            var author = currentUser.UserId ?? throw new InvalidOperationException("An entry is added by a signed-in user.");
            var entry = [Module]Entry.Add(Guid.NewGuid(), tenantId, [Module]Scope.Site(siteState), request.Text, author, clock.GetUtcNow().UtcDateTime);
            await entries.AddAsync(entry, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return [Module]Scope.ToModel(entry);
        }
    }

    internal static class [Module]Scope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0 ? siteId : throw new InvalidOperationException("The module works through the request's site; none is resolved.");

        public static [Module]EntryModel ToModel([Module]Entry entry) => new(entry.Id, entry.Text, entry.AuthorUserId, entry.CreatedUtc);
    }
}
