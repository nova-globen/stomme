using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The request tests (10 facts): the
    /// <see cref="NotificationRequestAggregate"/> - creation invariants, attempt recording per requested channel, the
    /// derived <see cref="NotificationStatus"/>, the Sent/Failed events, and round-trip persistence. Type adaptation
    /// only: <c>DateTimeOffset</c> became a UTC <see cref="DateTime"/>, and the round-trip runs on the File store.
    /// </summary>
    public sealed class NotificationRequestTests
    {
        private static readonly DateTime At = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Create_StartsQueued_WithNoAttempts()
        {
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);

            Assert.Equal(NotificationStatus.Queued, request.Status);
            Assert.Empty(request.Attempts);
            Assert.False(request.AllChannelsAttempted);
        }

        [Fact]
        public void Create_WithoutRecipients_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "pipeline.failed", recipients: [], requestedChannels: [ChannelKind.Email]));
            Assert.Equal("recipients", ex.ParamName);
        }

        [Fact]
        public void Create_WithoutChannels_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "pipeline.failed", recipients: [NotificationTestData.Email()], requestedChannels: []));
            Assert.Equal("requestedChannels", ex.ParamName);
        }

        [Fact]
        public void Create_DeduplicatesRequestedChannels()
        {
            var request = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "pipeline.failed",
                recipients: [NotificationTestData.Email()],
                requestedChannels: [ChannelKind.Email, ChannelKind.Email, ChannelKind.InApp]);

            Assert.Equal([ChannelKind.Email, ChannelKind.InApp], request.RequestedChannels);
        }

        [Fact]
        public void RecordAttempt_ForUnrequestedChannel_Throws()
        {
            var request = NotificationTestData.Request(ChannelKind.Email);

            Assert.Throws<InvalidOperationException>(() =>
                request.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Succeeded, At));
        }

        [Fact]
        public void RecordAttempt_AllChannelsSucceed_StatusIsSent_AndRaisesSentEvents()
        {
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);

            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, At);
            request.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Succeeded, At);

            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.True(request.AllChannelsAttempted);
            Assert.Equal(2, request.DomainEvents.OfType<NotificationSentEvent>().Count());
            Assert.All(request.DomainEvents.OfType<NotificationSentEvent>(), e => Assert.Equal(request.Id, e.RequestId));
        }

        [Fact]
        public void RecordAttempt_AllChannelsFail_StatusIsFailed_AndRaisesFailedEvents()
        {
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);

            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Failed, At, "smtp timeout");
            request.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Failed, At, "hub offline");

            Assert.Equal(NotificationStatus.Failed, request.Status);
            Assert.Equal(2, request.DomainEvents.OfType<NotificationFailedEvent>().Count());
        }

        [Fact]
        public void RecordAttempt_MixedOutcomes_StatusIsPartiallyFailed()
        {
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);

            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, At);
            request.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Failed, At, "hub offline");

            Assert.Equal(NotificationStatus.PartiallyFailed, request.Status);
        }

        [Fact]
        public void RecordAttempt_NotAllChannelsAttempted_StaysPartiallyFailed()
        {
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);

            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, At);

            Assert.False(request.AllChannelsAttempted);
            Assert.Equal(NotificationStatus.PartiallyFailed, request.Status);
        }

        [Fact]
        public async Task RoundTrips_ThroughRepository_WithAttempts()
        {
            await using var store = await NotificationFileStore.StartAsync();

            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.InApp);
            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, At);
            request.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Failed, At, "hub offline");
            await store.AddAsync(request);

            var loaded = await store.GetByIdAsync<NotificationRequestAggregate>(request.Id);

            Assert.NotNull(loaded);
            Assert.Equal(NotificationStatus.PartiallyFailed, loaded!.Status);
            Assert.Equal(2, loaded.Attempts.Count);
            Assert.Equal("hub offline", loaded.Attempts.Single(a => a.Channel == ChannelKind.InApp).Error);
            Assert.Equal([ChannelKind.Email, ChannelKind.InApp], loaded.RequestedChannels);
        }
    }
}
