using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Services
{
    /// <summary>
    /// The client/server contract of the Notifications module's UI (BL-78 step 5). The controls depend on this interface,
    /// never on a repository: the server binds it to <c>ServerNotificationsService</c> (a mediator dispatch, so every call is
    /// decided by the request's <c>[Requires]</c>), the browser to <see cref="NotificationsService"/> (HTTP to
    /// <c>api/Notifications</c>, decided by the action's <c>[RequiresPermissionKey]</c> AND the request's <c>[Requires]</c>).
    /// A denied or failed call answers null / an empty list / <see cref="Guid.Empty"/> - the controls pre-check with
    /// <c>ModuleBase.IsAuthorizedPermission</c> so a user never sees an action the server would refuse.
    /// </summary>
    /// <remarks>
    /// The inherited <see cref="INotificationService"/> (the framework's <c>Notification</c> rows - the UserProfile inbox and
    /// what the e-mail and in-app channels write) is a DIFFERENT contract and is unchanged; the notification center reads it.
    /// </remarks>
    public interface INotificationsService
    {
        Task<List<NotificationRequestSummary>> GetMyNotificationsAsync();

        Task<NotificationPreferencesView> GetMyPreferencesAsync();

        Task SaveMyPreferencesAsync(NotificationPreferencesView preferences);

        Task<List<NotificationRequestSummary>> GetRequestsAsync();

        Task<NotificationRequestDetail> GetRequestAsync(Guid requestId);

        Task RetryRequestAsync(Guid requestId);

        Task<Guid> SendTestAsync(NotificationSendTestInput input);

        Task<List<NotificationTemplateSummary>> GetTemplatesAsync();

        Task<NotificationTemplateDetail> GetTemplateAsync(Guid templateId);

        Task<Guid> SaveTemplateAsync(NotificationTemplateInput input);

        Task DeleteTemplateAsync(Guid templateId);

        Task<NotificationTemplatePreview> PreviewTemplateAsync(NotificationTemplatePreviewInput input);

        Task<List<NotificationChannelSummary>> GetChannelsAsync();

        Task<NotificationChannelDetail> GetChannelAsync(Guid channelId);

        Task<Guid> SaveChannelAsync(NotificationChannelInput input);

        Task SetChannelEnabledAsync(Guid channelId, bool enabled);

        Task SetChannelCredentialAsync(Guid channelId, NotificationChannelCredentialInput input);

        Task DeleteChannelAsync(Guid channelId);
    }

    /// <summary>The browser implementation: the module's Web API over HTTP (<c>api/Notifications</c>).</summary>
    public class NotificationsService : ServiceBase, INotificationsService, IClientService
    {
        public NotificationsService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Notifications");

        public async Task<List<NotificationRequestSummary>> GetMyNotificationsAsync() =>
            await GetJsonAsync<List<NotificationRequestSummary>>($"{ApiUrl}/mine") ?? new List<NotificationRequestSummary>();

        public Task<NotificationPreferencesView> GetMyPreferencesAsync() =>
            GetJsonAsync<NotificationPreferencesView>($"{ApiUrl}/preferences");

        public Task SaveMyPreferencesAsync(NotificationPreferencesView preferences) =>
            PutJsonAsync($"{ApiUrl}/preferences", preferences);

        public async Task<List<NotificationRequestSummary>> GetRequestsAsync() =>
            await GetJsonAsync<List<NotificationRequestSummary>>($"{ApiUrl}/log") ?? new List<NotificationRequestSummary>();

        public Task<NotificationRequestDetail> GetRequestAsync(Guid requestId) =>
            GetJsonAsync<NotificationRequestDetail>($"{ApiUrl}/log/{requestId}");

        public Task RetryRequestAsync(Guid requestId) =>
            PostAsync($"{ApiUrl}/log/{requestId}/retry");

        public Task<Guid> SendTestAsync(NotificationSendTestInput input) =>
            PostJsonAsync<NotificationSendTestInput, Guid>($"{ApiUrl}/send-test", input);

        public async Task<List<NotificationTemplateSummary>> GetTemplatesAsync() =>
            await GetJsonAsync<List<NotificationTemplateSummary>>($"{ApiUrl}/templates") ?? new List<NotificationTemplateSummary>();

        public Task<NotificationTemplateDetail> GetTemplateAsync(Guid templateId) =>
            GetJsonAsync<NotificationTemplateDetail>($"{ApiUrl}/templates/{templateId}");

        public Task<Guid> SaveTemplateAsync(NotificationTemplateInput input) =>
            PostJsonAsync<NotificationTemplateInput, Guid>($"{ApiUrl}/templates", input);

        public Task DeleteTemplateAsync(Guid templateId) =>
            DeleteAsync($"{ApiUrl}/templates/{templateId}");

        public Task<NotificationTemplatePreview> PreviewTemplateAsync(NotificationTemplatePreviewInput input) =>
            PostJsonAsync<NotificationTemplatePreviewInput, NotificationTemplatePreview>($"{ApiUrl}/templates/preview", input);

        public async Task<List<NotificationChannelSummary>> GetChannelsAsync() =>
            await GetJsonAsync<List<NotificationChannelSummary>>($"{ApiUrl}/channels") ?? new List<NotificationChannelSummary>();

        public Task<NotificationChannelDetail> GetChannelAsync(Guid channelId) =>
            GetJsonAsync<NotificationChannelDetail>($"{ApiUrl}/channels/{channelId}");

        public Task<Guid> SaveChannelAsync(NotificationChannelInput input) =>
            PostJsonAsync<NotificationChannelInput, Guid>($"{ApiUrl}/channels", input);

        public Task SetChannelEnabledAsync(Guid channelId, bool enabled) =>
            PostAsync($"{ApiUrl}/channels/{channelId}/{(enabled ? "enable" : "disable")}");

        public Task SetChannelCredentialAsync(Guid channelId, NotificationChannelCredentialInput input) =>
            PutJsonAsync($"{ApiUrl}/channels/{channelId}/credential", input);

        public Task DeleteChannelAsync(Guid channelId) =>
            DeleteAsync($"{ApiUrl}/channels/{channelId}");
    }
}