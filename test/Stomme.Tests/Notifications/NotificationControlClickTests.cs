using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Services;
using Stomme.Notifications;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;
using NotificationsIndex = Stomme.Modules.Notifications.Index;
using Preferences = Stomme.Modules.Notifications.Preferences;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// BL-54 for the Notifications module: the controls' <c>@onclick</c> handlers are BOUND. A Razor file compiled
    /// without <c>@using Microsoft.AspNetCore.Components.Web</c> turns <c>@onclick</c> into a plain HTML attribute - the module
    /// builds green, renders, and every click is silently dead. bUnit's <c>Click()</c> THROWS
    /// (<c>MissingEventHandlerException</c>) on an element with no bound handler, so these facts go RED on exactly that defect.
    /// </summary>
    /// <remarks>
    /// The controls render with a real <see cref="PageState"/> / <see cref="ModuleModel"/> cascade and the module's key rows in the
    /// module's <c>PermissionList</c> (what <c>PageModuleRepository</c> merges in), so <c>IsAuthorizedPermission</c> is exercised
    /// too - a user without view-own gets no button at all.
    /// </remarks>
    public sealed class NotificationControlClickTests : BunitContext
    {
        private readonly RecordingNotificationsService _service = new();
        private readonly EmptyInboxService _inbox = new();

        public NotificationControlClickTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<INotificationsService>(_service);
            Services.AddSingleton<INotificationService>(_inbox);
        }

        // BL-229, found driving "Ask for approval": the inbox showed a notice's subject alone, so an approver was told a decision
        // waits but not what, nor where. A notice opens to its body as text - the renderer's encoded breaks and an error's <br />
        // as lines, markup never - with an address on this site as a link and any other left as text.
        [Fact]
        public void An_inbox_notice_opens_to_its_body_with_the_sites_own_addresses_as_links()
        {
            _inbox.Items.Add(new Notification
            {
                Subject = "Approval needed: Pay invoices",
                Body = "Pay invoice 42?&#xA;&#xA;Decide here: http://localhost/admin/pipelines/*/59/Run?id=r1.&#xA;Not here: https://elsewhere.test/phish &lt;b&gt;bold&lt;/b&gt;<script>alert(1)</script>",
                CreatedOn = new DateTime(2026, 10, 6, 14, 20, 2, DateTimeKind.Utc),
            });

            var cut = Render<NotificationsIndex>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Registered))
                .AddCascadingValue(Module()));

            var item = cut.Find(".db-notification-item");
            Assert.Equal("Approval needed: Pay invoices", item.QuerySelector("summary")!.TextContent.Trim());
            var body = item.QuerySelector(".db-notification-item__body")!;
            Assert.Equal("Pay invoice 42?\n\nDecide here: http://localhost/admin/pipelines/*/59/Run?id=r1.\nNot here: https://elsewhere.test/phish <b>bold</b>alert(1)", body.TextContent);
            var link = Assert.Single(body.QuerySelectorAll("a"));
            Assert.Equal("http://localhost/admin/pipelines/*/59/Run?id=r1", link.GetAttribute("href"));
            Assert.Empty(body.QuerySelectorAll("b, script"));
        }

        [Fact]
        public void Clicking_save_on_the_preferences_control_submits_the_callers_preferences()
        {
            var cut = Render<Preferences>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Registered))
                .AddCascadingValue(Module()));

            cut.Find("#notifications-preferences-save").Click();

            cut.WaitForAssertion(() => Assert.Equal(1, _service.Saves));
            Assert.Equal("Preferences saved.", cut.Find("#notifications-preferences-message").TextContent.Trim());
        }

        [Fact]
        public void Clicking_refresh_on_the_notification_center_reloads_the_callers_notifications()
        {
            var cut = Render<NotificationsIndex>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Registered))
                .AddCascadingValue(Module()));
            var before = _service.MineReads;
            Assert.True(before >= 1, "the center did not load on render");

            cut.Find("#notifications-refresh").Click();

            cut.WaitForAssertion(() => Assert.Equal(before + 1, _service.MineReads));

            // A registered user holds view-own only: no operator links.
            Assert.Empty(cut.FindAll("#notifications-log-link"));
            Assert.Empty(cut.FindAll("#notifications-templates-link"));
        }

        [Fact]
        public void A_user_without_view_own_gets_no_control_and_the_service_is_never_called()
        {
            var cut = Render<Preferences>(parameters => parameters
                .AddCascadingValue(Page("Some Other Role"))
                .AddCascadingValue(Module()));

            Assert.Empty(cut.FindAll("#notifications-preferences-save"));
            Assert.NotEmpty(cut.FindAll(".db-notifications-denied"));
            Assert.Equal(0, _service.PreferenceReads);
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page(string role) => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "notifications", Name = "Notifications" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + role + ";" },
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        // The key rows as the definition seeds them (view-own: Registered Users + Administrators; the rest: Administrators).
        private static ModuleModel Module() => new()
        {
            ModuleId = 11,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(NotificationsPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        private sealed class RecordingNotificationsService : INotificationsService
        {
            public int Saves;
            public int MineReads;
            public int PreferenceReads;

            public Task<List<NotificationRequestSummary>> GetMyNotificationsAsync()
            {
                MineReads++;
                return Task.FromResult(new List<NotificationRequestSummary>());
            }

            public Task<NotificationPreferencesView> GetMyPreferencesAsync()
            {
                PreferenceReads++;
                return Task.FromResult(new NotificationPreferencesView(Array.Empty<NotificationChannelKind>(), NotificationSeverityPreference.All,
                    Array.Empty<string>(), null, NotificationDigestMode.Immediate, null));
            }

            public Task SaveMyPreferencesAsync(NotificationPreferencesView preferences)
            {
                Saves++;
                return Task.CompletedTask;
            }

            public Task<List<NotificationRequestSummary>> GetRequestsAsync() => Task.FromResult(new List<NotificationRequestSummary>());
            public Task<NotificationRequestDetail> GetRequestAsync(Guid requestId) => Task.FromResult<NotificationRequestDetail>(null!);
            public Task RetryRequestAsync(Guid requestId) => Task.CompletedTask;
            public Task<Guid> SendTestAsync(NotificationSendTestInput input) => Task.FromResult(Guid.Empty);
            public Task<List<NotificationTemplateSummary>> GetTemplatesAsync() => Task.FromResult(new List<NotificationTemplateSummary>());
            public Task<NotificationTemplateDetail> GetTemplateAsync(Guid templateId) => Task.FromResult<NotificationTemplateDetail>(null!);
            public Task<Guid> SaveTemplateAsync(NotificationTemplateInput input) => Task.FromResult(Guid.Empty);
            public Task DeleteTemplateAsync(Guid templateId) => Task.CompletedTask;
            public Task<NotificationTemplatePreview> PreviewTemplateAsync(NotificationTemplatePreviewInput input) => Task.FromResult<NotificationTemplatePreview>(null!);
            public Task<List<NotificationChannelSummary>> GetChannelsAsync() => Task.FromResult(new List<NotificationChannelSummary>());
            public Task<NotificationChannelDetail> GetChannelAsync(Guid channelId) => Task.FromResult<NotificationChannelDetail>(null!);
            public Task<Guid> SaveChannelAsync(NotificationChannelInput input) => Task.FromResult(Guid.Empty);
            public Task SetChannelEnabledAsync(Guid channelId, bool enabled) => Task.CompletedTask;
            public Task SetChannelCredentialAsync(Guid channelId, NotificationChannelCredentialInput input) => Task.CompletedTask;
            public Task DeleteChannelAsync(Guid channelId) => Task.CompletedTask;
        }

        private sealed class EmptyInboxService : INotificationService
        {
            /// <summary>The caller's inbox, as the framework's notification rows answer it; empty unless a test fills it.</summary>
            public List<Notification> Items { get; } = new();

            public Task<List<Notification>> GetNotificationsAsync(int siteId, string direction, int userId) => Task.FromResult(Items.ToList());
            public Task<List<Notification>> GetNotificationsAsync(int siteId, string direction, int userId, int count, bool isRead) => Task.FromResult(new List<Notification>());
            public Task<int> GetNotificationCountAsync(int siteId, string direction, int userId, bool isRead) => Task.FromResult(0);
            public Task<Notification> GetNotificationAsync(int notificationId) => Task.FromResult<Notification>(null!);
            public Task<Notification> AddNotificationAsync(Notification notification) => Task.FromResult(notification);
            public Task<Notification> UpdateNotificationAsync(Notification notification) => Task.FromResult(notification);
            public Task DeleteNotificationAsync(int notificationId) => Task.CompletedTask;
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}