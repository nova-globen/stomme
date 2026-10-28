using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.AiAssistant.Services
{
    /// <summary>What the chat panel gets back from a call: the value, or the refusal to show in its place.</summary>
    public sealed record AssistantOutcome<T>(T Value, AssistantRefusalView Refusal)
    {
        public bool Succeeded => Refusal is null;
    }

    public interface IAiAssistantService
    {
        Task<AssistantOutcome<List<AssistantConversationSummary>>> ListAsync();

        Task<AssistantOutcome<AssistantConversationView>> StartAsync();

        Task<AssistantOutcome<AssistantConversationView>> GetAsync(Guid conversationId);

        Task<AssistantOutcome<AssistantConversationView>> SendAsync(Guid conversationId, string message);

        Task<AssistantOutcome<AssistantArtifactApplyResult>> ApplyAsync(Guid conversationId, Guid artifactId);

        Task<AssistantOutcome<bool>> DiscardAsync(Guid conversationId, Guid artifactId);

        Task<AssistantOutcome<AssistantSettingsView>> GetSettingsAsync();

        Task<AssistantOutcome<AssistantSettingsView>> SaveSettingsAsync(SaveAssistantSettingsRequest request);
    }

    /// <summary>
    /// The chat panel's client over <c>api/AiAssistant</c> (BL-97 increment 10b). A refusal (400, 403, 409, 429, 503) carries its
    /// code and a message written to be shown, so the panel reads the body rather than treating the status as an exception.
    /// </summary>
    public class AiAssistantService : ServiceBase, IAiAssistantService, IClientService
    {
        public AiAssistantService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("AiAssistant");

        public Task<AssistantOutcome<List<AssistantConversationSummary>>> ListAsync() =>
            SendAsync<List<AssistantConversationSummary>>(HttpMethod.Get, $"{ApiUrl}/conversations", null);

        public Task<AssistantOutcome<AssistantConversationView>> StartAsync() =>
            SendAsync<AssistantConversationView>(HttpMethod.Post, $"{ApiUrl}/conversations", new { });

        public Task<AssistantOutcome<AssistantConversationView>> GetAsync(Guid conversationId) =>
            SendAsync<AssistantConversationView>(HttpMethod.Get, $"{ApiUrl}/conversations/{conversationId}", null);

        public Task<AssistantOutcome<AssistantConversationView>> SendAsync(Guid conversationId, string message) =>
            SendAsync<AssistantConversationView>(HttpMethod.Post, $"{ApiUrl}/conversations/{conversationId}/messages", new SendAssistantMessageRequest(message));

        public Task<AssistantOutcome<AssistantArtifactApplyResult>> ApplyAsync(Guid conversationId, Guid artifactId) =>
            SendAsync<AssistantArtifactApplyResult>(HttpMethod.Post, $"{ApiUrl}/conversations/{conversationId}/artifacts/{artifactId}/apply", new { });

        public async Task<AssistantOutcome<bool>> DiscardAsync(Guid conversationId, Guid artifactId)
        {
            var outcome = await SendAsync<object>(HttpMethod.Post, $"{ApiUrl}/conversations/{conversationId}/artifacts/{artifactId}/discard", new { });
            return new AssistantOutcome<bool>(outcome.Succeeded, outcome.Refusal);
        }

        public Task<AssistantOutcome<AssistantSettingsView>> GetSettingsAsync() =>
            SendAsync<AssistantSettingsView>(HttpMethod.Get, $"{ApiUrl}/settings", null);

        public Task<AssistantOutcome<AssistantSettingsView>> SaveSettingsAsync(SaveAssistantSettingsRequest request) =>
            SendAsync<AssistantSettingsView>(HttpMethod.Put, $"{ApiUrl}/settings", request);

        private async Task<AssistantOutcome<T>> SendAsync<T>(HttpMethod method, string url, object body)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            using var response = await GetHttpClient().SendAsync(request); // the antiforgery header rides GetHttpClient
            if (response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)
                    ? new AssistantOutcome<T>(default, null)
                    : new AssistantOutcome<T>(await response.Content.ReadFromJsonAsync<T>(), null);
            }

            AssistantRefusalView refusal = null;
            try
            {
                refusal = await response.Content.ReadFromJsonAsync<AssistantRefusalView>();
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a refusal body (a 404, a validation problem); described by its status below.
            }
            catch (NotSupportedException)
            {
                // No JSON content at all.
            }

            return new AssistantOutcome<T>(default, refusal?.Code is not null ? refusal : new AssistantRefusalView(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), Describe(response.StatusCode)));
        }

        private static string Describe(HttpStatusCode status) => status switch
        {
            HttpStatusCode.NotFound => "That conversation is not available.",
            HttpStatusCode.Forbidden => "You do not have access to that.",
            HttpStatusCode.BadRequest => "The request was not accepted.",
            _ => "The assistant could not complete the request.",
        };
    }
}
