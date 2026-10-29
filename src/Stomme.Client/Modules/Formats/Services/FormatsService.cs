using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Formats;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Formats.Services
{
    /// <summary>
    /// The Formats module's client contract. No method takes a site: the request is the site, and the
    /// server decides every call again on its key. A refused save comes back as an exception the page shows.
    /// </summary>
    public interface IFormatsService
    {
        Task<List<FormatDescriptor>> ListAsync(FormatKind? kind = null, FormatScope? scope = null);

        Task<FormatDescriptor> GetAsync(Guid id);

        Task<FormatDescriptor> SaveAsync(FormatDefinitionRequest request);

        Task DeleteAsync(Guid id);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Formats</c>.</summary>
    public class FormatsService : ServiceBase, IFormatsService, IClientService
    {
        /// <summary>A refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public FormatsService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Formats");

        public async Task<List<FormatDescriptor>> ListAsync(FormatKind? kind = null, FormatScope? scope = null)
        {
            var query = new List<string>();
            if (kind is { } k)
            {
                query.Add("kind=" + k);
            }

            if (scope is { } s)
            {
                query.Add("scope=" + s);
            }

            var url = query.Count == 0 ? ApiUrl : ApiUrl + "?" + string.Join("&", query);
            return await GetJsonAsync<List<FormatDescriptor>>(url) ?? new List<FormatDescriptor>();
        }

        public Task<FormatDescriptor> GetAsync(Guid id) => GetJsonAsync<FormatDescriptor>($"{ApiUrl}/{id}");

        public Task<FormatDescriptor> SaveAsync(FormatDefinitionRequest request) =>
            PostJsonAsync<FormatDefinitionRequest, FormatDescriptor>(ApiUrl, request);

        public Task DeleteAsync(Guid id) => DeleteAsync($"{ApiUrl}/{id}");
    }
}
