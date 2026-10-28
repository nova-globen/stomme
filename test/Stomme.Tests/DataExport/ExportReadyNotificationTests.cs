#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Models.Notifications;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Notifications;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// BL-85 (D108 item 8) - the "your export is ready" notice over BL-78's producer port.
    /// </summary>
    /// <remarks>
    /// What is pinned is WHO is told and WHAT they are handed: the requester alone, by user id, with a route that
    /// authorises whoever follows it - never the site's administrators, never an address, never the artifact. And that
    /// the notice is best-effort: a dispatcher that fails must not fail the export.
    /// </remarks>
    public class ExportReadyNotificationTests
    {
        private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        private static ExportJobAggregate ReadyJob(int requester = 42)
        {
            var job = ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, requester, "stomme.log", ExportFormat.Xlsx, null, "{}", Now);
            job.Claim(Now, TimeSpan.FromMinutes(10));
            job.Complete(99, 1234, 5678, Now.AddSeconds(5), TimeSpan.FromDays(7));
            return job;
        }

        [Fact]
        public async Task The_REQUESTER_is_told_by_user_id_and_handed_the_modules_own_route()
        {
            var dispatcher = new CapturingDispatcher();
            var job = ReadyJob(requester: 42);

            await new ExportReadyNotifier(NullLogger<ExportReadyNotifier>.Instance, dispatcher).NotifyAsync(job);

            var request = Assert.Single(dispatcher.Requests);
            Assert.Equal(ExportTemplateCatalogue.ReadyKey, request.TemplateKey);

            // One recipient, a USER, the requester. Not an address, not an endpoint, not the administrators.
            var recipient = Assert.Single(request.Recipients);
            Assert.Equal(NotificationRecipientKind.User, recipient.Kind);
            Assert.Equal("42", recipient.Value);
            Assert.Equal(new[] { NotificationChannelKind.InApp }, request.Channels);

            var parameters = request.Parameters!.ToDictionary(p => p.Name, p => p.Value);
            Assert.Equal("stomme.log", parameters["source"]);
            Assert.Equal("Xlsx", parameters["format"]);
            Assert.Equal("1234", parameters["rows"]);
            // The MODULE's download route - the one that checks requester-or-manage and that the file is still in the
            // Exports folder. Never a framework file URL, which authorises by folder View alone.
            Assert.Equal($"/api/DataExport/{job.Id:D}/download", parameters["download"]);
            Assert.DoesNotContain(parameters.Values, value => value.Contains("/api/File/", StringComparison.OrdinalIgnoreCase) || value.Contains("/files/", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Nothing_is_sent_for_a_job_that_is_not_Ready()
        {
            var dispatcher = new CapturingDispatcher();
            var notifier = new ExportReadyNotifier(NullLogger<ExportReadyNotifier>.Instance, dispatcher);

            var queued = ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "stomme.log", ExportFormat.Csv, null, "{}", Now);
            await notifier.NotifyAsync(queued);

            queued.Claim(Now, TimeSpan.FromMinutes(10));
            await notifier.NotifyAsync(queued);

            queued.Fail(ExportFailureCode.RenderError, Now);
            await notifier.NotifyAsync(queued);

            Assert.Empty(dispatcher.Requests);
        }

        [Fact]
        public async Task A_dispatcher_that_FAILS_does_not_fail_the_export_and_an_absent_one_is_a_no_op()
        {
            // The export is ready whether or not anyone could be told: an advisory must not become an outage.
            await new ExportReadyNotifier(NullLogger<ExportReadyNotifier>.Instance, new ThrowingDispatcher()).NotifyAsync(ReadyJob());
            await new ExportReadyNotifier(NullLogger<ExportReadyNotifier>.Instance, dispatcher: null).NotifyAsync(ReadyJob());
        }

        [Fact]
        public void The_template_declares_every_placeholder_the_notice_sends()
        {
            var templates = ExportTemplateCatalogue.All(TenantId.Default, 7);
            var template = Assert.Single(templates, t => t.Key == ExportTemplateCatalogue.ReadyKey);

            // A parameter the template does not declare is one the renderer refuses; the two lists must agree.
            Assert.Equal(
                new[] { "download", "format", "rows", "source" },
                template.Placeholders.Placeholders.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(7, template.SiteId);

            // BL-95: the schedule-paused notice, the same agreement.
            var paused = Assert.Single(templates, t => t.Key == ExportTemplateCatalogue.SchedulePausedKey);
            Assert.Equal(new[] { "reason", "schedule" }, paused.Placeholders.Placeholders.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(2, templates.Count);
            Assert.Equal(2, new ExportTemplateSource().CreateTemplates(TenantId.Default, 7).Count);
        }

        [Fact]
        public async Task A_paused_schedule_tells_its_owner_and_the_operators_in_app_and_nothing_about_the_data()
        {
            var dispatcher = new CapturingDispatcher();
            var schedule = Stomme.Modules.DataExport.Domain.ExportScheduleAggregate.Create(
                Guid.NewGuid(), TenantId.Default, 7, 42, "Nightly log", "stomme.log", ExportFormat.Xlsx, null, "{}", "0 6 * * *", DateTime.UtcNow);
            schedule.Pause(Stomme.Modules.DataExport.Domain.ExportSchedulePauseReason.OwnerNotAllowed, DateTime.UtcNow);

            await new ExportReadyNotifier(NullLogger<ExportReadyNotifier>.Instance, dispatcher).NotifySchedulePausedAsync(schedule, new[] { 1, 42 });

            var request = Assert.Single(dispatcher.Requests);
            Assert.Equal(ExportTemplateCatalogue.SchedulePausedKey, request.TemplateKey);
            Assert.Equal(new[] { "1", "42" }, request.Recipients.Select(r => r.Value).OrderBy(v => v, StringComparer.Ordinal));
            Assert.Equal(new[] { NotificationChannelKind.InApp }, request.Channels);
            var parameters = request.Parameters!.ToDictionary(p => p.Name, p => p.Value);
            Assert.Equal("Nightly log", parameters["schedule"]);
            Assert.Contains("owner may no longer", parameters["reason"], StringComparison.Ordinal);
        }

        private sealed class CapturingDispatcher : INotificationDispatcher
        {
            public List<NotificationDispatchRequest> Requests { get; } = new();

            public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                return Task.FromResult(Guid.NewGuid());
            }
        }

        private sealed class ThrowingDispatcher : INotificationDispatcher
        {
            public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default) =>
                throw new InvalidOperationException("the notification store is down");
        }
    }
}
