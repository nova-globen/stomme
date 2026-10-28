using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Landing;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Landing.Services
{
    /// <summary>
    /// The landing service in the BROWSER (BL-98): <c>api/Landing</c> over HTTP. A refusal the server explains (a 400 with the
    /// domain's reason, a 409) is thrown with that reason, so the page can show it - the base class's helpers would return
    /// nothing and lose it.
    /// </summary>
    public class LandingService : ServiceBase, ILandingService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public LandingService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("Landing");

        public async Task<LandingPageView> GetAsync()
        {
            using var response = await GetHttpClient().GetAsync(ApiUrl); // 204: no page, or a draft this caller may not see
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<LandingPageView>() : null;
        }

        public async Task<PredefinedLandingContent> GetTemplateAsync() =>
            await GetJsonAsync<PredefinedLandingContent>($"{ApiUrl}/template");

        public async Task<LandingPageView> ConfigurePredefinedAsync(ConfigurePredefinedLandingRequest request)
        {
            using var response = await GetHttpClient().PutAsJsonAsync($"{ApiUrl}/predefined", request); // antiforgery rides GetHttpClient
            return await ReadOrThrowAsync(response);
        }

        public async Task<LandingPageView> ConfigureDashboardAsync(ConfigureDashboardLandingRequest request)
        {
            using var response = await GetHttpClient().PutAsJsonAsync($"{ApiUrl}/dashboard", request);
            return await ReadOrThrowAsync(response);
        }

        public async Task<LandingPageView> PublishAsync()
        {
            using var response = await GetHttpClient().PostAsync($"{ApiUrl}/publish", null);
            return await ReadOrThrowAsync(response);
        }

        public async Task<LandingPageView> UnpublishAsync()
        {
            using var response = await GetHttpClient().PostAsync($"{ApiUrl}/unpublish", null);
            return await ReadOrThrowAsync(response);
        }

        public async Task ResetAsync()
        {
            using var response = await GetHttpClient().DeleteAsync(ApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The landing page was not reset ({(int)response.StatusCode}).");
            }
        }

        public async Task<LandingDraftView> GenerateAsync(GenerateLandingRequest request)
        {
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/generate", request);
            return await ReadOrThrowAsync<LandingDraftView>(response);
        }

        public async Task<LandingDraftView> ProposeSectionAsync(ProposeLandingSectionRequest request)
        {
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/sections/propose", request);
            return await ReadOrThrowAsync<LandingDraftView>(response);
        }

        public async Task<LandingPageView> ConfigureGeneratedAsync(ConfigureGeneratedLandingRequest request)
        {
            using var response = await GetHttpClient().PutAsJsonAsync($"{ApiUrl}/generated", request);
            return await ReadOrThrowAsync(response);
        }

        private static Task<LandingPageView> ReadOrThrowAsync(HttpResponseMessage response) => ReadOrThrowAsync<LandingPageView>(response);

        // A refusal carries its reason: the domain's ({ error }), or the assistant's ({ code, message }) for an AI request.
        private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>();
            }

            string reason = null;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
                {
                    reason = error.GetString();
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var message))
                {
                    reason = message.GetString();
                }
            }
            catch (JsonException)
            {
                // No explanation in the body; the status says enough.
            }

            throw new InvalidOperationException(reason ?? $"The request was refused ({(int)response.StatusCode}).");
        }
    }
}
