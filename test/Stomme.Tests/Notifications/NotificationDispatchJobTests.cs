using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// <c>NotificationDispatchJobTests</c>, messaging OFF (no <c>IEventBus</c>): the Job-backed
    /// pipeline is authoritative. Queued -&gt; delivered; a failing channel backs off (30 s, then 60 s) on the scalar retry
    /// state; retries stop at 3 and the request completes Failed; quiet hours re-arm <c>NextAttemptOn</c> without consuming
    /// a retry; a digest hold is flushed by the hourly pass; the retention purge removes only terminal requests older than
    /// the site setting. Carries the 6 facts ported from the predecessor platform <c>NotificationDigestJobTests.cs</c> (harness swapped: the
    /// File store + the job's per-site pass for the mediator command and <c>NotificationDigestJob</c>). The last case
    /// DRIVES the real <see cref="NotificationDispatchJob"/> on installed SQLite, PostgreSQL and RavenDB hosts.
    /// </summary>
    public sealed class NotificationDispatchJobTests
    {
        private const string TemplateKey = "digest.test";
        private const string WebhookProviderKey = "cap-webhook";

        // ---- the pipeline policy --------------------------------------------------------------------------------

        /// <summary>
        /// A request another writer changed while the pass held it - the erasure of its recipient, which deletes or
        /// scrubs the row from its own job - is left to that write, and the site's other requests are still delivered. Found in
        /// the erasure drive's log: the conflict escaped the pass and stopped every delivery of the site for the minute.
        /// </summary>
        [Fact]
        public async Task A_request_another_writer_changed_during_the_pass_is_left_to_it_and_the_others_are_delivered()
        {
            var lost = RequestFor(Email(), createdOn: NotificationPipelineHarness.Start.AddMinutes(-1));
            var other = RequestFor(Email());
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            await using var harness = await NotificationPipelineHarness.StartAsync([webhook], configure: services =>
            {
                var original = services.Last(descriptor => descriptor.ServiceType == typeof(INotificationRequestRepository));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(typeof(INotificationRequestRepository), provider =>
                {
                    var inner = original.ImplementationFactory is not null
                        ? (INotificationRequestRepository)original.ImplementationFactory(provider)
                        : (INotificationRequestRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                    return ConflictingRequests.Wrap(inner, lost.Id);
                }, original.Lifetime));
            });
            await harness.AddAsync(Template());
            await harness.AddAsync(WebhookChannel());
            await harness.AddAsync(lost, other);

            var result = await harness.RunSiteAsync();

            var reread = (await harness.GetAsync<NotificationRequestAggregate>(other.Id))!;
            Assert.Equal(NotificationStatus.Sent, reread.Status);
            Assert.NotNull(reread.CompletedOn);
            Assert.True(result.Delivered >= 1, $"delivered {result.Delivered}");
            // The conflicting request kept its stored state: the write that won is not overwritten.
            Assert.Null((await harness.GetAsync<NotificationRequestAggregate>(lost.Id))!.CompletedOn);
        }

        /// <summary>The request repository with one request's save answered as a concurrent writer's change would answer it.</summary>
        public class ConflictingRequests : System.Reflection.DispatchProxy
        {
            private INotificationRequestRepository _inner = null!;
            private Guid _conflictOn;

            public static INotificationRequestRepository Wrap(INotificationRequestRepository inner, Guid conflictOn)
            {
                var proxy = Create<INotificationRequestRepository, ConflictingRequests>();
                var self = (ConflictingRequests)(object)proxy;
                self._inner = inner;
                self._conflictOn = conflictOn;
                return proxy;
            }

            protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
            {
                if (method!.Name == nameof(INotificationRequestRepository.UpdateAsync) && args?[0] is NotificationRequestAggregate request && request.Id == _conflictOn)
                {
                    return Task.FromException(new Stomme.Persistence.Abstractions.ConcurrencyConflictException(nameof(NotificationRequestAggregate), request.Id.ToString()));
                }

                try
                {
                    return method.Invoke(_inner, args);
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    throw ex.InnerException;
                }
            }
        }

        [Fact]
        public async Task A_queued_request_is_delivered_by_one_pass_and_completes()
        {
            var (harness, webhook) = await StartAsync();
            await using var _ = harness;
            var request = RequestFor(Email());
            await harness.AddAsync(request);

            var result = await harness.RunSiteAsync();

            Assert.Single(webhook.Sent);
            Assert.Equal(1, result.Delivered);
            var reread = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(NotificationStatus.Sent, reread.Status);
            Assert.Equal(NotificationPipelineHarness.Start, reread.CompletedOn);
            Assert.Equal(0, reread.AttemptCount);

            // Completed work is never selected again.
            await harness.RunSiteAsync();
            Assert.Single(webhook.Sent);
        }

        [Fact]
        public async Task A_failing_channel_backs_off_thirty_seconds_then_sixty_and_is_not_selected_early()
        {
            var (harness, webhook) = await StartAsync();
            await using var _ = harness;
            webhook.Result = ChannelDeliveryResult.Fail("transport error");
            var request = RequestFor(Email());
            await harness.AddAsync(request);

            await harness.RunSiteAsync();
            var first = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(1, first.AttemptCount);
            Assert.Equal(NotificationPipelineHarness.Start.AddSeconds(30), first.NextAttemptOn);
            Assert.Null(first.CompletedOn);

            harness.Clock.Advance(TimeSpan.FromSeconds(29));
            await harness.RunSiteAsync();
            Assert.Single(webhook.Sent); // not due yet

            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await harness.RunSiteAsync();
            var second = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(2, webhook.Sent.Count);
            Assert.Equal(2, second.AttemptCount);
            Assert.Equal(harness.Clock.UtcNow.AddSeconds(60), second.NextAttemptOn);
        }

        [Fact]
        public void The_backoff_doubles_from_thirty_seconds_and_caps_at_one_hour()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), NotificationDispatchProcessor.BackoffFor(1));
            Assert.Equal(TimeSpan.FromSeconds(60), NotificationDispatchProcessor.BackoffFor(2));
            Assert.Equal(TimeSpan.FromSeconds(120), NotificationDispatchProcessor.BackoffFor(3));
            Assert.Equal(TimeSpan.FromHours(1), NotificationDispatchProcessor.BackoffFor(8));
            Assert.Equal(TimeSpan.FromHours(1), NotificationDispatchProcessor.BackoffFor(30));
            Assert.Equal(3, NotificationDispatchProcessor.MaxRetries);
        }

        [Fact]
        public async Task Retries_stop_after_three_and_the_request_completes_Failed()
        {
            var (harness, webhook) = await StartAsync();
            await using var _ = harness;
            webhook.Result = ChannelDeliveryResult.Fail("transport error");
            var request = RequestFor(Email());
            await harness.AddAsync(request);

            for (var pass = 0; pass < 6; pass++)
            {
                await harness.RunSiteAsync();
                harness.Clock.Advance(TimeSpan.FromHours(1));
            }

            var reread = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(4, webhook.Sent.Count); // the initial pass + 3 retries
            Assert.Equal(4, reread.Attempts.Count);
            Assert.Equal(NotificationDispatchProcessor.MaxRetries, reread.AttemptCount);
            Assert.Equal(NotificationStatus.Failed, reread.Status);
            Assert.NotNull(reread.CompletedOn);
        }

        [Fact]
        public async Task Quiet_hours_defer_to_the_window_end_without_consuming_a_retry_then_deliver()
        {
            var (harness, webhook) = await StartAsync();
            await using var _ = harness;
            harness.Clock.Set(new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc));
            await harness.AddAsync(NotificationPipelineHarness.Preference(11, quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.Zero)));
            var request = RequestFor(NotificationPipelineHarness.User(11));
            await harness.AddAsync(request);

            var result = await harness.RunSiteAsync();

            var deferred = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(1, result.Deferred);
            Assert.Empty(webhook.Sent);
            Assert.Empty(deferred.Attempts);
            Assert.Equal(0, deferred.AttemptCount);
            Assert.Equal(new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc), deferred.NextAttemptOn);
            Assert.Equal(NotificationStatus.Queued, deferred.Status);

            harness.Clock.Set(new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc));
            await harness.RunSiteAsync();
            Assert.Single(webhook.Sent);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!.Status);
        }

        [Fact]
        public async Task The_hourly_pass_runs_at_most_once_an_hour_and_records_when_it_ran()
        {
            var (harness, _) = await StartAsync();
            await using var _disp = harness;

            Assert.True((await harness.RunSiteAsync()).HourlyPassRan);
            Assert.Equal(NotificationPipelineHarness.Start, harness.Settings.LastHourlyPass[NotificationPipelineHarness.SiteId]);

            harness.Clock.Advance(TimeSpan.FromMinutes(59));
            Assert.False((await harness.RunSiteAsync()).HourlyPassRan);

            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await harness.RunSiteAsync()).HourlyPassRan);
            Assert.Equal(NotificationPipelineHarness.Start.AddHours(1), harness.Settings.LastHourlyPass[NotificationPipelineHarness.SiteId]);
        }

        [Fact]
        public async Task The_retention_purge_removes_only_terminal_requests_older_than_the_site_setting()
        {
            var (harness, _) = await StartAsync();
            await using var _disp = harness;
            harness.Settings.Retention[NotificationPipelineHarness.SiteId] = TimeSpan.FromDays(10);
            var now = NotificationPipelineHarness.Start;

            var oldSent = RequestFor(Email(), createdOn: now.AddDays(-40));
            oldSent.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Succeeded, now.AddDays(-40));
            oldSent.Complete(now.AddDays(-40));
            var oldFailed = RequestFor(Email(), createdOn: now.AddDays(-40));
            oldFailed.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Failed, now.AddDays(-39), "x");
            oldFailed.Complete(now.AddDays(-11));
            var recentSent = RequestFor(Email(), createdOn: now.AddDays(-9));
            recentSent.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Succeeded, now.AddDays(-9));
            recentSent.Complete(now.AddDays(-9));
            var oldInFlight = RequestFor(Email(), createdOn: now.AddDays(-40));
            oldInFlight.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Failed, now.AddDays(-40), "x");
            oldInFlight.ScheduleRetry(now.AddDays(1)); // still in flight - never purged
            var otherSite = NotificationRequestAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId + 1, TemplateKey,
                [Email()], [ChannelKind.Webhook], createdOnUtc: now.AddDays(-40));
            otherSite.Complete(now.AddDays(-40));
            await harness.AddAsync(oldSent, oldFailed, recentSent, oldInFlight, otherSite);

            var result = await harness.RunSiteAsync();

            Assert.True(result.HourlyPassRan);
            Assert.Equal(2, result.Purged);
            Assert.Null(await harness.GetAsync<NotificationRequestAggregate>(oldSent.Id));
            Assert.Null(await harness.GetAsync<NotificationRequestAggregate>(oldFailed.Id));
            Assert.NotNull(await harness.GetAsync<NotificationRequestAggregate>(recentSent.Id));
            Assert.NotNull(await harness.GetAsync<NotificationRequestAggregate>(oldInFlight.Id));
            Assert.NotNull(await harness.GetAsync<NotificationRequestAggregate>(otherSite.Id)); // another site's retention is its own
        }

        [Fact]
        public async Task A_missing_template_fails_the_attempt_and_retries()
        {
            var (harness, webhook) = await StartAsync();
            await using var _ = harness;
            var request = NotificationPipelineHarness.Request("no.such.template", [Email()], [ChannelKind.Webhook]);
            await harness.AddAsync(request);

            await harness.RunSiteAsync();

            var reread = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Empty(webhook.Sent);
            var attempt = Assert.Single(reread.Attempts);
            Assert.Equal(DeliveryStatus.Failed, attempt.Status);
            Assert.Contains("no.such.template", attempt.Error);
            Assert.Equal(1, reread.AttemptCount);
        }

        [Fact]
        public async Task A_render_error_is_a_failed_attempt_never_a_crashed_pass()
        {
            var (harness, webhook) = await StartAsync(seedTemplate: false);
            await using var _ = harness;
            await harness.AddAsync(NotificationPipelineHarness.Template(TemplateKey, "Subject", "{% for i in (1..100000000) %}x{% endfor %}"));
            var request = RequestFor(Email());
            var healthy = NotificationPipelineHarness.Request("healthy", [Email()], [ChannelKind.Webhook]);
            await harness.AddAsync(NotificationPipelineHarness.Template("healthy", "S", "B"));
            await harness.AddAsync(request, healthy);

            await harness.RunSiteAsync();

            var failed = (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!;
            Assert.Equal(DeliveryStatus.Failed, Assert.Single(failed.Attempts).Status);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(healthy.Id))!.Status);
            Assert.Single(webhook.Sent);
        }

        // ---- ported: the predecessor platform NotificationDigestJobTests (6) -------------------------------------------------------

        [Fact]
        public async Task DigestMode_SuppressesImmediateDelivery_RecordsDigestQueuedAndKeepsQueued()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userId = 21;
            await SeedAsync(harness, Template(), WebhookChannel(), DigestPreference(userId));
            var id = await SeedRequestForUserAsync(harness, userId);

            var result = await harness.RunSiteAsync();

            // Provider must not have been called.
            Assert.Empty(webhook.Sent);
            // Outcome signals digest queuing (harness swap: the pass result, not the predecessor platform's DeliveryOutcome).
            Assert.Equal(1, result.DigestHeld);
            // No retry consumed (the predecessor platform: no retry message scheduled).
            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(0, request.AttemptCount);
            // Request is still Queued with a DigestQueued attempt recorded, and held for the flush.
            Assert.Equal(NotificationStatus.Queued, request.Status);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.Webhook && a.Status == DeliveryStatus.DigestQueued);
            Assert.NotNull(request.DigestDeferredUntil);
        }

        [Fact]
        public async Task DigestJob_FlushesDigestQueuedRequest_MarksSent()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userId = 21;
            await SeedAsync(harness, Template(), WebhookChannel(), DigestPreference(userId));
            var id = await SeedRequestForUserAsync(harness, userId);

            // Suppress immediate delivery.
            await harness.RunSiteAsync();
            Assert.Empty(webhook.Sent);

            // Run the digest flush (harness swap: the next hourly pass of the dispatch job).
            harness.Clock.Advance(TimeSpan.FromHours(1));
            var result = await harness.RunSiteAsync();

            // Provider should now have received the delivery.
            Assert.Single(webhook.Sent);
            Assert.Equal(1, result.Flushed);
            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status);
        }

        [Fact]
        public async Task ImmediateMode_DeliversOnDispatch_DigestJobSkipsIt()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userId = 21;
            await SeedAsync(harness, Template(), WebhookChannel(), ImmediatePreference(userId));
            var id = await SeedRequestForUserAsync(harness, userId);

            var first = await harness.RunSiteAsync();

            // Delivered immediately, not digest-queued.
            Assert.Equal(0, first.DigestHeld);
            Assert.Single(webhook.Sent);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(id))!.Status);

            // Digest flush finds nothing to do.
            webhook.Sent.Clear();
            harness.Clock.Advance(TimeSpan.FromHours(1));
            var flush = await harness.RunSiteAsync();

            Assert.Empty(webhook.Sent);
            Assert.True(flush.HourlyPassRan);
            Assert.Equal(0, flush.Flushed);
        }

        [Fact]
        public async Task DigestJob_ProcessesAllPendingRequests_AcrossMultipleUsers()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userA = 31;
            var userB = 32;
            await SeedAsync(harness, Template(), WebhookChannel(), DigestPreference(userA), DigestPreference(userB));

            var idA1 = await SeedRequestForUserAsync(harness, userA);
            var idA2 = await SeedRequestForUserAsync(harness, userA);
            var idB1 = await SeedRequestForUserAsync(harness, userB);

            await harness.RunSiteAsync();
            Assert.Empty(webhook.Sent);

            harness.Clock.Advance(TimeSpan.FromHours(1));
            var result = await harness.RunSiteAsync();

            Assert.Equal(3, webhook.Sent.Count);
            Assert.Equal(3, result.Flushed);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(idA1))!.Status);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(idA2))!.Status);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(idB1))!.Status);
        }

        [Fact]
        public async Task DigestJob_Idempotent_DoesNotRedeliverSentRequests()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userId = 21;
            await SeedAsync(harness, Template(), WebhookChannel(), DigestPreference(userId));
            await SeedRequestForUserAsync(harness, userId);
            await harness.RunSiteAsync();

            // First flush delivers.
            harness.Clock.Advance(TimeSpan.FromHours(1));
            await harness.RunSiteAsync();
            Assert.Single(webhook.Sent);

            // Second flush: the request is Sent and no longer held.
            webhook.Sent.Clear();
            harness.Clock.Advance(TimeSpan.FromHours(1));
            var second = await harness.RunSiteAsync();

            Assert.Empty(webhook.Sent);
            Assert.True(second.HourlyPassRan);
            Assert.Equal(0, second.Flushed);
        }

        [Fact]
        public async Task NonUserRecipient_BypassesDigestSuppression_DeliversImmediately()
        {
            var (harness, webhook) = await BuildHostAsync();
            await using var _ = harness;
            var userId = 21;
            await SeedAsync(harness, Template(), WebhookChannel(), DigestPreference(userId));

            // Request targets BOTH a digest-mode user AND a literal email address.
            var request = NotificationPipelineHarness.Request(TemplateKey,
                recipients: [NotificationPipelineHarness.User(userId), new Recipient(RecipientKind.Email, "ops@example.com")],
                channels: [ChannelKind.Webhook]);
            await harness.AddAsync(request);

            var result = await harness.RunSiteAsync();

            // Non-user recipient forces immediate delivery. Provider fires once per recipient: 2 sends total.
            Assert.Equal(0, result.DigestHeld);
            Assert.Equal(2, webhook.Sent.Count);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(request.Id))!.Status);
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static async Task<(NotificationPipelineHarness Harness, FakeTransportChannel Webhook)> StartAsync(bool seedTemplate = true, bool seedChannel = true)
        {
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            var harness = await NotificationPipelineHarness.StartAsync([webhook]);
            if (seedTemplate)
            {
                await harness.AddAsync(Template());
            }

            if (seedChannel)
            {
                await harness.AddAsync(WebhookChannel());
            }

            return (harness, webhook);
        }

        // the predecessor platform's BuildHost: the capturing webhook provider only; SeedAsync adds the template, channel and preferences.
        private static Task<(NotificationPipelineHarness Harness, FakeTransportChannel Webhook)> BuildHostAsync() =>
            StartAsync(seedTemplate: false, seedChannel: false);

        private static Recipient Email() => new(RecipientKind.Email, "ops@example.com");

        private static NotificationRequestAggregate RequestFor(Recipient recipient, DateTime? createdOn = null) =>
            NotificationRequestAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId, TemplateKey,
                [recipient], [ChannelKind.Webhook], createdOnUtc: createdOn ?? NotificationPipelineHarness.Start);

        private static NotificationTemplateAggregate Template() =>
            NotificationPipelineHarness.Template(TemplateKey, "Digest Subject", "Digest Body");

        private static NotificationChannelAggregate WebhookChannel() =>
            NotificationPipelineHarness.Channel(ChannelKind.Webhook, WebhookProviderKey, "Webhook");

        private static NotificationPreferenceAggregate DigestPreference(int userId) =>
            NotificationPipelineHarness.Preference(userId, DigestMode.Digest);

        private static NotificationPreferenceAggregate ImmediatePreference(int userId) =>
            NotificationPipelineHarness.Preference(userId, DigestMode.Immediate);

        private static async Task SeedAsync(NotificationPipelineHarness harness, NotificationTemplateAggregate template,
            NotificationChannelAggregate channel, params NotificationPreferenceAggregate[] preferences)
        {
            await harness.AddAsync(template);
            await harness.AddAsync(channel);
            await harness.AddAsync(preferences);
        }

        private static async Task<Guid> SeedRequestForUserAsync(NotificationPipelineHarness harness, int userId)
        {
            var request = NotificationPipelineHarness.Request(TemplateKey, [NotificationPipelineHarness.User(userId)], [ChannelKind.Webhook]);
            await harness.AddAsync(request);
            return request.Id;
        }
    }

    /// <summary>
    /// The real <see cref="NotificationDispatchJob"/> DRIVEN on an installed host per store (D30 bar): a Shared-port
    /// dispatch through the production dispatcher with the production recipient constraint (the site's REAL users and
    /// settings), then <c>ExecuteJobAsync</c> exactly as <c>HostedServiceBase</c> calls it (a job scope with the tenant set,
    /// no <c>HttpContext</c>, no <c>IEventBus</c>) delivers over the SHIPPED in-app channel into the inherited mailbox,
    /// with the dispatch parameters rendered - read back in a NEW scope. PostgreSQL and RavenDB FAIL rather than skip.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class InstalledNotificationDispatchJobTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public InstalledNotificationDispatchJobTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_dispatch_job_delivers_a_shared_port_dispatch_with_the_recipient_constraint_enforced(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            // Every hosted service EXCEPT the jobs: the test runs the dispatch job itself, so the per-minute loop must not race it.
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var siteId = alias.SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var marker = "job-" + Guid.NewGuid().ToString("N");

                // The boot path created generic.message on this site (the reconciler); configure an in-app channel.
                await GateDrive.InTenantAsync(app, Host, async (sp, a) =>
                {
                    Assert.NotNull(await sp.GetRequiredService<INotificationTemplateRepository>().GetByKeyAsync(a.SiteId, "generic.message"));
                    await sp.GetRequiredService<INotificationChannelRepository>().AddAsync(NotificationChannelAggregate.Create(
                        Guid.NewGuid(), TenantId.Default, a.SiteId, ChannelKind.InApp, "Inbox", new TransportSettings(ChannelKind.InApp, null, null, [])));
                    return true;
                });

                // ---- the recipient constraint against the site's REAL users and settings -------------------------
                var external = new NotificationDispatchRequest("generic.message",
                    [new DispatchRecipient(NotificationRecipientKind.Email, "ops@example.com")], [NotificationChannelKind.Email],
                    [new DispatchParameter("subject", "s"), new DispatchParameter("body", "b")]);
                await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() =>
                    GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(external)));
                await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() =>
                    GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(external with
                    {
                        Recipients = [new DispatchRecipient(NotificationRecipientKind.Endpoint, "https://hooks.example.com/relay")],
                    })));
                await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() =>
                    GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(external with
                    {
                        Recipients = [new DispatchRecipient(NotificationRecipientKind.User, "987654")], // no such user on this site
                    })));

                await GateDrive.InTenantAsync(app, Host, async (sp, a) =>
                    await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Site, EntityId = a.SiteId,
                        SettingName = NotificationSiteSettingNames.ExternalRecipients, SettingValue = "True", IsPrivate = true,
                    }));
                var acceptedExternal = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(external));
                Assert.NotEqual(Guid.Empty, acceptedExternal);

                // ---- the stub dependent dispatches to the host user; the JOB delivers in a later scope -------------
                var id = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    new Fixtures.DispatchTestModule.DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>())
                        .CloudUsageThresholdAsync(hostUserId, 1.5m, 1m));
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    // A distinguishing parameter: dispatched now, rendered only at job time.
                    var requests = sp.GetRequiredService<INotificationRequestRepository>();
                    var queued = await requests.GetByIdAsync(id);
                    Assert.Equal(NotificationStatus.Queued, queued!.Status);
                    return true;
                });
                var markedId = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(
                    new NotificationDispatchRequest("generic.message",
                        [new DispatchRecipient(NotificationRecipientKind.User, hostUserId.ToString(CultureInfo.InvariantCulture))], [NotificationChannelKind.InApp],
                        [new DispatchParameter("subject", marker), new DispatchParameter("body", "<b>" + marker + "</b>")])));

                var notes = await RunJobAsync(app);
                _output.WriteLine($"[{storeName}] job notes: {notes}");

                var mailbox = await GateDrive.InTenantAsync(app, Host, async (sp, a) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(a.SiteId, -1, hostUserId)).ToList());
                var delivered = Assert.Single(mailbox, row => row.Subject == marker);
                Assert.Equal("&lt;b&gt;" + marker + "&lt;/b&gt;", delivered.Body); // the persisted parameter, HTML-encoded for in-app
                Assert.True(delivered.IsDelivered);
                Assert.Null(delivered.FromUserId);

                var requestsAfter = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var requests = sp.GetRequiredService<INotificationRequestRepository>();
                    return new[] { await requests.GetByIdAsync(markedId), await requests.GetByIdAsync(id), await requests.GetByIdAsync(acceptedExternal) };
                });
                _output.WriteLine($"[{storeName}] requests: {string.Join(" | ", requestsAfter.Select(r => $"{r!.Id}:{r.Status}:attempts={r.Attempts.Count}:retries={r.AttemptCount}:completed={r.CompletedOn}"))}");
                Assert.Equal(NotificationStatus.Sent, requestsAfter[0]!.Status);
                Assert.NotNull(requestsAfter[0]!.CompletedOn);
                Assert.Equal(NotificationStatus.Sent, requestsAfter[1]!.Status);
                // The site had no e-mail channel: its first e-mail creates the default one, so the external request
                // is handed to the site's mail queue and completes, rather than failing and retrying until an admin made one.
                Assert.Equal(NotificationStatus.Sent, requestsAfter[2]!.Status);
                Assert.NotNull(requestsAfter[2]!.CompletedOn);
                var emailChannels = await GateDrive.InTenantAsync(app, Host, async (sp, a) =>
                    (await sp.GetRequiredService<INotificationChannelRepository>().ListBySiteAsync(a.SiteId)).Where(c => c.Kind == ChannelKind.Email).ToList());
                Assert.Equal(NotificationDispatchProcessor.DefaultEmailChannelName, Assert.Single(emailChannels).Name);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>ExecuteJobAsync exactly as <c>HostedServiceBase</c> invokes it: one job scope, the tenant set, no HttpContext.</summary>
        internal static async Task<string> RunJobAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var tenants = await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync();
            var notes = string.Empty;
            foreach (var tenant in tenants)
            {
                scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                var job = new NotificationDispatchJob(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
                notes += await job.ExecuteJobAsync(scope.ServiceProvider);
            }

            return notes;
        }
    }
}
