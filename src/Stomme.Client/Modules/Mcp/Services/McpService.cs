using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Mcp;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Mcp.Services
{
    /// <summary>
    /// The client/server contract of the MCP module's UI. The controls depend on this interface,
    /// never on a repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is ONE implementation, and it is HTTP.</b> Every other module here ships a server-side twin so that an
    /// interactive-SERVER circuit dispatches in-process; this one does not, on purpose. The by-id authorization that
    /// matters - a registration belongs to a SITE - lives in <c>McpRegistrationService</c> behind the controller, and a
    /// second implementation would be a second path to it that the driven suites do not cover. When a server twin is
    /// added it must go through the same service, and <c>McpSiteIsolationTests</c> is where it earns its place.
    /// </para>
    /// <para>
    /// A denied or failed call answers null or an empty list; the controls pre-check with
    /// <c>ModuleBase.IsAuthorizedPermission</c>, so a user is not offered an action the server would refuse - and the
    /// server decides again per request regardless.
    /// </para>
    /// </remarks>
    public interface IMcpService
    {
        Task<List<McpServerRegistrationView>> GetServersAsync();

        Task<McpServerRegistrationView> RegisterServerAsync(McpServerRegistrationRequest request);

        Task RefreshServerAsync(Guid id);

        Task SetServerEnabledAsync(Guid id, bool enabled);

        Task RemoveServerAsync(Guid id);

        Task SetApiKeyAsync(Guid id, string apiKey);

        Task<List<McpPublishedItemView>> GetPublishedAsync();

        Task<List<McpAuditEntryView>> GetAuditAsync(int days);
    }

    /// <summary>The browser implementation: the module's Web API over HTTP (<c>api/Mcp</c>).</summary>
    public class McpService : ServiceBase, IMcpService, IClientService
    {
        /// <summary>A refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public McpService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Mcp");

        public async Task<List<McpServerRegistrationView>> GetServersAsync() =>
            await GetJsonAsync<List<McpServerRegistrationView>>($"{ApiUrl}/servers") ?? new List<McpServerRegistrationView>();

        public Task<McpServerRegistrationView> RegisterServerAsync(McpServerRegistrationRequest request) =>
            PostJsonAsync<McpServerRegistrationRequest, McpServerRegistrationView>($"{ApiUrl}/servers", request);

        public Task RefreshServerAsync(Guid id) => PostAsync($"{ApiUrl}/servers/{id}/refresh");

        public Task SetServerEnabledAsync(Guid id, bool enabled) =>
            PostAsync($"{ApiUrl}/servers/{id}/{(enabled ? "enable" : "disable")}");

        public Task RemoveServerAsync(Guid id) => DeleteAsync($"{ApiUrl}/servers/{id}");

        // The key travels ONE way. There is no read, here or on the server: the only thing anything will say about a
        // stored key is whether it exists (McpServerRegistrationView.HasApiKey).
        public Task SetApiKeyAsync(Guid id, string apiKey) =>
            PutJsonAsync($"{ApiUrl}/servers/{id}/apikey", new McpApiKeyRequest { ApiKey = apiKey });

        public async Task<List<McpPublishedItemView>> GetPublishedAsync() =>
            await GetJsonAsync<List<McpPublishedItemView>>($"{ApiUrl}/published") ?? new List<McpPublishedItemView>();

        public async Task<List<McpAuditEntryView>> GetAuditAsync(int days) =>
            await GetJsonAsync<List<McpAuditEntryView>>($"{ApiUrl}/audit?days={days}") ?? new List<McpAuditEntryView>();
    }
}
