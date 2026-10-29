using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Sessions;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Sessions.Services
{
    /// <summary>
    /// The sessions contract. No method takes a user or a site: the caller is the user, the request is the site,
    /// and the server decides every call again.
    /// </summary>
    public interface ISessionService
    {
        /// <summary>The caller's own active sessions on this site. Empty when signed out.</summary>
        Task<List<UserSessionView>> GetMineAsync();

        /// <summary>Ends one of the caller's OWN sessions.</summary>
        Task<bool> RevokeAsync(Guid sessionId);

        /// <summary>Every active session on this site - Administrators only; empty when refused.</summary>
        Task<List<UserSessionView>> GetSiteAsync(int take);

        /// <summary>Ends any session on this site - Administrators only, and a Host's only by a Host.</summary>
        Task<bool> RevokeSiteSessionAsync(Guid sessionId);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Session</c>.</summary>
    public class SessionService : ServiceBase, ISessionService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public SessionService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Session");

        public async Task<List<UserSessionView>> GetMineAsync() =>
            await GetJsonAsync<List<UserSessionView>>(ApiUrl) ?? new List<UserSessionView>();

        public async Task<bool> RevokeAsync(Guid sessionId)
        {
            await DeleteAsync($"{ApiUrl}/{sessionId}");
            return true;
        }

        public async Task<List<UserSessionView>> GetSiteAsync(int take) =>
            await GetJsonAsync<List<UserSessionView>>($"{ApiUrl}/site?take={take}") ?? new List<UserSessionView>();

        public async Task<bool> RevokeSiteSessionAsync(Guid sessionId)
        {
            await DeleteAsync($"{ApiUrl}/site/{sessionId}");
            return true;
        }
    }
}
