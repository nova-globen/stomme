using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.CloudUsage.Services
{
    public interface ICloudUsageService
    {
        Task<CloudCostSummaryView> GetSummaryAsync(DateOnly? from, DateOnly? to);

        Task<CloudCostByProviderView> GetByProviderAsync(DateOnly? from, DateOnly? to);

        Task<CloudCostByServiceView> GetByServiceAsync(DateOnly? from, DateOnly? to, int? top);

        Task<CloudCostMonthOverMonthView> GetMonthOverMonthAsync(DateOnly? month);

        Task<CloudBudgetStatusView> GetBudgetStatusAsync(DateOnly? month);

        Task<List<CloudBudgetView>> ListBudgetsAsync();

        Task<CloudBudgetView> SetBudgetAsync(SetCloudBudgetRequest request);

        Task<CloudCostSyncView> SyncAsync(SyncCloudCostsRequest request);
    }

    /// <summary>The page's client over <c>api/CloudUsage</c> (BL-97 increment 4).</summary>
    public class CloudUsageService : ServiceBase, ICloudUsageService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public CloudUsageService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("CloudUsage");

        public async Task<CloudCostSummaryView> GetSummaryAsync(DateOnly? from, DateOnly? to) =>
            await GetJsonAsync<CloudCostSummaryView>($"{ApiUrl}/summary{Window(from, to)}");

        public async Task<CloudCostByProviderView> GetByProviderAsync(DateOnly? from, DateOnly? to) =>
            await GetJsonAsync<CloudCostByProviderView>($"{ApiUrl}/by-provider{Window(from, to)}");

        public async Task<CloudCostByServiceView> GetByServiceAsync(DateOnly? from, DateOnly? to, int? top) =>
            await GetJsonAsync<CloudCostByServiceView>($"{ApiUrl}/by-service{Window(from, to)}{(top is { } n ? (from is null && to is null ? "?" : "&") + "top=" + n.ToString(CultureInfo.InvariantCulture) : string.Empty)}");

        public async Task<CloudCostMonthOverMonthView> GetMonthOverMonthAsync(DateOnly? month) =>
            await GetJsonAsync<CloudCostMonthOverMonthView>($"{ApiUrl}/month-over-month{Month(month)}");

        public async Task<CloudBudgetStatusView> GetBudgetStatusAsync(DateOnly? month) =>
            await GetJsonAsync<CloudBudgetStatusView>($"{ApiUrl}/budget-status{Month(month)}");

        public async Task<List<CloudBudgetView>> ListBudgetsAsync() =>
            await GetJsonAsync<List<CloudBudgetView>>($"{ApiUrl}/budgets") ?? new List<CloudBudgetView>();

        public async Task<CloudBudgetView> SetBudgetAsync(SetCloudBudgetRequest request) =>
            await PutJsonAsync<SetCloudBudgetRequest, CloudBudgetView>($"{ApiUrl}/budgets", request);

        public async Task<CloudCostSyncView> SyncAsync(SyncCloudCostsRequest request) =>
            await PostJsonAsync<SyncCloudCostsRequest, CloudCostSyncView>($"{ApiUrl}/sync", request);

        private static string Window(DateOnly? from, DateOnly? to)
        {
            var parts = new List<string>();
            if (from is { } lower)
            {
                parts.Add("from=" + lower.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (to is { } upper)
            {
                parts.Add("to=" + upper.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
        }

        private static string Month(DateOnly? month) => month is { } value ? "?month=" + value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
    }
}
