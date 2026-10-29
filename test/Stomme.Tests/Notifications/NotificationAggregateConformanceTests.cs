using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// <c>NotificationAggregateConformanceTests</c>: all four notification aggregates, with EVERY
    /// value object populated, every collection non-empty and a request carrying two <see cref="DeliveryAttempt"/>s,
    /// round-trip member for member on EVERY module-aggregate provider - EF (SQLite), EF (PostgreSQL), RavenDB and File
    /// - through the module's own <c>ServerStartup</c> and closed repositories, plus the provider-neutral predicates the
    /// dispatch pipeline will use (due work, digest flush, site scoping, the normalized key/name lookups).
    /// <para>
    /// <b>Placement.</b> The persistence conformance project has no File-store module fixture; the EF/Raven/File
    /// module-aggregate harness lives HERE (Guestbook, the first collection suite), so this suite reuses its shape in <c>Stomme.Tests</c>.
    /// </para>
    /// <para>
    /// <b>DateTime Kind (the SQLite trap).</b> SQLite materializes a scalar DateTime column as Unspecified, RavenDB and
    /// the JSON collection column as Utc. The aggregates RE-STAMP every instant UTC on read (<c>UtcInstant.Stamp</c>
    /// in the property getter), so this suite asserts BOTH the instant and <c>Kind == Utc</c> on every provider.
    /// Instants are fixed at millisecond precision (PostgreSQL <c>timestamptz</c> keeps microseconds).
    /// </para>
    /// PostgreSQL and RavenDB FAIL rather than skip when unreachable.
    /// </summary>
    public abstract class NotificationAggregateConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, 250, DateTimeKind.Utc);

        private ServiceProvider _services = default!;

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);
            _services = services.BuildServiceProvider();

            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        private async Task<T> InScopeAsync<TRepository, T>(Func<TRepository, Task<T>> action)
            where TRepository : notnull
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<TRepository>());
        }

        private Task AddAsync<TRepository, TAggregate>(TAggregate aggregate)
            where TRepository : Stomme.SharedKernel.Domain.IRepository<TAggregate, Guid>
            where TAggregate : Stomme.SharedKernel.Domain.AggregateRoot<Guid> =>
            InScopeAsync<TRepository, int>(async repository =>
            {
                await repository.AddAsync(aggregate);
                return 0;
            });

        // ---- builders: every value object populated, every collection non-empty ----

        private static NotificationRequestAggregate FullRequest(int siteId = 7, string key = "Pipeline.Failed", DateTime? createdOn = null)
        {
            var request = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, siteId, key,
                recipients:
                [
                    new Recipient(RecipientKind.User, "42"),
                    new Recipient(RecipientKind.Email, "ops@example.com"),
                    new Recipient(RecipientKind.Endpoint, "https://hooks.example.com/a:b"),
                ],
                requestedChannels: [ChannelKind.Email, ChannelKind.Webhook, ChannelKind.InApp],
                parameters:
                [
                    new NotificationParameter("name", "Ada \"the\" Countess"),
                    new NotificationParameter("html", "<script>alert(1)</script>"),
                ],
                severity: NotificationSeverity.Warning,
                topic: "pipeline:42",
                createdOnUtc: createdOn ?? Now.AddMinutes(-10));
            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, new DateTime(2026, 9, 14, 11, 51, 0, 123, DateTimeKind.Utc));
            request.RecordAttempt(ChannelKind.Webhook, DeliveryStatus.Failed, new DateTime(2026, 9, 14, 11, 52, 30, 456, DateTimeKind.Utc), "HTTP 503");
            request.ScheduleRetry(new DateTime(2026, 9, 14, 11, 53, 0, 789, DateTimeKind.Utc));
            request.DeferToDigest(new DateTime(2026, 9, 14, 13, 0, 0, DateTimeKind.Utc));
            request.Complete(new DateTime(2026, 9, 14, 11, 59, 59, 999, DateTimeKind.Utc));
            return request;
        }

        private static NotificationTemplateAggregate FullTemplate(int siteId = 7, string key = "Pipeline.Failed") =>
            NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, siteId, key,
                supportedChannels: [ChannelKind.Webhook, ChannelKind.Email, ChannelKind.InApp],
                contents:
                [
                    new LocalizedContent(new Locale("en-US"), new Content("Pipeline {{ name }} failed", "Run {{ runId }} <b>failed</b>.")),
                    new LocalizedContent(new Locale("sv-SE"), new Content("", "Körningen {{ runId }} misslyckades, \"igen\".")),
                ],
                placeholders: new PlaceholderSchema(["name", "runId"]));

        private static NotificationChannelAggregate FullChannel(int siteId = 7, string name = "Ops Webhook") =>
            NotificationChannelAggregate.Create(
                Guid.NewGuid(), TenantId.Default, siteId, ChannelKind.Webhook, name,
                new TransportSettings(ChannelKind.Webhook, "https://hooks.example.com/notify?x=1&y=2",
                    "notifications.channel.0f8fad5bd9cb469fa16570867728950e.credential",
                    [new TransportOption("header", "X-Ops"), new TransportOption("format", "{\"json\":true}")]),
                new RateLimit(10, TimeSpan.FromMinutes(5)));

        private static NotificationPreferenceAggregate FullPreference(int siteId = 7, int userId = 42)
        {
            var preference = NotificationPreferenceAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, userId);
            preference.Configure(
                acceptedChannels: [ChannelKind.Webhook, ChannelKind.Email],
                severityFilter: NotificationSeverityFilter.ErrorsOnly,
                mutedTopics: ["pipeline:9", "billing, monthly"],
                quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.FromHours(2)),
                digest: DigestMode.Digest,
                preferredLocale: "sv-SE");
            return preference;
        }

        /// <summary>The provider this fixture claims to exercise: an EF provider-name fragment, or the generic repository type name.</summary>
        protected abstract string ExpectedStore { get; }

        // Anti-vacuity: a green run must be a run against the NAMED store - never a silent fallback (e.g. a SQLite
        // sidecar standing in for PostgreSQL) - and, on EF, with all four aggregates in the shared model and the
        // collections mapped as columns (not left to EF discovery).
        [Fact]
        public async Task The_store_under_test_is_the_named_provider_with_all_four_aggregates_mapped()
        {
            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<Stomme.SharedKernel.Domain.IRepository<NotificationRequestAggregate, Guid>>();

            if (repository.GetType().Name.StartsWith("EfRepository", StringComparison.Ordinal))
            {
                var context = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>();
                Assert.Contains(ExpectedStore, context.Database.ProviderName, StringComparison.OrdinalIgnoreCase);
                foreach (var aggregate in new[] { typeof(NotificationRequestAggregate), typeof(NotificationTemplateAggregate), typeof(NotificationChannelAggregate), typeof(NotificationPreferenceAggregate) })
                {
                    Assert.NotNull(context.Model.FindEntityType(aggregate));
                }

                var request = context.Model.FindEntityType(typeof(NotificationRequestAggregate))!;
                foreach (var column in new[] { "Recipients", "RequestedChannels", "Parameters", "Attempts", "NextAttemptOn", "DigestDeferredUntil", "TemplateKeyNormalized" })
                {
                    Assert.NotNull(request.FindProperty(column));
                }

                Assert.Null(context.Model.FindEntityType(typeof(DeliveryAttempt)));
                // A real query per table: it throws if the table does not exist in THIS database (fresh per test, so empty).
                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<NotificationRequestAggregate>()));
                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<NotificationTemplateAggregate>()));
                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<NotificationChannelAggregate>()));
                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<NotificationPreferenceAggregate>()));
            }
            else
            {
                Assert.Contains(ExpectedStore, repository.GetType().Name, StringComparison.Ordinal);
            }
        }
        // ---- round-trips ----

        [Fact]
        public async Task A_request_with_every_value_object_every_collection_and_two_attempts_round_trips()
        {
            var written = FullRequest();
            await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(written);

            var reread = await InScopeAsync<INotificationRequestRepository, NotificationRequestAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            AssertSameRequest(written, reread!);
        }

        [Fact]
        public async Task A_template_with_localized_content_and_placeholders_round_trips()
        {
            var written = FullTemplate();
            await AddAsync<INotificationTemplateRepository, NotificationTemplateAggregate>(written);

            var reread = await InScopeAsync<INotificationTemplateRepository, NotificationTemplateAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            AssertSameTemplate(written, reread!);
        }

        [Fact]
        public async Task A_channel_with_transport_and_rate_limit_round_trips_and_a_null_rate_limit_stays_null()
        {
            var full = FullChannel();
            var bare = NotificationChannelAggregate.Create(
                Guid.NewGuid(), TenantId.Default, 7, ChannelKind.Email, "Mail", new TransportSettings(ChannelKind.Email, null, null, []));
            bare.Disable();
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(full);
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(bare);

            var fullReread = await InScopeAsync<INotificationChannelRepository, NotificationChannelAggregate?>(r => r.GetByIdAsync(full.Id));
            var bareReread = await InScopeAsync<INotificationChannelRepository, NotificationChannelAggregate?>(r => r.GetByIdAsync(bare.Id));

            Assert.NotNull(fullReread);
            AssertSameChannel(full, fullReread!);
            Assert.NotNull(bareReread);
            AssertSameChannel(bare, bareReread!);
            Assert.Null(bareReread!.RateLimit);
            Assert.False(bareReread.Enabled);
        }

        [Fact]
        public async Task A_preference_with_quiet_hours_round_trips_and_an_unconfigured_one_reads_back_permissive()
        {
            var full = FullPreference();
            var bare = NotificationPreferenceAggregate.Create(Guid.NewGuid(), TenantId.Default, 7, 43);
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(full);
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(bare);

            var fullReread = await InScopeAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate?>(r => r.GetByIdAsync(full.Id));
            var bareReread = await InScopeAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate?>(r => r.GetByIdAsync(bare.Id));

            Assert.NotNull(fullReread);
            AssertSamePreference(full, fullReread!);
            Assert.NotNull(bareReread);
            AssertSamePreference(bare, bareReread!);
            Assert.Null(bareReread!.QuietHours);
            Assert.Null(bareReread.PreferredLocale);
            Assert.Empty(bareReread.AcceptedChannels);
            Assert.Empty(bareReread.MutedTopics);
        }

        [Fact]
        public async Task A_request_mutated_after_load_persists_its_new_attempts_and_scalar_state_on_update()
        {
            var written = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, 7, "generic.message", [new Recipient(RecipientKind.User, "5")],
                [ChannelKind.Email, ChannelKind.InApp], createdOnUtc: Now.AddMinutes(-1));
            await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(written);

            await InScopeAsync<INotificationRequestRepository, int>(async repository =>
            {
                var loaded = await repository.GetByIdAsync(written.Id);
                loaded!.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, Now);
                loaded.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Skipped, Now.AddSeconds(1), "muted");
                loaded.ScheduleRetry(Now.AddSeconds(30));
                loaded.DeferToDigest(Now.AddHours(1));
                loaded.Complete(Now.AddSeconds(2));
                await repository.UpdateAsync(loaded);
                return 0;
            });

            var reread = await InScopeAsync<INotificationRequestRepository, NotificationRequestAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            Assert.Equal(NotificationStatus.Sent, reread!.Status);
            Assert.Equal(2, reread.Attempts.Count);
            Assert.Equal("muted", reread.Attempts[1].Error);
            Assert.Equal(1, reread.AttemptCount);
            AssertUtc(Now.AddSeconds(30), reread.NextAttemptOn);
            AssertUtc(Now.AddHours(1), reread.DigestDeferredUntil);
            AssertUtc(Now.AddSeconds(2), reread.CompletedOn);
        }

        // ---- the pipeline's FindAsync predicates ----

        // These predicates changed once Status became derived: the due-work predicate is no longer
        // "Status == Queued" - Status is DERIVED from the attempts, so a request whose first pass failed is Failed and a
        // retry would never be selected - but the scalar in-flight marker "CompletedOn == null", excluding digest holds
        // ("DigestDeferredUntil == null"), per SITE. Null checks on nullable DateTimes join the conformance set here.

        [Fact]
        public async Task Due_work_is_exactly_the_sites_in_flight_unheld_requests_whose_next_attempt_is_not_in_the_future()
        {
            var dueQueued = Bare(createdOn: Now.AddMinutes(-5));
            var dueAtTheInstant = Bare(createdOn: Now);
            var futureQueued = Bare(createdOn: Now.AddMinutes(-5));
            futureQueued.DeferUntil(Now.AddMinutes(1));
            var completedSent = Bare(createdOn: Now.AddMinutes(-5));
            completedSent.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, Now.AddMinutes(-4));
            completedSent.Complete(Now.AddMinutes(-4));
            var failedAwaitingRetry = Bare(createdOn: Now.AddMinutes(-5));
            failedAwaitingRetry.RecordAttempt(ChannelKind.Email, DeliveryStatus.Failed, Now.AddMinutes(-4), "refused");
            failedAwaitingRetry.ScheduleRetry(Now.AddSeconds(-1));
            var heldForDigest = Bare(createdOn: Now.AddMinutes(-5));
            heldForDigest.DeferToDigest(Now.AddHours(1));
            var otherSite = Bare(createdOn: Now.AddMinutes(-5), siteId: 8);
            foreach (var request in new[] { dueQueued, dueAtTheInstant, futureQueued, completedSent, failedAwaitingRetry, heldForDigest, otherSite })
            {
                await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(request);
            }

            var due = await InScopeAsync<INotificationRequestRepository, IReadOnlyList<NotificationRequestAggregate>>(r => r.ListDueAsync(7, Now));

            Assert.Equal(
                new[] { dueQueued.Id, dueAtTheInstant.Id, failedAwaitingRetry.Id }.OrderBy(id => id),
                due.Select(request => request.Id).OrderBy(id => id));
        }

        [Fact]
        public async Task Digest_due_work_is_exactly_the_sites_held_requests_whose_flush_is_not_in_the_future()
        {
            var held = Bare(createdOn: Now.AddHours(-1));
            held.DeferToDigest(Now.AddMinutes(-1));
            var heldLater = Bare(createdOn: Now.AddHours(-1));
            heldLater.DeferToDigest(Now.AddMinutes(30));
            var notHeld = Bare(createdOn: Now.AddHours(-1));
            var released = Bare(createdOn: Now.AddHours(-1));
            released.DeferToDigest(Now.AddMinutes(-2));
            released.ReleaseFromDigest();
            var heldOtherSite = Bare(createdOn: Now.AddHours(-1), siteId: 8);
            heldOtherSite.DeferToDigest(Now.AddMinutes(-1));
            foreach (var request in new[] { held, heldLater, notHeld, released, heldOtherSite })
            {
                await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(request);
            }

            var due = await InScopeAsync<INotificationRequestRepository, IReadOnlyList<NotificationRequestAggregate>>(r => r.ListDigestDueAsync(7, Now));

            Assert.Equal(held.Id, Assert.Single(due).Id);
        }

        [Fact]
        public async Task Purgeable_work_is_exactly_the_sites_completed_requests_older_than_the_cutoff()
        {
            var oldCompleted = Bare(createdOn: Now.AddDays(-40));
            oldCompleted.Complete(Now.AddDays(-31));
            var atTheCutoff = Bare(createdOn: Now.AddDays(-40));
            atTheCutoff.Complete(Now.AddDays(-30));
            var recentCompleted = Bare(createdOn: Now.AddDays(-40));
            recentCompleted.Complete(Now.AddDays(-1));
            var oldInFlight = Bare(createdOn: Now.AddDays(-40));
            var oldCompletedOtherSite = Bare(createdOn: Now.AddDays(-40), siteId: 8);
            oldCompletedOtherSite.Complete(Now.AddDays(-31));
            foreach (var request in new[] { oldCompleted, atTheCutoff, recentCompleted, oldInFlight, oldCompletedOtherSite })
            {
                await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(request);
            }

            var purgeable = await InScopeAsync<INotificationRequestRepository, IReadOnlyList<NotificationRequestAggregate>>(r => r.ListPurgeableAsync(7, Now.AddDays(-30)));

            Assert.Equal(oldCompleted.Id, Assert.Single(purgeable).Id);
        }

        [Fact]
        public async Task Every_site_scoped_read_returns_only_that_sites_aggregates()
        {
            await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(FullRequest(siteId: 7));
            await AddAsync<INotificationRequestRepository, NotificationRequestAggregate>(FullRequest(siteId: 8));
            await AddAsync<INotificationTemplateRepository, NotificationTemplateAggregate>(FullTemplate(siteId: 7));
            await AddAsync<INotificationTemplateRepository, NotificationTemplateAggregate>(FullTemplate(siteId: 8));
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(FullChannel(siteId: 7));
            var disabled = FullChannel(siteId: 7, name: "Disabled");
            disabled.Disable();
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(disabled);
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(FullChannel(siteId: 8));
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(FullPreference(siteId: 7));
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(FullPreference(siteId: 8));

            Assert.All(await InScopeAsync<INotificationRequestRepository, IReadOnlyList<NotificationRequestAggregate>>(r => r.ListBySiteAsync(7)),
                request => Assert.Equal(7, request.SiteId));
            Assert.Single(await InScopeAsync<INotificationRequestRepository, IReadOnlyList<NotificationRequestAggregate>>(r => r.ListBySiteAsync(8)));
            Assert.Single(await InScopeAsync<INotificationTemplateRepository, IReadOnlyList<NotificationTemplateAggregate>>(r => r.ListBySiteAsync(7)));
            Assert.Equal(2, (await InScopeAsync<INotificationChannelRepository, IReadOnlyList<NotificationChannelAggregate>>(r => r.ListBySiteAsync(7))).Count);
            var enabled = Assert.Single(await InScopeAsync<INotificationChannelRepository, IReadOnlyList<NotificationChannelAggregate>>(r => r.ListEnabledAsync(7)));
            Assert.Equal(7, enabled.SiteId);
            Assert.True(enabled.Enabled);
            Assert.Single(await InScopeAsync<INotificationPreferenceRepository, IReadOnlyList<NotificationPreferenceAggregate>>(r => r.ListBySiteAsync(8)));
        }

        [Fact]
        public async Task Key_name_and_user_lookups_normalize_both_sides_and_stay_inside_the_site()
        {
            var template = FullTemplate(siteId: 7, key: "Pipeline.Failed");
            await AddAsync<INotificationTemplateRepository, NotificationTemplateAggregate>(template);
            await AddAsync<INotificationTemplateRepository, NotificationTemplateAggregate>(FullTemplate(siteId: 8, key: "Pipeline.Failed"));
            var channel = FullChannel(siteId: 7, name: "Ops Webhook");
            await AddAsync<INotificationChannelRepository, NotificationChannelAggregate>(channel);
            var preference = FullPreference(siteId: 7, userId: 42);
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(preference);
            await AddAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate>(FullPreference(siteId: 7, userId: 420));

            // The raw FindAsync the repository runs: KeyNormalized == KEY.ToUpperInvariant().
            var raw = await InScopeAsync<INotificationTemplateRepository, IReadOnlyList<NotificationTemplateAggregate>>(r =>
                r.FindAsync(t => t.KeyNormalized == "pipeline.failed".ToUpperInvariant()));
            Assert.Equal(2, raw.Count);

            Assert.Equal(template.Id, (await InScopeAsync<INotificationTemplateRepository, NotificationTemplateAggregate?>(r => r.GetByKeyAsync(7, "  pipeline.FAILED ")))!.Id);
            Assert.Null(await InScopeAsync<INotificationTemplateRepository, NotificationTemplateAggregate?>(r => r.GetByKeyAsync(9, "pipeline.failed")));
            Assert.Equal(channel.Id, (await InScopeAsync<INotificationChannelRepository, NotificationChannelAggregate?>(r => r.GetByNameAsync(7, "ops webhook")))!.Id);
            Assert.Null(await InScopeAsync<INotificationChannelRepository, NotificationChannelAggregate?>(r => r.GetByNameAsync(8, "OPS WEBHOOK")));
            Assert.Equal(preference.Id, (await InScopeAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate?>(r => r.GetByUserAsync(7, 42)))!.Id);
            Assert.Null(await InScopeAsync<INotificationPreferenceRepository, NotificationPreferenceAggregate?>(r => r.GetByUserAsync(8, 42)));
        }

        private static NotificationRequestAggregate Bare(DateTime createdOn, int siteId = 7) =>
            NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, siteId, "generic.message", [new Recipient(RecipientKind.User, "5")], [ChannelKind.Email],
                createdOnUtc: createdOn);

        // ---- member-for-member comparison ----

        private static void AssertUtc(DateTime? expected, DateTime? actual)
        {
            Assert.Equal(expected, actual);
            if (actual is { } instant)
            {
                Assert.Equal(DateTimeKind.Utc, instant.Kind);
            }
        }

        private static void AssertSameRequest(NotificationRequestAggregate expected, NotificationRequestAggregate actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.TemplateKey, actual.TemplateKey);
            Assert.Equal(expected.TemplateKeyNormalized, actual.TemplateKeyNormalized);
            Assert.Equal(expected.Severity, actual.Severity);
            Assert.Equal(expected.Topic, actual.Topic);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.AttemptCount, actual.AttemptCount);
            AssertUtc(expected.NextAttemptOn, actual.NextAttemptOn);
            AssertUtc(expected.DigestDeferredUntil, actual.DigestDeferredUntil);
            AssertUtc(expected.CreatedOn, actual.CreatedOn);
            AssertUtc(expected.CompletedOn, actual.CompletedOn);
            Assert.Equal(expected.Recipients, actual.Recipients);
            Assert.Equal(expected.Recipients.Select(r => r.Value), actual.Recipients.Select(r => r.Value));
            Assert.Equal(expected.RequestedChannels, actual.RequestedChannels);
            Assert.Equal(expected.Parameters, actual.Parameters);
            Assert.Equal(
                expected.Attempts.Select(a => (a.Id, a.Channel, a.Status, a.AttemptedOn, a.Error)),
                actual.Attempts.Select(a => (a.Id, a.Channel, a.Status, a.AttemptedOn, a.Error)));
            Assert.All(actual.Attempts, a => Assert.Equal(DateTimeKind.Utc, a.AttemptedOn.Kind));
        }

        private static void AssertSameTemplate(NotificationTemplateAggregate expected, NotificationTemplateAggregate actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.Key, actual.Key);
            Assert.Equal(expected.KeyNormalized, actual.KeyNormalized);
            Assert.Equal(expected.SupportedChannels, actual.SupportedChannels);
            Assert.Equal(expected.Contents, actual.Contents);
            Assert.Equal(expected.Contents.Select(c => (c.Locale.Value, c.Content.Value)), actual.Contents.Select(c => (c.Locale.Value, c.Content.Value)));
            Assert.Equal(expected.Placeholders, actual.Placeholders);
            Assert.Equal(expected.Placeholders.Value, actual.Placeholders.Value);
        }

        private static void AssertSameChannel(NotificationChannelAggregate expected, NotificationChannelAggregate actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.NameNormalized, actual.NameNormalized);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.Equal(expected.Transport, actual.Transport);
            Assert.Equal(expected.Transport.Value, actual.Transport.Value);
            Assert.Equal(expected.RateLimit, actual.RateLimit);
            Assert.Equal(expected.RateLimit?.Value, actual.RateLimit?.Value);
        }

        private static void AssertSamePreference(NotificationPreferenceAggregate expected, NotificationPreferenceAggregate actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.UserId, actual.UserId);
            Assert.Equal(expected.AcceptedChannels, actual.AcceptedChannels);
            Assert.Equal(expected.SeverityFilter, actual.SeverityFilter);
            Assert.Equal(expected.MutedTopics, actual.MutedTopics);
            Assert.Equal(expected.QuietHours, actual.QuietHours);
            Assert.Equal(expected.QuietHours?.Value, actual.QuietHours?.Value);
            Assert.Equal(expected.Digest, actual.Digest);
            Assert.Equal(expected.PreferredLocale, actual.PreferredLocale);
        }
    }

    /// <summary>EF Core on a throwaway SQLite file, composed through the module startup.</summary>
    public sealed class SqliteNotificationAggregateConformanceTests : NotificationAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-notifications-conformance-{Guid.NewGuid():N}.db")};Pooling=True";

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = _connectionString,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            var connection = new SqliteConnectionStringBuilder(_connectionString);
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(connection.DataSource);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>EF Core on a LIVE PostgreSQL scratch database. Fails, never skips, when PostgreSQL is down.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresNotificationAggregateConformanceTests : NotificationAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_notifications_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The notification aggregate conformance suite FAILS rather than skips.");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = PostgresTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString(_database)));
            PostgresTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    /// <summary>The generic RavenRepository on a LIVE RavenDB, its own database per test. Fails, never skips.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenNotificationAggregateConformanceTests : NotificationAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-notifications-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-notifications-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
                })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            // Delete through a FRESH store: the container already disposed the shared one.
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: the next run's janitor sweep removes a leftover
            }
        }
    }

    /// <summary>The generic FileRepository in a throwaway directory - the third provider.</summary>
    public sealed class FileNotificationAggregateConformanceTests : NotificationAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-notifications-file-conformance-{Guid.NewGuid():N}");

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }
}
