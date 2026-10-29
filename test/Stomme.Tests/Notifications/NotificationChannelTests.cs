using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The channel tests (8 facts): the
    /// <see cref="NotificationChannelAggregate"/> and its <see cref="TransportSettings"/> - kind/transport agreement,
    /// the no-inline-secret invariant, enable/disable, the <see cref="ChannelConfiguredEvent"/>, and round-trip
    /// persistence of the transport value graph. The round-trip runs on the module's File store.
    /// Renamed: <c>EmailTransport_WithoutVaultReference_Throws</c> became
    /// <see cref="EmailTransport_WithoutSecretReference_IsAccepted"/> - a credential-free email channel is valid.
    /// </summary>
    public sealed class NotificationChannelTests
    {
        [Fact]
        public void Create_EnablesAndRaisesConfiguredEvent()
        {
            var channel = NotificationTestData.EmailChannel();

            Assert.True(channel.Enabled);
            Assert.Equal(ChannelKind.Email, channel.Kind);
            Assert.Single(channel.DomainEvents.OfType<ChannelConfiguredEvent>());
        }

        [Fact]
        public void Create_TransportKindMismatch_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationChannelAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, ChannelKind.Email, "Mismatch", NotificationTestData.WebhookTransport()));
            Assert.Equal("transport", ex.ParamName);
        }

        [Fact]
        public void Disable_ThenEnable_TogglesAndRaisesEvents()
        {
            var channel = NotificationTestData.EmailChannel();
            channel.ClearDomainEvents();

            channel.Disable();
            Assert.False(channel.Enabled);

            channel.Enable();
            Assert.True(channel.Enabled);
            Assert.Equal(2, channel.DomainEvents.OfType<ChannelConfiguredEvent>().Count());
        }

        [Fact]
        public void Disable_WhenAlreadyDisabled_IsNoOp()
        {
            var channel = NotificationTestData.EmailChannel();
            channel.Disable();
            channel.ClearDomainEvents();

            channel.Disable();

            Assert.Empty(channel.DomainEvents);
        }

        // Deliberate change: the first design threw for an Email transport without a vault reference, which a
        // credential-free email channel (the existing SMTP queue) cannot supply. The reference requirement moves to
        // the channel PROVIDER that declares it; the domain accepts the transport.
        [Fact]
        public void EmailTransport_WithoutSecretReference_IsAccepted()
        {
            var transport = new TransportSettings(ChannelKind.Email, "smtp.example.com", secretReference: null, options: []);

            Assert.Null(transport.SecretReference);
            Assert.Equal(ChannelKind.Email, transport.Kind);
        }

        [Fact]
        public void Transport_WithInlineSecretOption_IsRejected()
        {
            var ex = Assert.Throws<ArgumentException>(() => new TransportSettings(
                ChannelKind.Email, "smtp.example.com", NotificationTestData.SecretReference,
                options: [new TransportOption("apiKey", "sk-live-123")]));
            Assert.Equal("options", ex.ParamName);
        }

        [Fact]
        public void WebhookTransport_WithoutAbsoluteUrl_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new TransportSettings(ChannelKind.Webhook, "not-a-url", secretReference: null, options: []));
            Assert.Equal("endpoint", ex.ParamName);
        }

        [Fact]
        public async Task RoundTrips_ThroughRepository_PreservingTransportGraph()
        {
            await using var store = await NotificationFileStore.StartAsync();

            var channel = NotificationChannelAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, ChannelKind.Email, "Primary",
                NotificationTestData.EmailTransport(), new RateLimit(100, TimeSpan.FromHours(1)));
            await store.AddAsync(channel);

            var loaded = await store.GetByIdAsync<NotificationChannelAggregate>(channel.Id);

            Assert.NotNull(loaded);
            Assert.Equal(channel.Transport, loaded!.Transport);
            Assert.Equal(channel.RateLimit, loaded.RateLimit);
            Assert.Equal(NotificationTestData.SecretReference, loaded.Transport.SecretReference);
        }
    }
}
