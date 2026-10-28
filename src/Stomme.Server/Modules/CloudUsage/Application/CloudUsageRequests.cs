#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Security.Abstractions;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.CloudUsage.Application
{
    // ============================================================================================================
    // CloudUsage's commands and queries (BL-97 increment 4, the predecessor platform's by name): the manual sync over the shared sync
    // path, the budget upsert, and the rollups - every one through the REQUEST's site, so another site's rows and
    // budgets do not exist for it. Totals stay per currency; no FX.
    // ============================================================================================================

    [Requires(CloudUsagePermissions.Manage)]
    public sealed record SyncCloudCostsCommand(Guid ConnectionId, string Provider, string? Window = null, string? TargetDataset = null, string? DatasetScope = null) : ICommand<CloudCostSyncView>;

    public sealed class SyncCloudCostsCommandHandler(ICloudCostSyncService sync) : ICommandHandler<SyncCloudCostsCommand, CloudCostSyncView>
    {
        public async Task<CloudCostSyncView> Handle(SyncCloudCostsCommand request, CancellationToken ct)
        {
            var result = await sync.SyncAsync(new CloudCostSyncInstruction(
                request.Provider,
                request.ConnectionId,
                request.Window,
                request.TargetDataset,
                request.DatasetScope,
                RequestBody: null,
                OriginReference: request.ConnectionId.ToString(),
                DatasetOrigin: OriginKind.ExternalQuery,
                MaterializeDataset: !string.IsNullOrWhiteSpace(request.TargetDataset)), ct);
            return result.Error is { } error
                ? new CloudCostSyncView(false, error.Code, error.Reason + " " + error.Detail, null, null, null, 0, null)
                : new CloudCostSyncView(true, null, result.Outcome!.Reason, result.Outcome.Provider, result.Outcome.From, result.Outcome.To, result.Outcome.Entries.Count, result.Outcome.DatasetName);
        }
    }

    [Requires(CloudUsagePermissions.Manage)]
    public sealed record SetCloudBudgetCommand(decimal MonthlyAmount, string Currency, CloudProvider? Provider = null) : ICommand<CloudBudgetView>;

    public sealed class SetCloudBudgetCommandValidator : AbstractValidator<SetCloudBudgetCommand>
    {
        public SetCloudBudgetCommandValidator()
        {
            RuleFor(command => command.MonthlyAmount).GreaterThan(0m);
            RuleFor(command => command.Currency).NotEmpty().Length(3);
        }
    }

    /// <summary>One budget per scope (all providers, or one): the second set for a scope amends it rather than duplicating it.</summary>
    public sealed class SetCloudBudgetCommandHandler(ICloudBudgetRepository budgets, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState)
        : ICommandHandler<SetCloudBudgetCommand, CloudBudgetView>
    {
        public async Task<CloudBudgetView> Handle(SetCloudBudgetCommand request, CancellationToken ct)
        {
            var siteId = CloudUsageScope.Site(siteState);
            var budget = await budgets.FindForScopeAsync(siteId, request.Provider, ct);

            // One write on every path: an amend of the scope's budget, or the scope's first.
            if (budget is not null)
            {
                budget.Amend(request.MonthlyAmount, request.Currency);
                await budgets.UpdateAsync(budget, ct);
            }
            else
            {
                var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A budget belongs to a tenant; none is resolved in this scope.");
                budget = CloudBudgetAggregate.Set(Guid.NewGuid(), tenantId, siteId, request.MonthlyAmount, request.Currency, request.Provider);
                await budgets.AddAsync(budget, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return View(budget);
        }

        internal static CloudBudgetView View(CloudBudgetAggregate budget) => new(budget.Id, budget.Provider, budget.MonthlyAmount, budget.Currency);
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record ListCloudBudgetsQuery : IQuery<IReadOnlyList<CloudBudgetView>>;

    public sealed class ListCloudBudgetsQueryHandler(ICloudBudgetRepository budgets, SiteState siteState) : IQueryHandler<ListCloudBudgetsQuery, IReadOnlyList<CloudBudgetView>>
    {
        public async Task<IReadOnlyList<CloudBudgetView>> Handle(ListCloudBudgetsQuery request, CancellationToken ct) =>
            (await budgets.ListForSiteAsync(CloudUsageScope.Site(siteState), ct)).Select(SetCloudBudgetCommandHandler.View).ToList();
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record GetCloudCostSummaryQuery(DateOnly? From = null, DateOnly? To = null) : IQuery<CloudCostSummaryView>;

    public sealed class GetCloudCostSummaryQueryHandler(ICloudCostRecordRepository records, SiteState siteState) : IQueryHandler<GetCloudCostSummaryQuery, CloudCostSummaryView>
    {
        public async Task<CloudCostSummaryView> Handle(GetCloudCostSummaryQuery request, CancellationToken ct)
        {
            var found = await CloudCostRollup.WindowAsync(records, CloudUsageScope.Site(siteState), request.From, request.To, ct);
            return new CloudCostSummaryView(request.From, request.To, found.Count, CloudCostRollup.ByCurrency(found));
        }
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record GetCloudCostByProviderQuery(DateOnly? From = null, DateOnly? To = null) : IQuery<CloudCostByProviderView>;

    public sealed class GetCloudCostByProviderQueryHandler(ICloudCostRecordRepository records, SiteState siteState) : IQueryHandler<GetCloudCostByProviderQuery, CloudCostByProviderView>
    {
        public async Task<CloudCostByProviderView> Handle(GetCloudCostByProviderQuery request, CancellationToken ct)
        {
            var found = await CloudCostRollup.WindowAsync(records, CloudUsageScope.Site(siteState), request.From, request.To, ct);
            var byProvider = found
                .GroupBy(r => new { r.Provider, r.Currency })
                .Select(g => new CloudCostProviderBreakdown(g.Key.Provider, g.Key.Currency, g.Sum(r => r.Cost), g.LongCount()))
                .OrderBy(b => b.Provider)
                .ThenBy(b => b.Currency, StringComparer.Ordinal)
                .ToList();
            return new CloudCostByProviderView(request.From, request.To, byProvider);
        }
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record GetCloudCostByServiceQuery(DateOnly? From = null, DateOnly? To = null, int? Top = null) : IQuery<CloudCostByServiceView>;

    public sealed class GetCloudCostByServiceQueryHandler(ICloudCostRecordRepository records, SiteState siteState) : IQueryHandler<GetCloudCostByServiceQuery, CloudCostByServiceView>
    {
        private const int DefaultTop = 10;

        public async Task<CloudCostByServiceView> Handle(GetCloudCostByServiceQuery request, CancellationToken ct)
        {
            var found = await CloudCostRollup.WindowAsync(records, CloudUsageScope.Site(siteState), request.From, request.To, ct);
            var byService = found
                .GroupBy(r => new { r.ServiceCategory, r.Service, r.Currency })
                .Select(g => new CloudCostServiceBreakdown(g.Key.ServiceCategory, g.Key.Service, g.Key.Currency, g.Sum(r => r.Cost), g.LongCount()))
                .OrderByDescending(b => b.TotalCost)
                .ThenBy(b => b.Service, StringComparer.OrdinalIgnoreCase)
                .Take(request.Top is > 0 ? request.Top.Value : DefaultTop)
                .ToList();
            return new CloudCostByServiceView(request.From, request.To, byService);
        }
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record GetCloudCostMonthOverMonthQuery(DateOnly? Month = null) : IQuery<CloudCostMonthOverMonthView>;

    public sealed class GetCloudCostMonthOverMonthQueryHandler(ICloudCostRecordRepository records, SiteState siteState, TimeProvider clock) : IQueryHandler<GetCloudCostMonthOverMonthQuery, CloudCostMonthOverMonthView>
    {
        public async Task<CloudCostMonthOverMonthView> Handle(GetCloudCostMonthOverMonthQuery request, CancellationToken ct)
        {
            var month = CloudCostRollup.MonthStart(request.Month, clock);
            var previous = month.AddMonths(-1);
            var both = await CloudCostRollup.WindowAsync(records, CloudUsageScope.Site(siteState), previous, month.AddMonths(1).AddDays(-1), ct);
            var current = CloudCostRollup.ByCurrency(CloudCostRollup.InMonth(both, month));
            var prior = CloudCostRollup.ByCurrency(CloudCostRollup.InMonth(both, previous));
            var deltas = current.Select(t => t.Currency).Union(prior.Select(t => t.Currency), StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(currency =>
            {
                var currentTotal = current.FirstOrDefault(t => t.Currency == currency)?.TotalCost ?? 0m;
                var previousTotal = prior.FirstOrDefault(t => t.Currency == currency)?.TotalCost ?? 0m;
                var delta = currentTotal - previousTotal;
                decimal? percent = previousTotal == 0m ? null : delta / previousTotal * 100m;
                return new CloudCostMonthDelta(currency, currentTotal, previousTotal, delta, percent);
            }).ToList();
            return new CloudCostMonthOverMonthView(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), previous.ToString("yyyy-MM", CultureInfo.InvariantCulture), deltas);
        }
    }

    [Requires(CloudUsagePermissions.View)]
    public sealed record GetCloudCostBudgetStatusQuery(DateOnly? Month = null) : IQuery<CloudBudgetStatusView>;

    public sealed class GetCloudCostBudgetStatusQueryHandler(ICloudCostRecordRepository records, ICloudBudgetRepository budgets, SiteState siteState, TimeProvider clock)
        : IQueryHandler<GetCloudCostBudgetStatusQuery, CloudBudgetStatusView>
    {
        public async Task<CloudBudgetStatusView> Handle(GetCloudCostBudgetStatusQuery request, CancellationToken ct)
        {
            var siteId = CloudUsageScope.Site(siteState);
            var month = CloudCostRollup.MonthStart(request.Month, clock);
            var monthRecords = await CloudCostRollup.WindowAsync(records, siteId, month, month.AddMonths(1).AddDays(-1), ct);
            var lines = (await budgets.ListForSiteAsync(siteId, ct)).Select(budget =>
            {
                // Budget vs actual is honest about currency: only the month's spend in the budget's own currency (and
                // provider scope) counts - mixed-currency spend is never converted.
                var actual = monthRecords.Where(r => r.Currency == budget.Currency && (budget.Provider is null || r.Provider == budget.Provider)).Sum(r => r.Cost);
                return new CloudBudgetStatusLine(budget.Id, budget.Provider, budget.Currency, budget.MonthlyAmount, actual, budget.MonthlyAmount - actual, actual / budget.MonthlyAmount * 100m);
            }).ToList();
            return new CloudBudgetStatusView(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), lines);
        }
    }

    internal static class CloudUsageScope
    {
        public static int Site(SiteState siteState) => siteState.Alias?.SiteId ?? throw new InvalidOperationException("Cloud usage belongs to the request's site; none is resolved.");
    }

    internal static class CloudCostRollup
    {
        // A window of calendar days, both ends inclusive (the predecessor platform's), read as a range on the stored UTC midnight.
        public static Task<IReadOnlyList<CloudCostRecordAggregate>> WindowAsync(ICloudCostRecordRepository records, int siteId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
            records.ListForSiteAsync(siteId,
                from is { } lower ? CloudCostRecordAggregate.ToStoredDate(lower) : null,
                to is { } upper ? CloudCostRecordAggregate.ToStoredDate(upper.AddDays(1)) : null,
                ct);

        public static IReadOnlyList<CloudCostRecordAggregate> InMonth(IEnumerable<CloudCostRecordAggregate> records, DateOnly monthStart)
        {
            var start = CloudCostRecordAggregate.ToStoredDate(monthStart);
            var next = CloudCostRecordAggregate.ToStoredDate(monthStart.AddMonths(1));
            return records.Where(r => r.DateUtc >= start && r.DateUtc < next).ToList();
        }

        public static IReadOnlyList<CloudCostCurrencyTotal> ByCurrency(IEnumerable<CloudCostRecordAggregate> records) =>
            records.GroupBy(r => r.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CloudCostCurrencyTotal(g.Key, g.Sum(r => r.Cost), g.LongCount())).ToList();

        public static DateOnly MonthStart(DateOnly? month, TimeProvider clock)
        {
            var anchor = month ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            return new DateOnly(anchor.Year, anchor.Month, 1);
        }
    }
}
