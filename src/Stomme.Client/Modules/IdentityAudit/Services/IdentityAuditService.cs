using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.IdentityAudit;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.IdentityAudit.Services
{
    /// <summary>
    /// The identity audit page's contract (BL-120). Administrators only - decided by the server on every call - and no
    /// method takes a site: the site is the request's own.
    /// </summary>
    public interface IIdentityAuditService
    {
        /// <summary>This site's entries of the last <paramref name="days"/> days, newest first. Empty when refused.</summary>
        Task<List<IdentityAuditEntryView>> GetRecentAsync(int days = 30, int take = 500);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/IdentityAudit</c>.</summary>
    public class IdentityAuditService : ServiceBase, IIdentityAuditService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public IdentityAuditService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("IdentityAudit");

        public async Task<List<IdentityAuditEntryView>> GetRecentAsync(int days = 30, int take = 500) =>
            await GetJsonAsync<List<IdentityAuditEntryView>>($"{ApiUrl}?days={days}&take={take}") ?? new List<IdentityAuditEntryView>();
    }
}
