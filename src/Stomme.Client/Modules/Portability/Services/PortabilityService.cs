using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Portability;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Portability.Services
{
    /// <summary>
    /// The portability contract (BL-82). No method takes a site or a requester: the request is the site, the caller is
    /// the requester, and the server decides every call again.
    /// </summary>
    public interface IPortabilityService
    {
        /// <summary>Whether portability is switched on here (<c>Portability:Enabled</c>) - so a page can stay out of the way when it is not.</summary>
        Task<bool> IsEnabledAsync();

        /// <summary>Every package on the site for a view holder; otherwise the caller's own and those about them.</summary>
        Task<List<PortabilityPackageView>> ListAsync();

        Task<PortabilityPackageView> RequestConfigAsync(PortabilityConfigRequest request);

        Task<PortabilityPackageView> RequestSubjectAsync(PortabilitySubjectRequest request);

        /// <summary>The caller's OWN data package.</summary>
        Task<PortabilityPackageView> RequestMineAsync();

        Task<PortabilityImportResult> ImportAsync(string json, ConflictStrategy strategy, CancellationToken ct = default);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Portability</c>. A download is a plain link to <c>api/Portability/{id}/download</c>.</summary>
    public class PortabilityService : ServiceBase, IPortabilityService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public PortabilityService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Portability");

        /// <summary>The API answers 404 on every route while switched off; anything else means on.</summary>
        public async Task<bool> IsEnabledAsync()
        {
            using var response = await GetHttpClient().GetAsync(ApiUrl);
            return response.StatusCode != System.Net.HttpStatusCode.NotFound;
        }

        public async Task<List<PortabilityPackageView>> ListAsync() =>
            await GetJsonAsync<List<PortabilityPackageView>>(ApiUrl) ?? new List<PortabilityPackageView>();

        public async Task<PortabilityPackageView> RequestConfigAsync(PortabilityConfigRequest request) =>
            await PostJsonAsync<PortabilityConfigRequest, PortabilityPackageView>($"{ApiUrl}/config", request);

        public async Task<PortabilityPackageView> RequestSubjectAsync(PortabilitySubjectRequest request) =>
            await PostJsonAsync<PortabilitySubjectRequest, PortabilityPackageView>($"{ApiUrl}/subject", request);

        public async Task<PortabilityPackageView> RequestMineAsync() =>
            await PostJsonAsync<object, PortabilityPackageView>($"{ApiUrl}/mine", new { });

        public async Task<PortabilityImportResult> ImportAsync(string json, ConflictStrategy strategy, CancellationToken ct = default)
        {
            using var content = new StringContent(json ?? string.Empty, System.Text.Encoding.UTF8, "application/json");
            using var response = await GetHttpClient().PostAsync($"{ApiUrl}/import?strategy={strategy}", content, ct);
            return response.IsSuccessStatusCode
                ? await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<PortabilityImportResult>(response.Content, cancellationToken: ct) ?? new PortabilityImportResult()
                : new PortabilityImportResult { Refusals = new List<string> { $"The server refused the import ({(int)response.StatusCode})." } };
        }

        public string DownloadUrl(PortabilityPackageView package) => $"{ApiUrl}/{package.Id}/download";
    }
}
