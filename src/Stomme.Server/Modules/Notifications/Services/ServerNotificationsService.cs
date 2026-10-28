using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Services
{
    /// <summary>
    /// The SERVER side of <see cref="INotificationsService"/> (BL-78 step 5): every call is a mediator dispatch, so each is
    /// decided by its request's <c>[Requires]</c> (the <c>AuthorizationBehavior</c>, over the request's <c>HttpContext</c>).
    /// Registered <c>AddTransient</c> in the module's <c>ServerStartup</c> so it wins over the client HTTP implementation on
    /// the server (the Guestbook / HtmlText pattern).
    /// </summary>
    /// <remarks>
    /// UNMEASURED residue: <c>PermissionRowAccessGuard</c> denies when <c>IHttpContextAccessor.HttpContext</c> is null, and
    /// whether it is populated inside a live interactive-SERVER circuit has not been driven here (the BL-99 gate states the
    /// same limit). The framework's own server services (HtmlText) read <c>HttpContext.User</c> on this path too.
    /// </remarks>
    public sealed class ServerNotificationsService(IMediator mediator) : INotificationsService, ITransientService
    {
        public async Task<List<NotificationRequestSummary>> GetMyNotificationsAsync() =>
            (await mediator.Send(new ListMyNotificationsQuery())).ToList();

        public Task<NotificationPreferencesView> GetMyPreferencesAsync() =>
            mediator.Send(new GetMyNotificationPreferencesQuery());

        public Task SaveMyPreferencesAsync(NotificationPreferencesView preferences) =>
            mediator.Send(new SaveMyNotificationPreferencesCommand(
                preferences.AcceptedChannels ?? Array.Empty<NotificationChannelKind>(),
                preferences.SeverityFilter,
                preferences.MutedTopics ?? Array.Empty<string>(),
                preferences.QuietHours,
                preferences.Digest,
                preferences.PreferredLocale));

        public async Task<List<NotificationRequestSummary>> GetRequestsAsync() =>
            (await mediator.Send(new ListNotificationRequestsQuery())).ToList();

        public Task<NotificationRequestDetail> GetRequestAsync(Guid requestId) =>
            mediator.Send(new GetNotificationRequestQuery(requestId));

        public Task RetryRequestAsync(Guid requestId) =>
            mediator.Send(new RetryNotificationRequestCommand(requestId));

        public Task<Guid> SendTestAsync(NotificationSendTestInput input) =>
            mediator.Send(new SendTestNotificationCommand(input.Channels ?? Array.Empty<NotificationChannelKind>(), input.Subject, input.Body));

        public async Task<List<NotificationTemplateSummary>> GetTemplatesAsync() =>
            (await mediator.Send(new ListNotificationTemplatesQuery())).ToList();

        public Task<NotificationTemplateDetail> GetTemplateAsync(Guid templateId) =>
            mediator.Send(new GetNotificationTemplateQuery(templateId));

        public Task<Guid> SaveTemplateAsync(NotificationTemplateInput input) =>
            mediator.Send(new SaveNotificationTemplateCommand(input.Id, input.Key, input.SupportedChannels, input.Contents, input.Placeholders));

        public Task DeleteTemplateAsync(Guid templateId) =>
            mediator.Send(new DeleteNotificationTemplateCommand(templateId));

        public Task<NotificationTemplatePreview> PreviewTemplateAsync(NotificationTemplatePreviewInput input) =>
            mediator.Send(new PreviewNotificationTemplateQuery(input.Id, input.LocaleCode, input.Parameters));

        public async Task<List<NotificationChannelSummary>> GetChannelsAsync() =>
            (await mediator.Send(new ListNotificationChannelsQuery())).ToList();

        public Task<NotificationChannelDetail> GetChannelAsync(Guid channelId) =>
            mediator.Send(new GetNotificationChannelQuery(channelId));

        public Task<Guid> SaveChannelAsync(NotificationChannelInput input) =>
            mediator.Send(new ConfigureChannelCommand(input.Id, input.Kind, input.Name, input.Endpoint, input.Secret, input.Options, input.RateLimit));

        public Task SetChannelEnabledAsync(Guid channelId, bool enabled) =>
            enabled ? mediator.Send(new EnableChannelCommand(channelId)) : mediator.Send(new DisableChannelCommand(channelId));

        public Task SetChannelCredentialAsync(Guid channelId, NotificationChannelCredentialInput input) =>
            mediator.Send(new SetChannelCredentialCommand(channelId, input?.Secret));

        public Task DeleteChannelAsync(Guid channelId) =>
            mediator.Send(new DeleteChannelCommand(channelId));
    }
}