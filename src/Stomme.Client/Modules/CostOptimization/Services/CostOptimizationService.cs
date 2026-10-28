using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.CostOptimization.Services
{
    public interface ICostOptimizationService
    {
        Task<List<RecommendationView>> ListAsync();

        Task<AdvisorRunView> RunAdvisorAsync();

        Task<RecommendationDecisionView> ApplyAsync(Guid recommendationId, ApplyRecommendationRequest request);

        Task<RecommendationDecisionView> DismissAsync(Guid recommendationId, DismissRecommendationRequest request);
    }

    /// <summary>
    /// The page's client over <c>api/CostOptimization</c> (BL-97 increment 6). A refused decision (404, 409, 400) still carries
    /// its code and reason in the body, so the page reads the body rather than treating the status as an exception.
    /// </summary>
    public class CostOptimizationService : ServiceBase, ICostOptimizationService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public CostOptimizationService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("CostOptimization");

        public async Task<List<RecommendationView>> ListAsync() =>
            await GetJsonAsync<List<RecommendationView>>(ApiUrl) ?? new List<RecommendationView>();

        public async Task<AdvisorRunView> RunAdvisorAsync() =>
            await PostJsonAsync<object, AdvisorRunView>($"{ApiUrl}/run", new object());

        public Task<RecommendationDecisionView> ApplyAsync(Guid recommendationId, ApplyRecommendationRequest request) =>
            DecideAsync($"{ApiUrl}/{recommendationId}/apply", request);

        public Task<RecommendationDecisionView> DismissAsync(Guid recommendationId, DismissRecommendationRequest request) =>
            DecideAsync($"{ApiUrl}/{recommendationId}/dismiss", request);

        private async Task<RecommendationDecisionView> DecideAsync<TRequest>(string url, TRequest request)
        {
            using var response = await GetHttpClient().PostAsJsonAsync(url, request); // the antiforgery header rides GetHttpClient
            RecommendationDecisionView decision = null;
            try
            {
                decision = await response.Content.ReadFromJsonAsync<RecommendationDecisionView>();
            }
            catch (System.Text.Json.JsonException)
            {
                // A validation problem is not a decision; it is reported below by its status.
            }

            return decision is { Success: true } || decision?.Code is not null
                ? decision
                : RecommendationDecisionView.Refused("invalid", $"The request was refused ({(int)response.StatusCode}).");
        }
    }
}
