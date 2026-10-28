using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Collaboration.Services
{
    /// <summary>What the overview gets back: the value, or the refusal to show in its place.</summary>
    public sealed record CollaborationOutcome<T>(T Value, CollaborationRefusalView Refusal)
    {
        public bool Succeeded => Refusal is null;
    }

    public interface ICollaborationService
    {
        Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync();

        Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request);

        Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId);

        // ---- an editor's calls (increment 12): the panel the pipeline editor and the dashboard builder host ----------------

        /// <summary>Opens or joins the resource's session; a refusal (switched off, not editable) means: no collaboration here.</summary>
        Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId);

        /// <summary>The session as it is now - re-read after a notice.</summary>
        Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId);

        /// <summary>Where the caller is editing: a step, a widget.</summary>
        Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion);

        /// <summary>Announces a change the caller made - refused unless they hold a live lock on the resource.</summary>
        Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor);

        Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId);

        /// <summary>The editor is still here - its presence lives on this heartbeat.</summary>
        Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId);

        /// <summary>Takes the lock, or is told who holds it - never an error.</summary>
        Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request);

        /// <summary>The heartbeat; the value tells whether the caller still holds it (null: no lock at all).</summary>
        Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request);

        Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request);

        /// <summary>Who holds the resource now; a null value when nobody does.</summary>
        Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId);
    }

    /// <summary>
    /// The overview's client over <c>api/Collaboration</c> (BL-97 increment 11). A refusal carries its code and a message
    /// written to be shown, so the page reads the body rather than treating the status as an exception.
    /// </summary>
    public class CollaborationService : ServiceBase, ICollaborationService, IClientService
    {
        public CollaborationService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("Collaboration");

        public async Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync()
        {
            using var response = await GetHttpClient().GetAsync($"{ApiUrl}/overview");
            return response.IsSuccessStatusCode
                ? new CollaborationOutcome<CollaborationOverviewView>(await response.Content.ReadFromJsonAsync<CollaborationOverviewView>(), null)
                : new CollaborationOutcome<CollaborationOverviewView>(null, await RefusalAsync(response));
        }

        public async Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request)
        {
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/locks/take-over", request); // the antiforgery header rides GetHttpClient
            return response.IsSuccessStatusCode ? new CollaborationOutcome<bool>(true, null) : new CollaborationOutcome<bool>(false, await RefusalAsync(response));
        }

        public async Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId)
        {
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/sessions/{sessionId}/close", new { });
            return response.IsSuccessStatusCode ? new CollaborationOutcome<bool>(true, null) : new CollaborationOutcome<bool>(false, await RefusalAsync(response));
        }

        public Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId) =>
            SendAsync<EditSessionView>(HttpMethod.Post, $"{ApiUrl}/sessions", new JoinSessionRequest(resourceType, resourceId));

        public Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId) =>
            SendAsync<EditSessionView>(HttpMethod.Get, $"{ApiUrl}/sessions/{sessionId}", null);

        public async Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion) =>
            Done(await SendAsync<object>(HttpMethod.Post, $"{ApiUrl}/sessions/{sessionId}/presence", new PresenceRequest(activeRegion, null)));

        public Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor) =>
            SendAsync<EditSessionView>(HttpMethod.Post, $"{ApiUrl}/sessions/{sessionId}/changes", new ChangeRequest(descriptor));

        public async Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId) =>
            Done(await SendAsync<object>(HttpMethod.Post, $"{ApiUrl}/sessions/{sessionId}/leave", new { }));

        public async Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId) =>
            Done(await SendAsync<object>(HttpMethod.Post, $"{ApiUrl}/sessions/{sessionId}/heartbeat", new { }));

        public Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request) =>
            SendAsync<ResourceLockResult>(HttpMethod.Post, $"{ApiUrl}/locks/acquire", request);

        public Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request) =>
            SendAsync<ResourceLockResult>(HttpMethod.Post, $"{ApiUrl}/locks/renew", request, notFoundIsEmpty: true);

        public async Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request) =>
            Done(await SendAsync<object>(HttpMethod.Post, $"{ApiUrl}/locks/release", request));

        public Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId) =>
            SendAsync<ResourceLockResult>(HttpMethod.Get, $"{ApiUrl}/locks?resourceType={Uri.EscapeDataString(resourceType)}&resourceId={Uri.EscapeDataString(resourceId)}&scope={LockScope.Resource}", null);

        // 204 is an answer with no value (nobody holds it, nothing to return); a refusal carries its code and message.
        private async Task<CollaborationOutcome<T>> SendAsync<T>(HttpMethod method, string url, object body, bool notFoundIsEmpty = false)
        {
            using var message = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                message.Content = JsonContent.Create(body, body.GetType());
            }

            using var response = await GetHttpClient().SendAsync(message); // the antiforgery header rides GetHttpClient
            if (response.StatusCode == HttpStatusCode.NoContent || (notFoundIsEmpty && response.StatusCode == HttpStatusCode.NotFound))
            {
                return new CollaborationOutcome<T>(default, null);
            }

            return response.IsSuccessStatusCode
                ? new CollaborationOutcome<T>(typeof(T) == typeof(object) ? default : await response.Content.ReadFromJsonAsync<T>(), null)
                : new CollaborationOutcome<T>(default, await RefusalAsync(response));
        }

        private static CollaborationOutcome<bool> Done<T>(CollaborationOutcome<T> outcome) => new(outcome.Succeeded, outcome.Refusal);

        private static async Task<CollaborationRefusalView> RefusalAsync(HttpResponseMessage response)
        {
            try
            {
                if (await response.Content.ReadFromJsonAsync<CollaborationRefusalView>() is { Code: not null } refusal)
                {
                    return refusal;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a refusal body; described by its status below.
            }
            catch (NotSupportedException)
            {
                // No JSON content at all.
            }

            return new CollaborationRefusalView(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "That is no longer there.",
                    HttpStatusCode.Forbidden => "You do not have access to that.",
                    _ => "The request could not be completed.",
                });
        }
    }
}
