using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications.Persistence;
using Stomme.Repository;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// <c>NotificationChannelSeamConformanceTests</c> (SQLite + PostgreSQL) and
    /// <see cref="RavenNotificationChannelSeamConformanceTests"/>: the channel seam DRIVEN through an INSTALLED production
    /// host on each store. A channel aggregate round-trips; the production resolver (plus a FAKE transport added beside
    /// the registered channels) resolves and skips; the real e-mail and in-app channels and the fake deliver; the
    /// attempts recorded on a request persist; and the e-mail and in-app rows read back through the EXISTING
    /// <see cref="INotificationRepository"/> - the e-mail row from the delivery QUEUE predicate, the in-app row from
    /// the user's mailbox.
    /// <para>
    /// <b>EF == Raven.</b> The persisted attempts are compared as a CANONICAL string (channel, status, UTC instant to the
    /// tick, DateTimeKind, error - in recorded order) against one literal expected value shared by every store, so a
    /// green SQLite, PostgreSQL and RavenDB run is byte-identical by construction; attempt identities are compared with
    /// the written aggregate separately. PostgreSQL and RavenDB FAIL rather than skip.
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class NotificationChannelSeamConformanceTests
    {
        internal const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public NotificationChannelSeamConformanceTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> RelationalStores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(RelationalStores))]
        public Task The_channel_seam_round_trips_resolves_delivers_and_records_identically(string storeName) =>
            DriveAsync(storeName, _output);

        internal static readonly DateTime FirstAttempt = new(2026, 9, 14, 12, 0, 0, 125, DateTimeKind.Utc);

        /// <summary>The persisted attempts, canonically - identical on every store or the seam is not provider-neutral.</summary>
        internal const string ExpectedAttempts =
            "Email|Succeeded|2026-09-14T12:00:00.1250000Z|Utc|" + "\n" +
            "InApp|Succeeded|2026-09-14T12:00:01.1250000Z|Utc|" + "\n" +
            "Sms|Succeeded|2026-09-14T12:00:02.1250000Z|Utc|" + "\n" +
            "Sms|Skipped|2026-09-14T12:00:03.1250000Z|Utc|The Sms channel 'Unregistered SMS' names provider 'twilio', which no transport registers; it is not substituted." + "\n" +
            "Email|Skipped|2026-09-14T12:00:04.1250000Z|Utc|The Email channel 'Disabled email' is disabled." + "\n" +
            "InApp|Failed|2026-09-14T12:00:05.1250000Z|Utc|In-app delivery targets a site user; the recipient is not a user.";

        internal static string Canonical(IEnumerable<DeliveryAttempt> attempts) =>
            string.Join("\n", attempts.Select(attempt => string.Join("|",
                attempt.Channel,
                attempt.Status,
                attempt.AttemptedOn.ToString("O", CultureInfo.InvariantCulture),
                attempt.AttemptedOn.Kind,
                attempt.Error ?? string.Empty)));

        internal static async Task DriveAsync(string storeName, ITestOutputHelper output)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var siteId = alias.SiteId;
                var hostUser = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IUserRepository>().GetUserAsync(GateDrive.HostUsername));
                Assert.NotNull(hostUser);
                var marker = "seam-" + Guid.NewGuid().ToString("N");

                // ---- the production composition registers the three shipped channels on this host ------------------
                var registered = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    Task.FromResult(sp.GetServices<INotificationChannel>().Select(channel => channel.GetType().Name).OrderBy(name => name).ToList()));
                Assert.Equal([nameof(EmailNotificationChannel), nameof(InAppNotificationChannel), nameof(WebhookNotificationChannel)], registered);

                // ---- channel aggregates round-trip (write in one scope, read in a fresh one) -------------------------
                var email = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Email, "Queue email",
                    new TransportSettings(ChannelKind.Email, null, null, [new TransportOption("from", "no-reply@example.com")]));
                var inApp = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.InApp, "Inbox",
                    new TransportSettings(ChannelKind.InApp, null, null, []));
                var fakeSms = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Sms, "Fake SMS",
                    new TransportSettings(ChannelKind.Sms, null, null, [new TransportOption(NotificationProviderKeys.SelectorOption, "fake-sms")]));
                var unregistered = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Sms, "Unregistered SMS",
                    new TransportSettings(ChannelKind.Sms, null, null, [new TransportOption(NotificationProviderKeys.SelectorOption, "twilio")]));
                var disabled = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Email, "Disabled email",
                    new TransportSettings(ChannelKind.Email, null, null, []));
                disabled.Disable();
                var webhook = NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Webhook, "Ops hook",
                    new TransportSettings(ChannelKind.Webhook, "https://hooks.example.com/notify?a=1&b=2",
                        WebhookNotificationChannel.CredentialReferenceKey(Guid.NewGuid()), [new TransportOption("format", "{\"json\":true}")]),
                    new RateLimit(10, TimeSpan.FromMinutes(5)));
                var written = new[] { email, inApp, fakeSms, unregistered, disabled, webhook };

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var channels = sp.GetRequiredService<INotificationChannelRepository>();
                    foreach (var channel in written)
                    {
                        await channels.AddAsync(channel);
                    }

                    return true;
                });

                var reread = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var channels = sp.GetRequiredService<INotificationChannelRepository>();
                    var list = new List<NotificationChannelAggregate>();
                    foreach (var channel in written)
                    {
                        list.Add(await channels.GetByIdAsync(channel.Id));
                    }

                    return list;
                });

                for (var i = 0; i < written.Length; i++)
                {
                    Assert.NotNull(reread[i]);
                    Assert.Equal(written[i].Kind, reread[i].Kind);
                    Assert.Equal(written[i].SiteId, reread[i].SiteId);
                    Assert.Equal(written[i].Name, reread[i].Name);
                    Assert.Equal(written[i].NameNormalized, reread[i].NameNormalized);
                    Assert.Equal(written[i].Enabled, reread[i].Enabled);
                    Assert.Equal(written[i].Transport.Value, reread[i].Transport.Value);
                    Assert.Equal(written[i].RateLimit?.Value, reread[i].RateLimit?.Value);
                }

                // ---- resolve + deliver + record, all from the RE-READ aggregates ------------------------------------
                var fake = new FakeTransportChannel(ChannelKind.Sms, "fake-sms");
                var requestId = Guid.NewGuid();
                var writtenRequest = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var resolver = new NotificationChannelResolver(
                        sp.GetServices<INotificationChannel>().Append(fake), sp.GetRequiredService<ILogger<NotificationChannelResolver>>());
                    var delivery = new NotificationChannelDelivery(resolver, sp.GetRequiredService<ILogger<NotificationChannelDelivery>>());
                    var request = NotificationRequestAggregate.Create(requestId, TenantId.Default, siteId, "generic.message",
                        recipients: [new Recipient(RecipientKind.User, hostUser.UserId.ToString(CultureInfo.InvariantCulture)), new Recipient(RecipientKind.Email, "ops@example.com")],
                        requestedChannels: [ChannelKind.Email, ChannelKind.InApp, ChannelKind.Sms],
                        parameters: [new NotificationParameter("subject", marker)],
                        createdOnUtc: FirstAttempt.AddMinutes(-1));

                    var byId = reread.ToDictionary(channel => channel.Id);
                    var external = new Recipient(RecipientKind.Email, "ops@example.com");
                    var user = new Recipient(RecipientKind.User, hostUser.UserId.ToString(CultureInfo.InvariantCulture));
                    var queuedPayload = new Payload("Queued " + marker, "<p>Queued &lt;b&gt; body</p>");
                    var inboxPayload = new Payload("Inbox " + marker, "Inbox body");

                    await delivery.DeliverAsync(siteId, request, byId[email.Id], external, queuedPayload, FirstAttempt);
                    await delivery.DeliverAsync(siteId, request, byId[inApp.Id], user, inboxPayload, FirstAttempt.AddSeconds(1));
                    await delivery.DeliverAsync(siteId, request, byId[fakeSms.Id], user, inboxPayload, FirstAttempt.AddSeconds(2));
                    await delivery.DeliverAsync(siteId, request, byId[unregistered.Id], user, inboxPayload, FirstAttempt.AddSeconds(3));
                    await delivery.DeliverAsync(siteId, request, byId[disabled.Id], external, queuedPayload, FirstAttempt.AddSeconds(4));
                    await delivery.DeliverAsync(siteId, request, byId[inApp.Id], external, inboxPayload, FirstAttempt.AddSeconds(5));

                    await sp.GetRequiredService<INotificationRequestRepository>().AddAsync(request);
                    return request;
                });

                Assert.Equal(ExpectedAttempts, Canonical(writtenRequest.Attempts));
                var fakeSend = Assert.Single(fake.Sent);
                Assert.Equal(siteId, fakeSend.SiteId);
                Assert.Equal(fakeSms.Id, fakeSend.Channel.Id);

                var persisted = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<INotificationRequestRepository>().GetByIdAsync(requestId));
                Assert.NotNull(persisted);
                var persistedCanonical = Canonical(persisted.Attempts);
                output.WriteLine($"[{store.Name}] persisted attempts:\n{persistedCanonical}");
                Assert.Equal(ExpectedAttempts, persistedCanonical);
                Assert.Equal(writtenRequest.Attempts.Select(attempt => attempt.Id), persisted.Attempts.Select(attempt => attempt.Id));
                Assert.Equal(writtenRequest.Status, persisted.Status);
                Assert.Equal(NotificationStatus.Sent, persisted.Status);

                // ---- the rows, through the EXISTING notification repository -----------------------------------------
                var queue = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(siteId, -1, -1)).ToList());
                output.WriteLine($"[{store.Name}] queue rows: {string.Join(" | ", queue.Select(row => $"{row.NotificationId}:{row.Subject}:to={row.ToUserId}/{row.ToEmail}:delivered={row.IsDelivered}:sendOn={row.SendOn}"))}");
                var queued = Assert.Single(queue, row => row.Subject == "Queued " + marker);
                Assert.Equal("ops@example.com", queued.ToEmail);
                Assert.Null(queued.ToUserId);
                Assert.Null(queued.FromUserId);
                Assert.False(queued.IsDelivered);
                Assert.Equal("<p>Queued &lt;b&gt; body</p>", queued.Body);
                Assert.DoesNotContain(queue, row => row.Subject == "Inbox " + marker); // an in-app row is never in the SMTP queue

                var mailbox = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(siteId, -1, hostUser.UserId)).ToList());
                output.WriteLine($"[{store.Name}] mailbox rows for user {hostUser.UserId}: {string.Join(" | ", mailbox.Select(row => $"{row.NotificationId}:{row.Subject}:from={row.FromUserId}:delivered={row.IsDelivered}"))}");
                var inbox = Assert.Single(mailbox, row => row.Subject == "Inbox " + marker);
                Assert.True(inbox.IsDelivered);
                Assert.NotNull(inbox.DeliveredOn);
                Assert.Null(inbox.FromUserId);
                Assert.Equal(hostUser.UserId, inbox.ToUserId);
                Assert.Equal("Inbox body", inbox.Body);

                // A full-length (256) subject persists on the live store - the bound the e-mail channel enforces.
                var longSubject = new string('s', EmailNotificationChannel.MaxSubjectLength - marker.Length) + marker;
                var longResult = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new EmailNotificationChannel(sp.GetRequiredService<INotificationRepository>()).SendAsync(
                        new ChannelDeliveryRequest(siteId, email, new Recipient(RecipientKind.Email, "ops@example.com"), new Payload(longSubject, "b"))));
                Assert.True(longResult.Succeeded, longResult.Error);
                var longQueue = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(siteId, -1, -1)).ToList());
                Assert.Contains(longQueue, row => row.Subject == longSubject);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }

    /// <summary>The same drive on RavenDB. Fails, never skips, when RavenDB is unreachable.</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenNotificationChannelSeamConformanceTests
    {
        private readonly ITestOutputHelper _output;

        public RavenNotificationChannelSeamConformanceTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public Task The_channel_seam_round_trips_resolves_delivers_and_records_identically_on_RavenDB() =>
            NotificationChannelSeamConformanceTests.DriveAsync(RavenGateStore.StoreName, _output);
    }
}
