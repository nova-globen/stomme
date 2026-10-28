#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Mediator;
using Stomme.Modules.CloudUsage.Application;
using Stomme.Shared;

namespace Stomme.Modules.CloudUsage.Services
{
    /// <summary>The page's service on the server: the same commands and queries the API sends, through the mediator (the Guestbook pattern).</summary>
    public sealed class ServerCloudUsageService(IMediator mediator) : ICloudUsageService, ITransientService
    {
        public Task<CloudCostSummaryView> GetSummaryAsync(DateOnly? from, DateOnly? to) => mediator.Send(new GetCloudCostSummaryQuery(from, to));

        public Task<CloudCostByProviderView> GetByProviderAsync(DateOnly? from, DateOnly? to) => mediator.Send(new GetCloudCostByProviderQuery(from, to));

        public Task<CloudCostByServiceView> GetByServiceAsync(DateOnly? from, DateOnly? to, int? top) => mediator.Send(new GetCloudCostByServiceQuery(from, to, top));

        public Task<CloudCostMonthOverMonthView> GetMonthOverMonthAsync(DateOnly? month) => mediator.Send(new GetCloudCostMonthOverMonthQuery(month));

        public Task<CloudBudgetStatusView> GetBudgetStatusAsync(DateOnly? month) => mediator.Send(new GetCloudCostBudgetStatusQuery(month));

        public async Task<List<CloudBudgetView>> ListBudgetsAsync() => (await mediator.Send(new ListCloudBudgetsQuery())).ToList();

        public Task<CloudBudgetView> SetBudgetAsync(SetCloudBudgetRequest request) => mediator.Send(new SetCloudBudgetCommand(request.MonthlyAmount, request.Currency, request.Provider));

        public Task<CloudCostSyncView> SyncAsync(SyncCloudCostsRequest request) =>
            mediator.Send(new SyncCloudCostsCommand(request.ConnectionId, request.Provider, request.Window, request.TargetDataset, request.DatasetScope));
    }
}
