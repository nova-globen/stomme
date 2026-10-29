using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The three self-service CQRS facts of the preference suite that were deferred (the other 13 are in
    /// <see cref="NotificationQuietHoursTests"/>): <c>SaveThenGet_RoundTripsPreferences</c>, <c>Save_Twice_UpdatesInPlace</c>,
    /// <c>Get_NoPreference_ReturnsPermissiveDefaults</c> - names kept; user ids are int; the harness is
    /// <see cref="NotificationCqrsHarness"/>. Below them, the Stomme additions for the view-own split: the send log, the
    /// caller's own list, send-test and retry.
    /// </summary>
    public sealed class NotificationPreferenceCqrsTests
    {
        [Fact]
        public async Task SaveThenGet_RoundTripsPreferences()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync(userId: 42);
            await using var _dispose = harness;

            await harness.SendAsync(new SaveMyNotificationPreferencesCommand(
                AcceptedChannels: new[] { NotificationChannelKind.Email, NotificationChannelKind.Webhook },
                SeverityFilter: NotificationSeverityPreference.ErrorsOnly,
                MutedTopics: new[] { "pipeline:9" },
                QuietHours: new NotificationQuietHoursInput(new TimeOnly(22, 0), new TimeOnly(6, 0), TimeSpan.FromHours(1)),
                Digest: NotificationDigestMode.Digest,
                PreferredLocale: "de"));

            var view = await harness.SendAsync(new GetMyNotificationPreferencesQuery());

            Assert.Equal(new[] { NotificationChannelKind.Email, NotificationChannelKind.Webhook }, view.AcceptedChannels);
            Assert.Equal(NotificationSeverityPreference.ErrorsOnly, view.SeverityFilter);
            Assert.Equal(new[] { "pipeline:9" }, view.MutedTopics);
            Assert.Equal(NotificationDigestMode.Digest, view.Digest);
            Assert.Equal("de", view.PreferredLocale);
            Assert.NotNull(view.QuietHours);
            Assert.Equal(new TimeOnly(22, 0), view.QuietHours!.Start);
            Assert.Equal(new TimeOnly(6, 0), view.QuietHours.End);
            Assert.Equal(TimeSpan.FromHours(1), view.QuietHours.UtcOffset);
        }

        [Fact]
        public async Task Save_Twice_UpdatesInPlace()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync(userId: 42);
            await using var _dispose = harness;

            Task Save(NotificationSeverityPreference filter) => harness.SendAsync(new SaveMyNotificationPreferencesCommand(
                Array.Empty<NotificationChannelKind>(), filter, Array.Empty<string>(), null, NotificationDigestMode.Immediate, null));

            await Save(NotificationSeverityPreference.All);
            await Save(NotificationSeverityPreference.None);

            var all = await harness.InScopeAsync(sp => sp.GetRequiredService<INotificationPreferenceRepository>().GetAllAsync());
            Assert.Single(all); // updated in place, not duplicated
            Assert.Equal(NotificationSeverityFilter.None, all[0].SeverityFilter);
        }

        [Fact]
        public async Task Get_NoPreference_ReturnsPermissiveDefaults()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync(userId: 42);
            await using var _dispose = harness;

            var view = await harness.SendAsync(new GetMyNotificationPreferencesQuery());

            Assert.Empty(view.AcceptedChannels);
            Assert.Equal(NotificationSeverityPreference.All, view.SeverityFilter);
            Assert.Null(view.QuietHours);
            Assert.Equal(NotificationDigestMode.Immediate, view.Digest);
        }

        // ---- added after the original cases ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Preferences_are_the_callers_own_and_a_second_user_does_not_see_them()
        {
            var (harness, _, user) = await NotificationCqrsHarness.StartAsync(userId: 42);
            await using var _dispose = harness;

            await harness.SendAsync(new SaveMyNotificationPreferencesCommand(Array.Empty<NotificationChannelKind>(), NotificationSeverityPreference.None,
                Array.Empty<string>(), null, NotificationDigestMode.Digest, "sv"));

            user.UserId = 43;
            var other = await harness.SendAsync(new GetMyNotificationPreferencesQuery());
            Assert.Null(other.PreferredLocale);
            Assert.Equal(NotificationSeverityPreference.All, other.SeverityFilter);

            user.UserId = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new GetMyNotificationPreferencesQuery()));
        }

        [Fact]
        public async Task My_notifications_are_only_the_requests_addressing_the_caller_on_this_site()
        {
            var (harness, _, user) = await NotificationCqrsHarness.StartAsync(userId: 7);
            await using var _dispose = harness;

            var mine = NotificationPipelineHarness.Request("generic.message", new[] { NotificationPipelineHarness.User(7) }, new[] { ChannelKind.InApp });
            var both = NotificationPipelineHarness.Request("generic.message", new[] { NotificationPipelineHarness.User(8), NotificationPipelineHarness.User(7) }, new[] { ChannelKind.InApp });
            var theirs = NotificationPipelineHarness.Request("generic.message", new[] { NotificationPipelineHarness.User(8) }, new[] { ChannelKind.InApp });
            var byEmail = NotificationPipelineHarness.Request("generic.message", new[] { new Recipient(RecipientKind.Email, "7@example.com") }, new[] { ChannelKind.Email });
            var otherSite = NotificationRequestAggregate.Create(Guid.NewGuid(), Stomme.Tenancy.Domain.TenantId.Default, NotificationPipelineHarness.SiteId + 1,
                "generic.message", new[] { NotificationPipelineHarness.User(7) }, new[] { ChannelKind.InApp }, createdOnUtc: NotificationPipelineHarness.Start);
            await harness.AddAsync(mine, both, theirs, byEmail, otherSite);

            var list = await harness.SendAsync(new ListMyNotificationsQuery());
            Assert.Equal(new[] { both.Id, mine.Id }.OrderBy(id => id), list.Select(item => item.Id).OrderBy(id => id));

            // The send log is the whole SITE (but never another site's).
            var log = await harness.SendAsync(new ListNotificationRequestsQuery());
            Assert.Equal(4, log.Count);
            Assert.DoesNotContain(log, item => item.Id == otherSite.Id);
            Assert.Null(await harness.SendAsync(new GetNotificationRequestQuery(otherSite.Id)));

            user.UserId = null;
            Assert.Empty(await harness.SendAsync(new ListMyNotificationsQuery()));
        }

        [Fact]
        public async Task Send_test_dispatches_generic_message_to_the_caller_and_retry_reopens_only_a_finished_unsent_request()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync(userId: 7);
            await using var _dispose = harness;
            harness.Directory.SiteUsers.Add((NotificationPipelineHarness.SiteId, 7));

            var id = await harness.SendAsync(new SendTestNotificationCommand(new[] { NotificationChannelKind.InApp }, "Hi", "<b>there</b>"));
            var queued = await harness.GetAsync<NotificationRequestAggregate>(id);
            Assert.NotNull(queued);
            Assert.Equal("generic.message", queued!.TemplateKey);
            Assert.Equal(NotificationPipelineHarness.User(7), Assert.Single(queued.Recipients));
            Assert.Contains(queued.Parameters, parameter => parameter.Name == "body" && parameter.Value == "<b>there</b>");

            // In flight: nothing to retry.
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new RetryNotificationRequestCommand(id)));

            var failed = NotificationPipelineHarness.Request("generic.message", new[] { NotificationPipelineHarness.User(7) }, new[] { ChannelKind.InApp });
            failed.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Failed, NotificationPipelineHarness.Start, "down");
            failed.ScheduleRetry(NotificationPipelineHarness.Start);
            failed.Complete(NotificationPipelineHarness.Start);
            await harness.AddAsync(failed);

            await harness.SendAsync(new RetryNotificationRequestCommand(failed.Id));
            var reopened = await harness.GetAsync<NotificationRequestAggregate>(failed.Id);
            Assert.Null(reopened!.CompletedOn);
            Assert.Equal(0, reopened.AttemptCount);
            Assert.Equal(harness.Clock.UtcNow, reopened.NextAttemptOn);
            Assert.Single(reopened.Attempts); // the history is kept

            var sent = NotificationPipelineHarness.Request("generic.message", new[] { NotificationPipelineHarness.User(7) }, new[] { ChannelKind.InApp });
            sent.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Succeeded, NotificationPipelineHarness.Start);
            sent.Complete(NotificationPipelineHarness.Start);
            await harness.AddAsync(sent);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new RetryNotificationRequestCommand(sent.Id)));
        }

        [Fact]
        public void The_wire_enums_mirror_the_domain_enums_by_name_and_value()
        {
            AssertMirror<NotificationStatus, NotificationRequestStatus>();
            AssertMirror<DeliveryStatus, NotificationDeliveryStatus>();
            AssertMirror<NotificationSeverityFilter, NotificationSeverityPreference>();
            AssertMirror<DigestMode, NotificationDigestMode>();
            AssertMirror<ChannelKind, NotificationChannelKind>();
            AssertMirror<RecipientKind, NotificationRecipientKind>();

            static void AssertMirror<TDomain, TWire>() where TDomain : struct, Enum where TWire : struct, Enum
            {
                Assert.Equal(
                    Enum.GetValues<TDomain>().Select(value => (value.ToString(), Convert.ToInt32(value))),
                    Enum.GetValues<TWire>().Select(value => (value.ToString(), Convert.ToInt32(value))));
            }
        }

        [Fact]
        public void Every_request_in_the_self_service_and_log_surface_names_its_split_key()
        {
            Assert.Equal(NotificationsPermissions.ViewOwn, Key<GetMyNotificationPreferencesQuery>());
            Assert.Equal(NotificationsPermissions.ViewOwn, Key<SaveMyNotificationPreferencesCommand>());
            Assert.Equal(NotificationsPermissions.ViewOwn, Key<ListMyNotificationsQuery>());
            Assert.Equal(NotificationsPermissions.ViewLog, Key<ListNotificationRequestsQuery>());
            Assert.Equal(NotificationsPermissions.ViewLog, Key<GetNotificationRequestQuery>());
            Assert.Equal(NotificationsPermissions.Send, Key<SendTestNotificationCommand>());
            Assert.Equal(NotificationsPermissions.Send, Key<RetryNotificationRequestCommand>());

            static string? Key<T>() where T : class =>
                Assert.Single((RequiresAttribute[])typeof(T).GetCustomAttributes(typeof(RequiresAttribute), inherit: true)).Permission;
        }
    }
}