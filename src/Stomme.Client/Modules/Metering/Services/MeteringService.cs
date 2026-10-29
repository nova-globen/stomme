using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Metering;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Services
{
    /// <summary>
    /// The client/server contract of the Metering module's UI. The controls depend on this
    /// interface, never on a repository: the server binds it to <c>ServerMeteringService</c> (a mediator dispatch,
    /// so every call is decided by the request's <c>[Requires]</c>), the browser to <see cref="MeteringService"/>
    /// (HTTP to <c>api/Metering</c>, decided by the action's <c>[RequiresPermissionKey]</c> AND the request's
    /// <c>[Requires]</c>). A denied or failed call answers null or an empty list; the controls pre-check with
    /// <c>ModuleBase.IsAuthorizedPermission</c> so a user is never offered an action the server would refuse.
    /// </summary>
    public interface IMeteringService
    {
        Task<List<MyUsageLine>> GetMyUsageAsync();

        Task<List<QuotaView>> GetQuotasAsync();

        Task<List<UsageRecordView>> GetUsageAsync(string metric = null, int take = 200);

        Task<AiUsageDashboard> GetAiDashboardAsync(int days = 30);

        Task<List<AiUsageRecordView>> GetAiCallsAsync(int take = 200);

        Task<Guid> DefineQuotaAsync(QuotaDefinition definition);

        Task ResetQuotaAsync(Guid quotaId);

        Task GrantOverrideAsync(Guid quotaId, QuotaOverrideRequest request);

        Task SetEnforcementAsync(Guid quotaId, bool enforced);

        Task DeleteQuotaAsync(Guid quotaId);
    }

    /// <summary>The BROWSER side: HTTP to the module's controller. The server decides every call again.</summary>
    public class MeteringService : ServiceBase, IMeteringService, IClientService
    {
        /// <summary>A refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public MeteringService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Metering");

        public async Task<List<MyUsageLine>> GetMyUsageAsync() =>
            await GetJsonAsync<List<MyUsageLine>>($"{ApiUrl}/mine") ?? new List<MyUsageLine>();

        public async Task<List<QuotaView>> GetQuotasAsync() =>
            await GetJsonAsync<List<QuotaView>>($"{ApiUrl}/quotas") ?? new List<QuotaView>();

        public async Task<List<UsageRecordView>> GetUsageAsync(string metric = null, int take = 200) =>
            await GetJsonAsync<List<UsageRecordView>>($"{ApiUrl}/usage?metric={Uri.EscapeDataString(metric ?? string.Empty)}&take={take}")
            ?? new List<UsageRecordView>();

        public Task<AiUsageDashboard> GetAiDashboardAsync(int days = 30) =>
            GetJsonAsync<AiUsageDashboard>($"{ApiUrl}/ai?days={days}");

        public async Task<List<AiUsageRecordView>> GetAiCallsAsync(int take = 200) =>
            await GetJsonAsync<List<AiUsageRecordView>>($"{ApiUrl}/ai/calls?take={take}") ?? new List<AiUsageRecordView>();

        public Task<Guid> DefineQuotaAsync(QuotaDefinition definition) =>
            PostJsonAsync<QuotaDefinition, Guid>($"{ApiUrl}/quotas", definition);

        public Task ResetQuotaAsync(Guid quotaId) =>
            PostAsync($"{ApiUrl}/quotas/{quotaId}/reset");

        public Task GrantOverrideAsync(Guid quotaId, QuotaOverrideRequest request) =>
            PostJsonAsync($"{ApiUrl}/quotas/{quotaId}/override", request);

        public Task SetEnforcementAsync(Guid quotaId, bool enforced) =>
            PostAsync($"{ApiUrl}/quotas/{quotaId}/enforcement/{enforced}");

        public Task DeleteQuotaAsync(Guid quotaId) =>
            DeleteAsync($"{ApiUrl}/quotas/{quotaId}");
    }
}
