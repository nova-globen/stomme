using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.Security.Abstractions;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The original channel CQRS cases (7 Fact + 1 Theory x5 =
    /// 12 cases): the channel CQRS surface end to end through the mediator. Run on <see cref="NotificationCqrsHarness"/>:
    /// the module's startup on the File store, fake transports, a dictionary site secret store).
    /// </summary>
    /// <remarks>
    /// Per-case map: <c>Configure_CreatesChannel_AndStoresSecretInVaultByReferenceOnly</c> -&gt;
    /// <c>..._AndStoresSecretInTheSiteSecretStoreByReferenceOnly</c> (the Vault is <c>ISiteSecretStore</c>, the reference the
    /// DERIVED key); <c>Configure_SendGridWithoutFrom_IsRejectedByProviderValidation</c> -&gt;
    /// <c>Configure_UnregisteredNamedProvider_IsRejected</c> (SendGrid is not shipped in this module, and a named provider nothing registers
    /// is now REJECTED rather than silently skipped); the Theory's two query rows expect <c>ManageChannels</c>, not the split
    /// <c>notifications:view</c>. The other four facts are unchanged in intent. Added below the original cases:
    /// site isolation, the credential command, delete.
    /// </remarks>
    public sealed class NotificationChannelCqrsTests
    {
        private static ConfigureChannelCommand NewEmailChannel(Guid? id = null, string name = "Primary email", string? secret = "smtp-user:smtp-pass") =>
            new(Id: id,
                Kind: NotificationChannelKind.Email,
                Name: name,
                Endpoint: "smtp.example.com",
                Secret: secret,
                Options: new[] { new NotificationChannelOption("port", "587"), new NotificationChannelOption("from", "no-reply@example.com") },
                RateLimit: new NotificationRateLimitInput(100, 60));

        [Fact]
        public async Task Configure_CreatesChannel_AndStoresSecretInTheSiteSecretStoreByReferenceOnly()
        {
            var (harness, secrets, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel());

            var view = await harness.SendAsync(new GetNotificationChannelQuery(id));
            Assert.NotNull(view);
            Assert.Equal(NotificationChannelKind.Email, view!.Kind);
            Assert.True(view.Enabled);
            // Only the reference is persisted - the raw secret is in the site secret store, never on the channel or its view.
            Assert.Equal(WebhookNotificationChannel.CredentialReferenceKey(id), view.SecretReference);
            Assert.Equal("smtp-user:smtp-pass", secrets.Secrets[(NotificationPipelineHarness.SiteId, view.SecretReference!)]);
            Assert.DoesNotContain(view.Options, option => option.Value.Contains("smtp-pass"));
            Assert.DoesNotContain("smtp-pass", System.Text.Json.JsonSerializer.Serialize(view));
        }

        [Fact]
        public async Task Configure_ListReturnsTheChannel()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel(name: "Ops email"));

            var list = await harness.SendAsync(new ListNotificationChannelsQuery());
            Assert.Contains(list, channel => channel.Id == id && channel.Name == "Ops email");
        }

        [Fact]
        public async Task Configure_WithExistingId_ReconfiguresInPlace()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel(name: "Before"));
            var updatedId = await harness.SendAsync(NewEmailChannel(id: id, name: "After"));

            Assert.Equal(id, updatedId);
            var view = await harness.SendAsync(new GetNotificationChannelQuery(id));
            Assert.Equal("After", view!.Name);
        }

        [Fact]
        public async Task Configure_CannotChangeKind()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel());
            var changeKind = new ConfigureChannelCommand(id, NotificationChannelKind.Webhook, "Webhook", "https://hooks.example.com/x", null,
                Array.Empty<NotificationChannelOption>(), null);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(changeKind));
            Assert.Contains("kind", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Configure_UnregisteredNamedProvider_IsRejected()
        {
            var (harness, secrets, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var command = new ConfigureChannelCommand(
                Id: null, Kind: NotificationChannelKind.Email, Name: "Cloud email", Endpoint: null, Secret: "api-key-value",
                Options: new[] { new NotificationChannelOption(NotificationProviderKeys.SelectorOption, "sendgrid") },
                RateLimit: null);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(command));
            Assert.Contains("sendgrid", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Refused BEFORE anything was written: no channel, and no orphaned secret.
            Assert.Empty(await harness.SendAsync(new ListNotificationChannelsQuery()));
            Assert.Empty(secrets.Secrets);
        }

        [Fact]
        public async Task EnableDisable_TogglesEnablement()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel());

            await harness.SendAsync(new DisableChannelCommand(id));
            Assert.False((await harness.SendAsync(new GetNotificationChannelQuery(id)))!.Enabled);

            await harness.SendAsync(new EnableChannelCommand(id));
            Assert.True((await harness.SendAsync(new GetNotificationChannelQuery(id)))!.Enabled);
        }

        [Fact]
        public async Task Configure_WebhookChannel_Succeeds()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(new ConfigureChannelCommand(
                Id: null, Kind: NotificationChannelKind.Webhook, Name: "Slack", Endpoint: "https://hooks.slack.com/services/x",
                Secret: null, Options: Array.Empty<NotificationChannelOption>(), RateLimit: null));

            var view = await harness.SendAsync(new GetNotificationChannelQuery(id));
            Assert.Equal(NotificationChannelKind.Webhook, view!.Kind);
            Assert.Equal("https://hooks.slack.com/services/x", view.Endpoint);
        }

        [Theory]
        [InlineData(typeof(ConfigureChannelCommand), NotificationsPermissions.ManageChannels)]
        [InlineData(typeof(EnableChannelCommand), NotificationsPermissions.ManageChannels)]
        [InlineData(typeof(DisableChannelCommand), NotificationsPermissions.ManageChannels)]
        [InlineData(typeof(ListNotificationChannelsQuery), NotificationsPermissions.ManageChannels)]
        [InlineData(typeof(GetNotificationChannelQuery), NotificationsPermissions.ManageChannels)]
        public void Request_DeclaresExpectedPermission(Type requestType, string expected)
        {
            var requires = (RequiresAttribute[])requestType.GetCustomAttributes(typeof(RequiresAttribute), inherit: true);
            Assert.Contains(requires, requirement => requirement.Permission == expected);
        }

        // ---- added after the original cases ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_channel_of_another_site_is_neither_listed_nor_readable_nor_addressable()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var foreign = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId + 1,
                ChannelKind.Email, "Other site", new TransportSettings(ChannelKind.Email, null, null, Array.Empty<TransportOption>()));
            await harness.AddAsync(foreign);

            Assert.Empty(await harness.SendAsync(new ListNotificationChannelsQuery()));
            Assert.Null(await harness.SendAsync(new GetNotificationChannelQuery(foreign.Id)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DisableChannelCommand(foreign.Id)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(NewEmailChannel(id: foreign.Id, name: "Hijack")));
            Assert.True((await harness.GetAsync<NotificationChannelAggregate>(foreign.Id))!.Enabled);
            Assert.Equal("Other site", (await harness.GetAsync<NotificationChannelAggregate>(foreign.Id))!.Name);
        }

        [Fact]
        public async Task The_credential_command_replaces_and_removes_the_secret_and_never_returns_it()
        {
            var (harness, secrets, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(new ConfigureChannelCommand(null, NotificationChannelKind.Webhook, "Hook", "https://hooks.example.com/x",
                null, Array.Empty<NotificationChannelOption>(), null));
            Assert.Null((await harness.SendAsync(new GetNotificationChannelQuery(id)))!.SecretReference);

            await harness.SendAsync(new SetChannelCredentialCommand(id, "token-1"));
            var reference = WebhookNotificationChannel.CredentialReferenceKey(id);
            Assert.Equal(reference, (await harness.SendAsync(new GetNotificationChannelQuery(id)))!.SecretReference);
            Assert.Equal("token-1", secrets.Secrets[(NotificationPipelineHarness.SiteId, reference)]);

            await harness.SendAsync(new SetChannelCredentialCommand(id, "token-2"));
            Assert.Equal("token-2", secrets.Secrets[(NotificationPipelineHarness.SiteId, reference)]);

            // Reconfiguring WITHOUT a secret keeps the credential it holds.
            await harness.SendAsync(new ConfigureChannelCommand(id, NotificationChannelKind.Webhook, "Hook renamed", "https://hooks.example.com/y",
                null, Array.Empty<NotificationChannelOption>(), null));
            Assert.Equal(reference, (await harness.SendAsync(new GetNotificationChannelQuery(id)))!.SecretReference);

            await harness.SendAsync(new SetChannelCredentialCommand(id, null));
            Assert.Null((await harness.SendAsync(new GetNotificationChannelQuery(id)))!.SecretReference);
            Assert.Empty(secrets.Secrets);
        }

        [Fact]
        public async Task Delete_removes_the_channel_and_its_credential()
        {
            var (harness, secrets, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewEmailChannel());
            Assert.NotEmpty(secrets.Secrets);

            await harness.SendAsync(new DeleteChannelCommand(id));

            Assert.Null(await harness.SendAsync(new GetNotificationChannelQuery(id)));
            Assert.Empty(secrets.Secrets);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DeleteChannelCommand(id)));
        }
    }
}