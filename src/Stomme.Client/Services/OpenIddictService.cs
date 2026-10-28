using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Services
{
    /// <summary>
    /// Service to manage the per-tenant OpenIddict authority's relying parties (<see cref="OpenIddictApplicationSeed"/>)
    /// and custom scopes (<see cref="OpenIddictScopeSeed"/>) — the runtime counterpart to the declarative
    /// <c>OpenIddict:Applications</c>/<c>OpenIddict:Scopes</c> config seeder.
    /// </summary>
    /// <remarks>
    /// Every operation is <b>Host-only</b> server-side: minting OAuth credentials against a tenant's authority is
    /// a host-level act. When the authority is switched off (<c>OpenIddict:Enabled</c> unset) every action but
    /// <see cref="GetStatusAsync"/> answers 404 — which surfaces here as <c>null</c>/an empty list, so callers
    /// should ask <see cref="GetStatusAsync"/> first rather than reading an empty list as "no clients".
    /// </remarks>
    public interface IOpenIddictService
    {
        /// <summary>
        /// Returns whether the authority is enabled for this install, plus the current tenant's endpoint
        /// addresses. Answers even when disabled — the one call that distinguishes off from missing.
        /// </summary>
        Task<OpenIddictStatus> GetStatusAsync();

        /// <summary>Returns every registered client application for the current tenant (no secrets).</summary>
        Task<List<OpenIddictApplicationSummary>> GetApplicationsAsync();

        /// <summary>Returns the given client application, or null when it does not exist.</summary>
        Task<OpenIddictApplicationSummary> GetApplicationAsync(string clientId);

        /// <summary>
        /// Creates or updates the given client application (idempotent upsert, keyed on ClientId). On an update
        /// an empty <see cref="OpenIddictApplicationSeed.ClientSecret"/> KEEPS the stored secret; supplying one
        /// rotates it.
        /// </summary>
        Task<OpenIddictApplicationSummary> SaveApplicationAsync(OpenIddictApplicationSeed application);

        /// <summary>Deletes the given client application.</summary>
        Task DeleteApplicationAsync(string clientId);

        /// <summary>Returns every custom scope registered for the current tenant.</summary>
        Task<List<OpenIddictScopeSeed>> GetScopesAsync();

        /// <summary>Creates or updates the given scope (idempotent upsert, keyed on Name).</summary>
        Task<OpenIddictScopeSeed> SaveScopeAsync(OpenIddictScopeSeed scope);

        /// <summary>Deletes the given scope.</summary>
        Task DeleteScopeAsync(string name);

        /// <summary>BL-224: the application's service account on this site; null when service accounts are off or the client is unknown.</summary>
        Task<OpenIddictServiceAccount> GetServiceAccountAsync(string clientId);

        /// <summary>BL-224: creates the application's service account; a refusal throws with the server's reason.</summary>
        Task<OpenIddictServiceAccount> CreateServiceAccountAsync(string clientId);

        /// <summary>BL-224: deletes the application's service account; its tokens sign no one in again.</summary>
        Task DeleteServiceAccountAsync(string clientId);
    }

    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class OpenIddictService : ServiceBase, IOpenIddictService
    {
        public OpenIddictService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string Apiurl => CreateApiUrl("OpenIddict");

        public async Task<OpenIddictStatus> GetStatusAsync()
        {
            return await GetJsonAsync<OpenIddictStatus>($"{Apiurl}/status");
        }

        public async Task<List<OpenIddictApplicationSummary>> GetApplicationsAsync()
        {
            return await GetJsonAsync<List<OpenIddictApplicationSummary>>($"{Apiurl}/applications");
        }

        public async Task<OpenIddictApplicationSummary> GetApplicationAsync(string clientId)
        {
            return await GetJsonAsync<OpenIddictApplicationSummary>($"{Apiurl}/applications/{WebUtility.UrlEncode(clientId)}");
        }

        public async Task<OpenIddictApplicationSummary> SaveApplicationAsync(OpenIddictApplicationSeed application)
        {
            return await PostJsonAsync<OpenIddictApplicationSeed, OpenIddictApplicationSummary>($"{Apiurl}/applications", application);
        }

        public async Task DeleteApplicationAsync(string clientId)
        {
            await DeleteAsync($"{Apiurl}/applications/{WebUtility.UrlEncode(clientId)}");
        }

        public async Task<List<OpenIddictScopeSeed>> GetScopesAsync()
        {
            return await GetJsonAsync<List<OpenIddictScopeSeed>>($"{Apiurl}/scopes");
        }

        public async Task<OpenIddictScopeSeed> SaveScopeAsync(OpenIddictScopeSeed scope)
        {
            return await PostJsonAsync<OpenIddictScopeSeed>($"{Apiurl}/scopes", scope);
        }

        public async Task DeleteScopeAsync(string name)
        {
            await DeleteAsync($"{Apiurl}/scopes/{WebUtility.UrlEncode(name)}");
        }

        public async Task<OpenIddictServiceAccount> GetServiceAccountAsync(string clientId)
        {
            return await GetJsonAsync<OpenIddictServiceAccount>($"{Apiurl}/applications/{WebUtility.UrlEncode(clientId)}/service-account");
        }

        public async Task<OpenIddictServiceAccount> CreateServiceAccountAsync(string clientId)
        {
            using var response = await GetHttpClient().PostAsync($"{Apiurl}/applications/{WebUtility.UrlEncode(clientId)}/service-account", null);
            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<OpenIddictServiceAccount>();
        }

        public async Task DeleteServiceAccountAsync(string clientId)
        {
            await DeleteAsync($"{Apiurl}/applications/{WebUtility.UrlEncode(clientId)}/service-account");
        }
    }
}
