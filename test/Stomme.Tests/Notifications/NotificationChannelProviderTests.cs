using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// BL-78 step 3 - the channel seam: resolution by kind (+ the <c>provider</c> selector), the skip cases, the
    /// kind-specific settings validation, and each shipped channel's send. Ported from the predecessor platform
    /// <c>NotificationChannelProviderTests</c> (11 facts) and EXTENDED, not merely carried.
    /// <para>
    /// Reconciliation (11 in / 11 accounted for), by the predecessor platform name:
    /// <list type="bullet">
    /// <item><c>Resolver_PicksSmtp_AsTheEmailDefault</c> -> <see cref="Resolver_PicksTheQueueEmailChannel_AsTheEmailDefault"/> (SMTP does not travel; the default e-mail transport is the inherited queue).</item>
    /// <item><c>Resolver_PicksSendGrid_WhenProviderOptionSelectsIt</c> -> <see cref="Resolver_PicksTheNamedProvider_WhenProviderOptionSelectsIt"/> (SendGrid does not travel; a second e-mail transport double is named instead).</item>
    /// <item><c>Resolver_SkipsDisabledChannel</c> -> <see cref="Resolver_SkipsDisabledChannel"/>.</item>
    /// <item><c>Resolver_ReturnsNull_ForKindWithNoProvider</c> -> <see cref="Resolver_ReturnsNull_ForKindWithNoProvider"/>.</item>
    /// <item><c>SmtpProvider_ValidatesMissingHostAndCredential</c> -> <see cref="EmailChannel_RequiresNeitherHostNorCredential"/> (CHANGED - the relaxed e-mail invariant: a credential-free queue channel is valid).</item>
    /// <item><c>SendGridProvider_RequiresFromOption</c> -> <see cref="Resolver_ReturnsNull_ForAnUnregisteredNamedProvider_InsteadOfTheFirstCandidate"/> (CHANGED - SendGrid does not travel; its slot pins the dropped <c>?? candidates[0]</c> fallback).</item>
    /// <item><c>WebhookProvider_PostsEnvelope_AndSucceedsOn2xx</c> -> <see cref="WebhookChannel_PostsEnvelope_AndSucceedsOn2xx"/> (EXTENDED - a real allowlisted loopback receiver through the SSRF-guarded client, JSON-escaped body, Bearer from the site secret store).</item>
    /// <item><c>WebhookProvider_FailsOn5xx</c> -> <see cref="WebhookChannel_FailsOn5xx"/>.</item>
    /// <item><c>InAppProvider_PushesRenderedPayload_ToTheRecipientUser</c> -> <see cref="InAppChannel_WritesADeliveredRow_AndPushesToTheRecipientUser"/> (EXTENDED - the inbox row).</item>
    /// <item><c>InAppProvider_WithoutPushConfigured_FailsWithoutThrowing</c> -> <see cref="InAppChannel_WithoutPushConfigured_StillDeliversToTheInbox"/> (CHANGED - the push port is optional, the row is the delivery).</item>
    /// <item><c>InAppProvider_NonUserRecipient_Fails</c> -> <see cref="InAppChannel_NonUserRecipient_Fails"/>.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class NotificationChannelProviderTests
    {
        private const int Site = NotificationTestData.SiteId;

        private static NotificationChannelResolver Resolver(out CapturingLogger<NotificationChannelResolver> logger, params INotificationChannel[] channels)
        {
            logger = new CapturingLogger<NotificationChannelResolver>();
            return new NotificationChannelResolver(channels, logger);
        }

        private static NotificationChannelResolver Resolver(params INotificationChannel[] channels) => Resolver(out _, channels);

        private static INotificationChannel[] AllChannels() =>
        [
            new EmailNotificationChannel(new RecordingNotificationRepository()),
            new InAppNotificationChannel(new RecordingNotificationRepository(), new MapServiceProvider(), new CapturingLogger<InAppNotificationChannel>()),
            Webhook(new FakeSiteSecretStore(), new FixedWebhookHostAllowlist()),
        ];

        private static WebhookNotificationChannel Webhook(FakeSiteSecretStore secrets, IWebhookHostAllowlist allowlist) =>
            new(new WebhookHttpClient(), secrets, allowlist, new CapturingLogger<WebhookNotificationChannel>());

        private static NotificationChannelAggregate Channel(ChannelKind kind, TransportSettings transport, string name = "Channel") =>
            NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, Site, kind, name, transport);

        // ---- resolution ------------------------------------------------------------------------------------------

        [Fact]
        public void Resolver_PicksTheQueueEmailChannel_AsTheEmailDefault()
        {
            var provider = Resolver(AllChannels()).For(ChannelKind.Email);
            Assert.NotNull(provider);
            Assert.Equal(NotificationProviderKeys.Email, provider.ProviderKey);
            Assert.IsType<EmailNotificationChannel>(provider);
        }

        [Fact]
        public void Resolver_PicksTheNamedProvider_WhenProviderOptionSelectsIt()
        {
            var named = new FakeTransportChannel(ChannelKind.Email, "relay");
            var resolver = Resolver([.. AllChannels(), named]);
            var channel = Channel(ChannelKind.Email,
                new TransportSettings(ChannelKind.Email, null, null, [new TransportOption(NotificationProviderKeys.SelectorOption, "relay")]));

            Assert.Same(named, resolver.ResolveForDelivery(channel));
        }

        [Fact]
        public void Resolver_SkipsDisabledChannel()
        {
            var resolver = Resolver(out var logger, AllChannels());
            var channel = NotificationTestData.WebhookChannel();
            channel.Disable();

            var resolution = resolver.Resolve(channel);
            Assert.Null(resolution.Channel);
            Assert.Contains("disabled", resolution.SkipReason);
            Assert.Null(resolver.ResolveForDelivery(channel));
            Assert.Contains(logger.Messages, message => message.Contains("disabled"));
        }

        [Fact]
        public void Resolver_ReturnsNull_ForKindWithNoProvider()
        {
            // SMS is an enum entry only: no transport ships.
            var resolver = Resolver(out var logger, AllChannels());
            Assert.Null(resolver.For(ChannelKind.Sms));

            var sms = Channel(ChannelKind.Sms, new TransportSettings(ChannelKind.Sms, null, null, []));
            var resolution = resolver.Resolve(sms);
            Assert.Null(resolution.Channel);
            Assert.Contains("No transport", resolution.SkipReason);
            Assert.Contains(logger.Messages, message => message.Contains("No transport"));
        }

        [Fact]
        public void Resolver_ReturnsNull_ForAnUnregisteredNamedProvider_InsteadOfTheFirstCandidate()
        {
            // the predecessor platform: "?? candidates[0]" silently delivered over the first same-kind transport. Dropped (D100 point 2).
            var resolver = Resolver(out var logger, AllChannels());
            var channel = Channel(ChannelKind.Email,
                new TransportSettings(ChannelKind.Email, null, null, [new TransportOption(NotificationProviderKeys.SelectorOption, "sendgrid")]));

            Assert.Null(resolver.For(ChannelKind.Email, "sendgrid"));
            var resolution = resolver.Resolve(channel);
            Assert.Null(resolution.Channel);
            Assert.Contains("sendgrid", resolution.SkipReason);
            Assert.Contains(logger.Messages, message => message.Contains("sendgrid"));
        }

        [Fact]
        public void The_module_startup_registers_exactly_the_three_shipped_channels()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);
            var channels = services.Where(descriptor => descriptor.ServiceType == typeof(INotificationChannel))
                .Select(descriptor => descriptor.ImplementationType)
                .OrderBy(type => type.Name)
                .ToList();

            Assert.Equal([typeof(EmailNotificationChannel), typeof(InAppNotificationChannel), typeof(WebhookNotificationChannel)], channels);
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(NotificationChannelResolver));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(WebhookHttpClient)
                && descriptor.Lifetime == Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
            // F13: the module must not add the mediator a second time.
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.FullName?.Contains("IMediator", StringComparison.Ordinal) == true);
        }

        // ---- e-mail: the inherited queue ------------------------------------------------------------------------

        [Fact]
        public async Task EmailChannel_RequiresNeitherHostNorCredential()
        {
            var channel = new EmailNotificationChannel(new RecordingNotificationRepository());
            var errors = await channel.ValidateSettingsAsync(Site, new TransportSettings(ChannelKind.Email, null, null, []));
            Assert.Empty(errors);
        }

        [Fact]
        public async Task EmailChannel_QueuesAnUndeliveredSystemRow_ForAnExternalAddress()
        {
            var rows = new RecordingNotificationRepository();
            var channel = new EmailNotificationChannel(rows);
            var request = new ChannelDeliveryRequest(Site, NotificationTestData.EmailChannel(), NotificationTestData.Email("dev@example.com"),
                new Payload("Alert &lt;b&gt;", "Pipeline &lt;script&gt; failed."));

            var result = await channel.SendAsync(request);

            Assert.True(result.Succeeded, result.Error);
            var row = Assert.Single(rows.Rows);
            Assert.Equal(Site, row.SiteId);
            Assert.Null(row.FromUserId); // F16: the inherited purge only removes FromUserId == null rows
            Assert.Null(row.ToUserId);
            Assert.Equal("dev@example.com", row.ToEmail);
            Assert.Equal("Alert &lt;b&gt;", row.Subject);
            Assert.Equal("Pipeline &lt;script&gt; failed.", row.Body);
            Assert.False(row.IsDelivered);
            Assert.Null(row.DeliveredOn);
            Assert.Null(row.SendOn);
            Assert.False(row.IsRead);
            Assert.False(row.IsDeleted);
            Assert.Equal(DateTimeKind.Utc, row.CreatedOn.Kind);
        }

        [Fact]
        public async Task EmailChannel_QueuesARowForASiteUser_ByUserId()
        {
            var rows = new RecordingNotificationRepository();
            var result = await new EmailNotificationChannel(rows).SendAsync(
                new ChannelDeliveryRequest(Site, NotificationTestData.EmailChannel(), new Recipient(RecipientKind.User, "42"), new Payload("S", "B")));

            Assert.True(result.Succeeded, result.Error);
            var row = Assert.Single(rows.Rows);
            Assert.Equal(42, row.ToUserId);
            Assert.Equal(string.Empty, row.ToEmail);
            Assert.Null(row.FromUserId);
        }

        [Fact]
        public async Task EmailChannel_RejectsAnEndpointRecipient_AndAnOverlongSubject_WithoutWriting()
        {
            var rows = new RecordingNotificationRepository();
            var channel = new EmailNotificationChannel(rows);

            var endpoint = await channel.SendAsync(new ChannelDeliveryRequest(Site, NotificationTestData.EmailChannel(),
                new Recipient(RecipientKind.Endpoint, "https://hooks.example.com"), new Payload("S", "B")));
            Assert.False(endpoint.Succeeded);
            Assert.False(endpoint.Skipped);

            var overlong = await channel.SendAsync(new ChannelDeliveryRequest(Site, NotificationTestData.EmailChannel(),
                NotificationTestData.Email(), new Payload(new string('s', EmailNotificationChannel.MaxSubjectLength + 1), "B")));
            Assert.False(overlong.Succeeded);
            Assert.Contains("subject", overlong.Error, StringComparison.OrdinalIgnoreCase);

            Assert.Empty(rows.Rows);
        }

        [Fact]
        public async Task EmailChannel_AStoreFailure_IsAFailedResult_NotAnException()
        {
            var rows = new RecordingNotificationRepository { ThrowOnAdd = new InvalidOperationException("the database said: password=hunter2") };
            var result = await new EmailNotificationChannel(rows).SendAsync(
                new ChannelDeliveryRequest(Site, NotificationTestData.EmailChannel(), NotificationTestData.Email(), new Payload("S", "B")));

            Assert.False(result.Succeeded);
            Assert.DoesNotContain("hunter2", result.Error);
        }

        // ---- webhook ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task WebhookChannel_PostsEnvelope_AndSucceedsOn2xx()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var secrets = new FakeSiteSecretStore();
            var channelAggregate = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, Site, ChannelKind.Webhook, "Ops",
                new TransportSettings(ChannelKind.Webhook, receiver.Url(), "notifications.channel.ops.credential", []));
            await secrets.SetAsync(Site, "notifications.channel.ops.credential", "tok-123");
            var channel = Webhook(secrets, new FixedWebhookHostAllowlist("127.0.0.1"));

            var result = await channel.SendAsync(new ChannelDeliveryRequest(
                Site, channelAggregate, NotificationTestData.Email("dev@example.com"), new Payload("Alert", "Pipeline <script>alert(1)</script> failed.")));

            Assert.True(result.Succeeded, result.Error);
            var received = Assert.Single(receiver.Requests);
            Assert.Equal("POST", received.Method);
            Assert.Equal("/hook", received.Path);
            Assert.Equal("Bearer tok-123", received.Headers["Authorization"]);
            Assert.StartsWith("application/json", received.Headers["Content-Type"]);
            Assert.DoesNotContain("<script>", received.Body); // JSON-escaped on the wire
            using var json = JsonDocument.Parse(received.Body);
            Assert.Equal("Alert", json.RootElement.GetProperty("subject").GetString());
            Assert.Equal("Pipeline <script>alert(1)</script> failed.", json.RootElement.GetProperty("body").GetString());
            Assert.Equal("Email:dev@example.com", json.RootElement.GetProperty("to").GetString());
        }

        [Fact]
        public async Task WebhookChannel_FailsOn5xx()
        {
            await using var receiver = new LoopbackWebhookReceiver { Status = HttpStatusCode.InternalServerError };
            var channel = Webhook(new FakeSiteSecretStore(), new FixedWebhookHostAllowlist("127.0.0.1"));

            var result = await channel.SendAsync(new ChannelDeliveryRequest(
                Site, NotificationTestData.WebhookChannel(url: receiver.Url()), NotificationTestData.Email(), new Payload("Subj", "Body")));

            Assert.False(result.Succeeded);
            Assert.Contains("500", result.Error);
            Assert.Single(receiver.Requests);
        }

        [Fact]
        public async Task WebhookChannel_AnUnreadableOrMissingCredential_IsACleanFailure_AndSendsNothing()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var secrets = new FakeSiteSecretStore();
            secrets.Unreadable.Add((Site, "notifications.channel.ops.credential"));
            var channel = Webhook(secrets, new FixedWebhookHostAllowlist("127.0.0.1"));
            var aggregate = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, Site, ChannelKind.Webhook, "Ops",
                new TransportSettings(ChannelKind.Webhook, receiver.Url(), "notifications.channel.ops.credential", []));
            var request = new ChannelDeliveryRequest(Site, aggregate, NotificationTestData.Email(), new Payload("S", "B"));

            var unreadable = await channel.SendAsync(request);
            Assert.False(unreadable.Succeeded);
            Assert.False(unreadable.Skipped);
            Assert.Contains("credential", unreadable.Error);

            secrets.Unreadable.Clear();
            var missing = await channel.SendAsync(request);
            Assert.False(missing.Succeeded);
            Assert.Contains("credential", missing.Error);

            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public void The_credential_reference_key_for_a_channel_is_the_D101_shape()
        {
            var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            Assert.Equal("notifications.channel.0f8fad5bd9cb469fa16570867728950e.credential", WebhookNotificationChannel.CredentialReferenceKey(id));
            Assert.True(Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey(WebhookNotificationChannel.CredentialReferenceKey(id)));
        }

        // ---- in-app ---------------------------------------------------------------------------------------------

        private static ChannelDeliveryRequest InAppRequest(Recipient recipient) =>
            new(Site, Channel(ChannelKind.InApp, NotificationTestData.InAppTransport(), "In-app"), recipient, new Payload("Subj", "Body"));

        [Fact]
        public async Task InAppChannel_WritesADeliveredRow_AndPushesToTheRecipientUser()
        {
            var rows = new RecordingNotificationRepository();
            var push = new CapturingRealtimePush();
            var channel = new InAppNotificationChannel(rows, new MapServiceProvider().With<Stomme.Transport.Realtime.INotificationRealtimePush>(push),
                new CapturingLogger<InAppNotificationChannel>());

            var result = await channel.SendAsync(InAppRequest(new Recipient(RecipientKind.User, "42")));

            Assert.True(result.Succeeded, result.Error);
            var message = Assert.Single(push.Messages);
            Assert.Equal("42", message.UserId);
            Assert.Equal("Subj", message.Subject);
            Assert.Equal("Body", message.Body);

            var row = Assert.Single(rows.Rows);
            Assert.Equal(42, row.ToUserId);
            Assert.Null(row.FromUserId);
            Assert.True(row.IsDelivered);
            Assert.NotNull(row.DeliveredOn);
            Assert.Equal(DateTimeKind.Utc, row.DeliveredOn.Value.Kind);
            Assert.Equal("Subj", row.Subject);
        }

        [Fact]
        public async Task InAppChannel_WithoutPushConfigured_StillDeliversToTheInbox()
        {
            var rows = new RecordingNotificationRepository();
            var channel = new InAppNotificationChannel(rows, new MapServiceProvider(), new CapturingLogger<InAppNotificationChannel>());

            var result = await channel.SendAsync(InAppRequest(new Recipient(RecipientKind.User, "42")));

            Assert.True(result.Succeeded, result.Error);
            Assert.True(Assert.Single(rows.Rows).IsDelivered);
        }

        [Fact]
        public async Task InAppChannel_NonUserRecipient_Fails()
        {
            var rows = new RecordingNotificationRepository();
            var push = new CapturingRealtimePush();
            var channel = new InAppNotificationChannel(rows, new MapServiceProvider().With<Stomme.Transport.Realtime.INotificationRealtimePush>(push),
                new CapturingLogger<InAppNotificationChannel>());

            var result = await channel.SendAsync(InAppRequest(NotificationTestData.Email()));

            Assert.False(result.Succeeded);
            Assert.Empty(push.Messages);
            Assert.Empty(rows.Rows);
        }

        // ---- delivery: resolution -> attempt ---------------------------------------------------------------------

        [Fact]
        public async Task Delivery_RecordsSucceeded_Failed_And_Skipped_Attempts_FromTheResolution()
        {
            var ok = new FakeTransportChannel(ChannelKind.Email, NotificationProviderKeys.Email);
            var failing = new FakeTransportChannel(ChannelKind.Webhook, NotificationProviderKeys.Webhook, ChannelDeliveryResult.Fail("HTTP 503"));
            var delivery = new NotificationChannelDelivery(Resolver(ok, failing), new CapturingLogger<NotificationChannelDelivery>());
            var request = NotificationTestData.Request(ChannelKind.Email, ChannelKind.Webhook, ChannelKind.Sms);
            var at = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            var payload = new Payload("S", "B");

            var sent = await delivery.DeliverAsync(Site, request, NotificationTestData.EmailChannel(), NotificationTestData.Email(), payload, at);
            var failed = await delivery.DeliverAsync(Site, request, NotificationTestData.WebhookChannel(), NotificationTestData.Email(), payload, at);
            var skipped = await delivery.DeliverAsync(Site, request, Channel(ChannelKind.Sms, new TransportSettings(ChannelKind.Sms, null, null, [])),
                NotificationTestData.Email(), payload, at);

            Assert.Equal(DeliveryStatus.Succeeded, sent.Status);
            Assert.Equal(DeliveryStatus.Failed, failed.Status);
            Assert.Equal("HTTP 503", failed.Error);
            Assert.Equal(DeliveryStatus.Skipped, skipped.Status);
            Assert.Contains("No transport", skipped.Error);
            Assert.Equal(3, request.Attempts.Count);
            Assert.Single(ok.Sent);
            Assert.Equal(Site, ok.Sent[0].SiteId);
        }

        [Fact]
        public async Task Delivery_AThrowingTransport_IsAFailedAttempt_ThatNamesOnlyTheExceptionType()
        {
            var throwing = new ThrowingChannel();
            var delivery = new NotificationChannelDelivery(Resolver(throwing), new CapturingLogger<NotificationChannelDelivery>());
            var request = NotificationTestData.Request(ChannelKind.Email);

            var attempt = await delivery.DeliverAsync(Site, request, NotificationTestData.EmailChannel(), NotificationTestData.Email(),
                new Payload("S", "B"), new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(DeliveryStatus.Failed, attempt.Status);
            Assert.Contains(nameof(InvalidOperationException), attempt.Error);
            Assert.DoesNotContain("secret-in-message", attempt.Error);
        }

        private sealed class ThrowingChannel : INotificationChannel
        {
            public ChannelKind Kind => ChannelKind.Email;

            public string ProviderKey => NotificationProviderKeys.Email;

            public Task<System.Collections.Generic.IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, System.Threading.CancellationToken ct = default) =>
                Task.FromResult<System.Collections.Generic.IReadOnlyList<string>>([]);

            public Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, System.Threading.CancellationToken ct = default) =>
                throw new InvalidOperationException("secret-in-message");
        }
    }
}
