#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Metering.Application
{
    // Every command below carries [Requires(Manage)], names NO site or tenant, and reaches an existing quota only
    // through GetForCurrentSiteAsync - so a by-id command against another site's quota is refused with exactly the
    // answer an unknown id gets. A command that carried no key at all and took the tenant
    // from the request body would let a caller reach another tenant's quota; that shape is refused here.

    /// <summary>Defines a quota for a metric and window, or redefines the one that exists.</summary>
    [Requires(MeteringPermissions.Manage)]
    public sealed record DefineQuotaCommand(string Metric, string Period, long Limit, int WarnAtPercent) : ICommand<Guid>;

    public sealed class DefineQuotaCommandHandler(
        IQuotaRepository quotas,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        IMeteringClock clock)
        : ICommandHandler<DefineQuotaCommand, Guid>
    {
        public async Task<Guid> Handle(DefineQuotaCommand request, CancellationToken ct)
        {
            var siteId = MeteringWire.RequireSiteId(siteState);
            var metric = MetricKeys.RequireMetric(request.Metric);
            var period = MeteringWire.ParsePeriod(request.Period);

            // The tenant is resolved BEFORE the id, because the id now includes it: two tenants sharing
            // one database and one site would otherwise name the same row.
            var tenantId = tenantContext.CurrentTenantId
                ?? throw new InvalidOperationException("No tenant is resolved in this scope.");
            var id = QuotaIds.For(tenantId, siteId, metric, period);

            var existing = await quotas.GetForCurrentSiteAsync(id, siteId, ct);
            if (existing is not null)
            {
                existing.Redefine(request.Limit, request.WarnAtPercent);
                await quotas.UpdateAsync(existing, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return existing.Id;
            }

            var quota = QuotaAggregate.Define(id, tenantId, siteId, metric, period, request.Limit, request.WarnAtPercent, clock.UtcNow);
            await quotas.AddAsync(quota, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return quota.Id;
        }
    }

    /// <summary>Resets a counter and its window - an operator action, distinct from the calendar roll.</summary>
    [Requires(MeteringPermissions.Manage)]
    public sealed record ResetQuotaCommand(Guid Id) : ICommand<Unit>;

    public sealed class ResetQuotaCommandHandler(IQuotaRepository quotas, IUnitOfWork unitOfWork, SiteState siteState, IMeteringClock clock)
        : ICommandHandler<ResetQuotaCommand, Unit>
    {
        public async Task<Unit> Handle(ResetQuotaCommand request, CancellationToken ct)
        {
            var quota = await QuotaCommandGuard.RequireAsync(quotas, request.Id, siteState, ct);
            quota.Reset(clock.UtcNow);
            await quotas.UpdateAsync(quota, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Grants extra units for the current window.</summary>
    [Requires(MeteringPermissions.Manage)]
    public sealed record GrantQuotaOverrideCommand(Guid Id, long Additional, DateTime? ExpiresUtc) : ICommand<Unit>;

    public sealed class GrantQuotaOverrideCommandHandler(IQuotaRepository quotas, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<GrantQuotaOverrideCommand, Unit>
    {
        public async Task<Unit> Handle(GrantQuotaOverrideCommand request, CancellationToken ct)
        {
            var quota = await QuotaCommandGuard.RequireAsync(quotas, request.Id, siteState, ct);
            var expires = request.ExpiresUtc is { } value
                ? DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
                : (DateTime?)null;
            quota.GrantOverride(request.Additional, expires);
            await quotas.UpdateAsync(quota, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Suspends enforcement (consumption still accrues) or resumes it.</summary>
    [Requires(MeteringPermissions.Manage)]
    public sealed record SetQuotaEnforcementCommand(Guid Id, bool Enforced) : ICommand<Unit>;

    public sealed class SetQuotaEnforcementCommandHandler(IQuotaRepository quotas, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<SetQuotaEnforcementCommand, Unit>
    {
        public async Task<Unit> Handle(SetQuotaEnforcementCommand request, CancellationToken ct)
        {
            var quota = await QuotaCommandGuard.RequireAsync(quotas, request.Id, siteState, ct);
            if (request.Enforced)
            {
                quota.Resume();
            }
            else
            {
                quota.Suspend();
            }

            await quotas.UpdateAsync(quota, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Deletes a quota definition. The ledger is untouched: the facts outlive the allowance.</summary>
    [Requires(MeteringPermissions.Manage)]
    public sealed record DeleteQuotaCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteQuotaCommandHandler(IQuotaRepository quotas, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<DeleteQuotaCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteQuotaCommand request, CancellationToken ct)
        {
            var quota = await QuotaCommandGuard.RequireAsync(quotas, request.Id, siteState, ct);
            await quotas.DeleteAsync(quota, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    internal static class QuotaCommandGuard
    {
        /// <summary>The one by-id resolution every command uses - see the file header.</summary>
        internal static async Task<QuotaAggregate> RequireAsync(IQuotaRepository quotas, Guid id, SiteState siteState, CancellationToken ct) =>
            await quotas.GetForCurrentSiteAsync(id, MeteringWire.RequireSiteId(siteState), ct)
            ?? throw new InvalidOperationException($"No quota '{id}' exists on this site.");
    }
}
