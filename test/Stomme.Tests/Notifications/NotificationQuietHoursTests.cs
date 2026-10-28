using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// Ported from the predecessor platform <c>tests/Notifications.Tests/NotificationPreferenceTests.cs</c> (16 facts), renamed per the
    /// BL-78 gate list. 13 facts travel here: the domain / pure-primitive facts (step 2) and, since step 4, the ten that
    /// drive the dispatch pipeline - harness swapped: the File store + the dispatch job's per-site pass
    /// (<see cref="NotificationPipelineHarness.RunSiteAsync"/>) for the predecessor platform's in-memory store + <c>SendNotificationCommand</c>,
    /// and the scalar <c>NextAttemptOn</c> re-arm for the bus consumer's deferred message. The three self-service CQRS
    /// facts (<c>SaveThenGet_RoundTripsPreferences</c>, <c>Save_Twice_UpdatesInPlace</c>,
    /// <c>Get_NoPreference_ReturnsPermissiveDefaults</c>) land with the CQRS step. Type adaptation only:
    /// <c>DateTimeOffset</c> became a UTC <see cref="DateTime"/>, a user id became an int.
    /// </summary>
    public sealed class NotificationQuietHoursTests
    {
        private const string TemplateKey = "pref.test";
        private const string WebhookProviderKey = "cap-webhook";
        private const string InAppProviderKey = "cap-inapp";

        private static readonly DateTime LateEvening = new(2026, 6, 25, 23, 0, 0, DateTimeKind.Utc);

        // ----- Channel/severity/topic filtering -----

        [Fact]
        public async Task NoPreference_DeliversAllRequestedChannels()
        {
            var (host, webhook, inApp) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            await SeedAsync(host, Template(), preference: null, WebhookChannel(), InAppChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook, ChannelKind.InApp]);

            await host.RunSiteAsync();

            var request = (await host.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.Single(webhook.Sent);
            Assert.Single(inApp.Sent);
        }

        [Fact]
        public async Task ChannelOptOut_SkipsThatChannel_AndDeliversTheRest()
        {
            var (host, webhook, inApp) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, channels: [ChannelKind.Webhook]); // accepts webhook only
            await SeedAsync(host, Template(), preference, WebhookChannel(), InAppChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook, ChannelKind.InApp]);

            await host.RunSiteAsync();

            var request = (await host.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status); // both channels resolved (one sent, one skipped)
            Assert.Single(webhook.Sent);
            Assert.Empty(inApp.Sent);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.InApp && a.Status == DeliveryStatus.Skipped);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.Webhook && a.Status == DeliveryStatus.Succeeded);
        }

        [Fact]
        public async Task SeverityBelowThreshold_SuppressesDelivery()
        {
            var (host, webhook, _) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, severity: NotificationSeverityFilter.CriticalOnly);
            await SeedAsync(host, Template(), preference, WebhookChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook], NotificationSeverity.Warning);

            await host.RunSiteAsync();

            var request = (await host.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Empty(webhook.Sent);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.Webhook && a.Status == DeliveryStatus.Skipped);
            Assert.Equal(NotificationStatus.Sent, request.Status); // suppression resolves the channel, no retry
        }

        [Fact]
        public async Task SeverityAtOrAboveThreshold_Delivers()
        {
            var (host, webhook, _) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, severity: NotificationSeverityFilter.CriticalOnly);
            await SeedAsync(host, Template(), preference, WebhookChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook], NotificationSeverity.Critical);

            await host.RunSiteAsync();

            Assert.Single(webhook.Sent);
            Assert.Equal(NotificationStatus.Sent, (await host.GetAsync<NotificationRequestAggregate>(id))!.Status);
        }

        [Fact]
        public async Task MutedTopic_SuppressesDelivery_WhileOtherTopicsDeliver()
        {
            var (host, webhook, _) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, mutedTopics: ["pipeline:42"]);
            await SeedAsync(host, Template(), preference, WebhookChannel());

            var mutedId = await SeedRequestAsync(host, userId, [ChannelKind.Webhook], topic: "pipeline:42");
            await SeedRequestAsync(host, userId, [ChannelKind.Webhook], topic: "pipeline:7");

            await host.RunSiteAsync(); // one pass handles both (the predecessor platform sent each separately)

            Assert.Contains((await host.GetAsync<NotificationRequestAggregate>(mutedId))!.Attempts, a => a.Status == DeliveryStatus.Skipped);
            Assert.Single(webhook.Sent); // only the un-muted topic was delivered
        }

        // ----- Quiet hours -----

        [Fact]
        public async Task QuietHours_NonCritical_DefersWithoutAttempting()
        {
            var (host, webhook, _) = await BuildHostAsync(clock: LateEvening);
            await using var _ = host;
            var userId = 101;
            // 22:00-07:00 window (wraps midnight); LateEvening 23:00 is inside it.
            var preference = Preference(userId, quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero));
            await SeedAsync(host, Template(), preference, WebhookChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook]);

            var result = await host.RunSiteAsync();

            // Harness swap: the predecessor platform's outcome.DeferFor (8 h) is the re-armed NextAttemptOn.
            Assert.Equal(1, result.Deferred);
            var request = (await host.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(TimeSpan.FromHours(8), request.NextAttemptOn - LateEvening); // 23:00 -> next 07:00
            Assert.Empty(request.Attempts);
            Assert.Equal(NotificationStatus.Queued, request.Status);
            Assert.Empty(webhook.Sent);
        }

        [Fact]
        public async Task QuietHours_Critical_DeliversAnyway()
        {
            var (host, webhook, _) = await BuildHostAsync(clock: LateEvening);
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero));
            await SeedAsync(host, Template(), preference, WebhookChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook], NotificationSeverity.Critical);

            var result = await host.RunSiteAsync();

            Assert.Equal(0, result.Deferred);
            Assert.Single(webhook.Sent);
            Assert.Equal(NotificationStatus.Sent, (await host.GetAsync<NotificationRequestAggregate>(id))!.Status);
        }

        [Fact]
        public async Task QuietHours_DispatchConsumer_RedispatchesAfterWindow()
        {
            var (host, webhook, _) = await BuildHostAsync(clock: LateEvening);
            await using var _ = host;
            var userId = 101;
            var preference = Preference(userId, quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero));
            await SeedAsync(host, Template(), preference, WebhookChannel());
            var id = await SeedRequestAsync(host, userId, [ChannelKind.Webhook]);

            await host.RunSiteAsync();

            // Harness swap: the predecessor platform deferred a fresh DispatchNotificationMessage by 8 h on the bus. Stomme re-arms the
            // request's NextAttemptOn to the window end without consuming a retry, and the job re-dispatches it then.
            var deferred = (await host.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(TimeSpan.FromHours(8), deferred.NextAttemptOn - LateEvening);
            Assert.Equal(0, deferred.AttemptCount);
            Assert.Empty(webhook.Sent);

            host.Clock.Set(deferred.NextAttemptOn);
            await host.RunSiteAsync();
            Assert.Single(webhook.Sent);
        }

        [Fact]
        public async Task QuietHours_WithAnAwakeRecipient_DeliversNow()
        {
            var (host, webhook, _) = await BuildHostAsync(clock: LateEvening);
            await using var _ = host;
            var quietUser = 101;
            var preference = Preference(quietUser, quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero));
            await SeedAsync(host, Template(), preference, WebhookChannel());

            // A quiet user plus a literal email recipient (always reachable): the request is delivered now. The request is
            // stored directly (as in the predecessor platform), not dispatched, so the site's external-recipient opt-in is not involved.
            var request = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId, TemplateKey,
                recipients: [NotificationPipelineHarness.User(quietUser), new Recipient(RecipientKind.Email, "ops@example.com")],
                requestedChannels: [ChannelKind.Webhook],
                createdOnUtc: LateEvening);
            await host.AddAsync(request);

            var result = await host.RunSiteAsync();
            Assert.Equal(0, result.Deferred);

            Assert.NotEmpty(webhook.Sent);
        }

        // ----- Preferred locale -----

        [Fact]
        public async Task PreferredLocale_SelectsMatchingTemplateContent()
        {
            var (host, webhook, _) = await BuildHostAsync();
            await using var _ = host;
            var userId = 101;
            var template = Template(
                new LocalizedContent(new Locale("en"), new Content("Hello", "English body")),
                new LocalizedContent(new Locale("fr"), new Content("Bonjour", "Corps francais")));
            var preference = Preference(userId, locale: "fr");
            await SeedAsync(host, template, preference, WebhookChannel());
            await SeedRequestAsync(host, userId, [ChannelKind.Webhook]);

            await host.RunSiteAsync();

            Assert.Equal("Corps francais", webhook.Sent.Single().Payload.Body);
        }

        // ----- Digest grouping primitive -----

        [Fact]
        public void Digest_GroupsRequestsByUser_InAscendingUserOrder()
        {
            NotificationRequestAggregate Req() => NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, TemplateKey, [NotificationTestData.Email()], [ChannelKind.Email]);

            var userA = 0xaa;
            var userB = 0xbb;
            var entries = new (int, NotificationRequestAggregate)[]
            {
                (userB, Req()), (userA, Req()), (userA, Req()),
            };

            var batches = NotificationDigest.GroupForDigest(entries);

            Assert.Equal(2, batches.Count);
            Assert.Equal(userA, batches[0].UserId);
            Assert.Equal(2, batches[0].Requests.Count);
            Assert.Equal(userB, batches[1].UserId);
            Assert.Single(batches[1].Requests);
        }

        // ----- QuietHours value object -----

        [Fact]
        public void QuietHours_WrappingWindow_IsQuietAndComputesNextEnd()
        {
            var quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero);

            Assert.True(quiet.IsQuietAt(new DateTime(2026, 6, 25, 23, 0, 0, DateTimeKind.Utc)));  // pre-midnight
            Assert.True(quiet.IsQuietAt(new DateTime(2026, 6, 26, 3, 0, 0, DateTimeKind.Utc)));   // post-midnight
            Assert.False(quiet.IsQuietAt(new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc))); // midday

            Assert.Equal(
                new DateTime(2026, 6, 26, 7, 0, 0, DateTimeKind.Utc),
                quiet.NextEnd(new DateTime(2026, 6, 25, 23, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(
                new DateTime(2026, 6, 26, 7, 0, 0, DateTimeKind.Utc),
                quiet.NextEnd(new DateTime(2026, 6, 26, 3, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void QuietHours_RespectsUtcOffset()
        {
            // 22:00-07:00 local at UTC+5: 18:00Z is 23:00 local -> quiet.
            var quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.FromHours(5));
            Assert.True(quiet.IsQuietAt(new DateTime(2026, 6, 25, 18, 0, 0, DateTimeKind.Utc)));
            Assert.False(quiet.IsQuietAt(new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc))); // 17:00 local
        }

        // ----- harness -----

        private static async Task<(NotificationPipelineHarness Host, FakeTransportChannel Webhook, FakeTransportChannel InApp)> BuildHostAsync(DateTime? clock = null)
        {
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            var inApp = new FakeTransportChannel(ChannelKind.InApp, InAppProviderKey);
            var host = await NotificationPipelineHarness.StartAsync([webhook, inApp]);
            if (clock is { } at)
            {
                host.Clock.Set(at);
            }

            return (host, webhook, inApp);
        }

        private static NotificationTemplateAggregate Template(params LocalizedContent[] contents) =>
            NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId, TemplateKey,
                supportedChannels: [ChannelKind.Webhook, ChannelKind.InApp],
                contents: contents.Length == 0
                    ? [new LocalizedContent(new Locale("en"), new Content("Subject", "Body"))]
                    : contents,
                placeholders: new PlaceholderSchema([]));

        private static NotificationChannelAggregate WebhookChannel() =>
            NotificationPipelineHarness.Channel(ChannelKind.Webhook, WebhookProviderKey, "Webhook");

        private static NotificationChannelAggregate InAppChannel() =>
            NotificationPipelineHarness.Channel(ChannelKind.InApp, InAppProviderKey, "In-app");

        private static async Task SeedAsync(
            NotificationPipelineHarness host, NotificationTemplateAggregate template, NotificationPreferenceAggregate? preference, params NotificationChannelAggregate[] channels)
        {
            await host.AddAsync(template);
            await host.AddAsync(channels);
            if (preference is not null)
            {
                await host.AddAsync(preference);
            }
        }

        private static async Task<Guid> SeedRequestAsync(
            NotificationPipelineHarness host, int userId, IReadOnlyList<ChannelKind> channels,
            NotificationSeverity severity = NotificationSeverity.Information, string? topic = null)
        {
            var request = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId, TemplateKey,
                recipients: [NotificationPipelineHarness.User(userId)],
                requestedChannels: channels, parameters: null, severity: severity, topic: topic,
                createdOnUtc: host.Clock.UtcNow);
            await host.AddAsync(request);
            return request.Id;
        }

        private static NotificationPreferenceAggregate Preference(
            int userId,
            IReadOnlyList<ChannelKind>? channels = null,
            NotificationSeverityFilter severity = NotificationSeverityFilter.All,
            IReadOnlyList<string>? mutedTopics = null,
            QuietHours? quietHours = null,
            DigestMode digest = DigestMode.Immediate,
            string? locale = null) =>
            NotificationPipelineHarness.Preference(userId, digest, channels, severity, mutedTopics, quietHours, locale);
    }
}
