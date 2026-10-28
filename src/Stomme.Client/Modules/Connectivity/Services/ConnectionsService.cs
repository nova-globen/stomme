using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Connectivity.Services
{
    /// <summary>
    /// The Connectivity module's client contract (BL-94 increment 7). No method takes a site: the request is the site,
    /// and the server decides every call again on its key. A refused save comes back as an exception the page shows; a
    /// test comes back as a category, never the connector's words; no call returns a credential.
    /// </summary>
    public interface IConnectionsService
    {
        Task<List<ConnectionView>> ListAsync();

        Task<ConnectionView> GetAsync(Guid id);

        Task<List<ConnectorView>> ListConnectorsAsync();

        Task<ConnectionView> CreateAsync(ConnectionRequest request);

        Task<ConnectionView> UpdateAsync(Guid id, ConnectionRequest request);

        Task<ConnectionTestView> TestAsync(Guid id);

        Task DeleteAsync(Guid id);

        /// <summary>The private hosts this site may reach (BL-180), and whether the caller may change them - the Host role alone.</summary>
        Task<ConnectionEgressView> GetEgressAsync();

        Task<ConnectionEgressView> SaveEgressAsync(string allowedHosts);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Connections</c>.</summary>
    public class ConnectionsService : ServiceBase, IConnectionsService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public ConnectionsService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Connections");

        public async Task<List<ConnectionView>> ListAsync() => await GetJsonAsync<List<ConnectionView>>(ApiUrl) ?? new List<ConnectionView>();

        public Task<ConnectionView> GetAsync(Guid id) => GetJsonAsync<ConnectionView>($"{ApiUrl}/{id}");

        public async Task<List<ConnectorView>> ListConnectorsAsync() => await GetJsonAsync<List<ConnectorView>>($"{ApiUrl}/connectors") ?? new List<ConnectorView>();

        public Task<ConnectionView> CreateAsync(ConnectionRequest request) => PostJsonAsync<ConnectionRequest, ConnectionView>(ApiUrl, request);

        public Task<ConnectionView> UpdateAsync(Guid id, ConnectionRequest request) => PutJsonAsync<ConnectionRequest, ConnectionView>($"{ApiUrl}/{id}", request);

        public Task<ConnectionTestView> TestAsync(Guid id) => PostJsonAsync<object, ConnectionTestView>($"{ApiUrl}/{id}/test", new { });

        public Task DeleteAsync(Guid id) => DeleteAsync($"{ApiUrl}/{id}");

        public Task<ConnectionEgressView> GetEgressAsync() => GetJsonAsync<ConnectionEgressView>($"{ApiUrl}/egress");

        public Task<ConnectionEgressView> SaveEgressAsync(string allowedHosts) =>
            PutJsonAsync<ConnectionEgressRequest, ConnectionEgressView>($"{ApiUrl}/egress", new ConnectionEgressRequest { AllowedHosts = allowedHosts ?? string.Empty });
    }
}
