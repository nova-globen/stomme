using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications.Persistence;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// A site with no e-mail or in-app channel gets a default one at its first notification. A new site had none, so
    /// every notification failed ("No enabled channel is configured for InApp") and was retried until an administrator
    /// created them (found by the app-guidance writers). Neither kind needs settings of its own; a channel an administrator
    /// disabled is left alone, and a kind that needs configuring (webhook) still fails.
    /// </summary>
    public sealed class NotificationDefaultChannelTests
    {
        private const string TemplateKey = "default.channel";

        private static NotificationRequestAggregate Request(params ChannelKind[] channels) =>
            NotificationRequestAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId, TemplateKey,
                [new Recipient(RecipientKind.Email, "ops@example.com")], channels, createdOnUtc: NotificationPipelineHarness.Start);

        [Fact]
        public async Task A_site_with_no_in_app_or_email_channel_gets_defaults_and_the_notification_is_delivered()
        {
            var inApp = new FakeTransportChannel(ChannelKind.InApp, NotificationProviderKeys.InApp);
            var email = new FakeTransportChannel(ChannelKind.Email, NotificationProviderKeys.Email);
            await using var harness = await NotificationPipelineHarness.StartAsync([inApp, email]);
            await harness.AddAsync(NotificationPipelineHarness.Template(TemplateKey, "Subject", "Body"));
            var request = Request(ChannelKind.InApp, ChannelKind.Email);
            await harness.AddAsync(request);

            await harness.RunSiteAsync();

            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!.Status);
            var channels = await harness.InScopeAsync(sp => sp.GetRequiredService<INotificationChannelRepository>().ListBySiteAsync(NotificationPipelineHarness.SiteId));
            Assert.Equal(new[] { NotificationDispatchProcessor.DefaultEmailChannelName, NotificationDispatchProcessor.DefaultInAppChannelName },
                channels.Select(channel => channel.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(channels, channel => Assert.True(channel.Enabled));
        }

        [Fact]
        public async Task A_disabled_channel_is_left_disabled_and_a_webhook_still_needs_configuring()
        {
            var inApp = new FakeTransportChannel(ChannelKind.InApp, NotificationProviderKeys.InApp);
            await using var harness = await NotificationPipelineHarness.StartAsync([inApp]);
            await harness.AddAsync(NotificationPipelineHarness.Template(TemplateKey, "Subject", "Body"));
            var disabled = NotificationPipelineHarness.Channel(ChannelKind.InApp, NotificationProviderKeys.InApp, "Inbox");
            disabled.Disable();
            await harness.AddAsync(disabled);
            var request = Request(ChannelKind.InApp, ChannelKind.Webhook);
            await harness.AddAsync(request);

            await harness.RunSiteAsync();

            var reread = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.NotEqual(NotificationStatus.Sent, reread.Status);
            var channels = await harness.InScopeAsync(sp => sp.GetRequiredService<INotificationChannelRepository>().ListBySiteAsync(NotificationPipelineHarness.SiteId));
            Assert.Equal("Inbox", Assert.Single(channels).Name);
            Assert.False(channels.Single().Enabled);
        }
    }
}
