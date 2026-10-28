using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Health;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Health.Services
{
    /// <summary>
    /// The client/server contract of the Health module's UI (D104 item 12). The controls depend on this interface,
    /// never on a repository: the browser binds it to <see cref="HealthService"/> (HTTP to <c>api/Health</c>,
    /// decided by the action's <c>[RequiresPermissionKey]</c> AND the request's <c>[Requires]</c>), and the server
    /// binds it to a mediator dispatch.
    /// </summary>
    /// <remarks>
    /// No method takes a site, a tenant or a user: all three are resolved server-side, so there is no parameter to
    /// forge. A denied or failed call answers null or an empty list, and the controls pre-check with
    /// <c>ModuleBase.IsAuthorizedPermission</c> so nobody is offered an action the server would refuse.
    /// </remarks>
    public interface IHealthService
    {
        Task<HealthDashboard> GetDashboardAsync();

        Task<List<HealthRollupView>> GetRollupsAsync(string window = "Hour", int take = 200);

        Task AcknowledgeAsync(Guid alertId);

        /// <summary>Runs this site's probes now (<c>health:manage</c>). Null when refused - including while a run is already in flight.</summary>
        Task<HealthRunNowResult> RunNowAsync();
    }

    /// <summary>The BROWSER side: HTTP to the module's controller. The server decides every call again.</summary>
    public class HealthService : ServiceBase, IHealthService, IClientService
    {
        public HealthService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Health");

        public Task<HealthDashboard> GetDashboardAsync() =>
            GetJsonAsync<HealthDashboard>(ApiUrl);

        public async Task<List<HealthRollupView>> GetRollupsAsync(string window = "Hour", int take = 200) =>
            await GetJsonAsync<List<HealthRollupView>>($"{ApiUrl}/rollups?window={Uri.EscapeDataString(window ?? "Hour")}&take={take}")
            ?? new List<HealthRollupView>();

        public Task AcknowledgeAsync(Guid alertId) =>
            PostAsync($"{ApiUrl}/alerts/{alertId}/acknowledge");

        public Task<HealthRunNowResult> RunNowAsync() =>
            PostJsonAsync<object, HealthRunNowResult>($"{ApiUrl}/run", new { });
    }
}
