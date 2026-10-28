#nullable enable
using System;
using Stomme.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The export job's life as a state machine, and the audit entry's invariants (BL-85, D108 items 2, 4 and 9).
    /// </summary>
    /// <remarks>
    /// What matters most here is the LEASE: a claim is a transition, a live render renews it, and only a lease
    /// that ran out with no heartbeat may be taken over - with a cap, so an export that kills its renderer every
    /// time fails instead of looping.
    /// </remarks>
    public class ExportJobStateMachineTests
    {
        private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        private static ExportJobAggregate Queued(string sourceKey = "stomme.log", ExportFormat format = ExportFormat.Csv) =>
            ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, sourceKey, format, null, "{}", Now);

        // ---- request ---------------------------------------------------------------------------------------

        [Fact]
        public void A_request_starts_Queued_with_nothing_else_set()
        {
            var job = Queued();

            Assert.Equal(ExportJobStatus.Queued, job.Status);
            Assert.Equal(0, job.Attempts);
            Assert.Equal(Now, job.RequestedOnUtc);
            Assert.Null(job.StartedOnUtc);
            Assert.Null(job.LeaseExpiresOnUtc);
            Assert.Null(job.FileId);
            Assert.Null(job.FailureCode);
            Assert.False(job.IsDownloadable(Now));
        }

        [Theory]
        [InlineData("Stomme.Log")]
        [InlineData(" stomme.log")]
        [InlineData("stomme.log.")]
        [InlineData("")]
        public void A_non_canonical_source_key_is_REJECTED_never_rewritten(string key)
        {
            // Rewriting would let two spellings name one source, so a permission granted for one answers for the other.
            Assert.Throws<ArgumentException>(() => Queued(key));
        }

        [Fact]
        public void A_request_refuses_a_non_UTC_clock_oversized_json_and_nonsense_ids()
        {
            Assert.Throws<ArgumentException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", ExportFormat.Csv, null, "{}", DateTime.Now));
            Assert.Throws<ArgumentException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", ExportFormat.Csv, null, "{}", new DateTime(2026, 1, 1)));
            Assert.Throws<ArgumentException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", ExportFormat.Csv, new string('x', ExportJobAggregate.MaximumArgumentsLength + 1), "{}", Now));
            Assert.Throws<ArgumentException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", ExportFormat.Csv, null, new string('x', ExportJobAggregate.MaximumOptionsLength + 1), Now));
            Assert.Throws<ArgumentException>(() => ExportJobAggregate.Request(Guid.Empty, TenantId.Default, 7, 42, "a", ExportFormat.Csv, null, "{}", Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 0, 42, "a", ExportFormat.Csv, null, "{}", Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 0, "a", ExportFormat.Csv, null, "{}", Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", (ExportFormat)99, null, "{}", Now));

            // Exactly at the limit is accepted - the bound is inclusive.
            Assert.NotNull(ExportJobAggregate.Request(Guid.NewGuid(), TenantId.Default, 7, 42, "a", ExportFormat.Csv, new string('x', ExportJobAggregate.MaximumArgumentsLength), "{}", Now));
        }

        // ---- the lease -------------------------------------------------------------------------------------

        [Fact]
        public void A_claim_moves_Queued_to_Rendering_and_takes_a_lease()
        {
            var job = Queued();
            job.Claim(Now, Lease);

            Assert.Equal(ExportJobStatus.Rendering, job.Status);
            Assert.Equal(1, job.Attempts);
            Assert.Equal(Now, job.StartedOnUtc);
            Assert.Equal(Now, job.HeartbeatOnUtc);
            Assert.Equal(Now + Lease, job.LeaseExpiresOnUtc);
        }

        [Fact]
        public void A_job_can_be_claimed_ONCE()
        {
            // In one process this is the whole guard; across hosts the store's concurrency check makes the second
            // writer's Update fail (ExportQueueLeaseTests). Either way the second claimant must not proceed.
            var job = Queued();
            job.Claim(Now, Lease);

            Assert.Throws<InvalidOperationException>(() => job.Claim(Now, Lease));
        }

        [Fact]
        public void A_heartbeat_renews_the_lease_so_a_LONG_healthy_render_is_never_reclaimable()
        {
            var job = Queued();
            job.Claim(Now, Lease);

            // A 35-minute render heartbeating every 5: past the original 10-minute lease three times over.
            for (var minutes = 5; minutes <= 35; minutes += 5)
            {
                var at = Now.AddMinutes(minutes);
                Assert.False(job.IsLeaseExpired(at), $"the lease had lapsed at +{minutes} min despite a heartbeat every 5");
                job.Heartbeat(at, Lease);
                Assert.Equal(at + Lease, job.LeaseExpiresOnUtc);
            }

            Assert.Equal(1, job.Attempts);
            Assert.Throws<InvalidOperationException>(() => job.Reclaim(Now.AddMinutes(36), Lease));
        }

        [Fact]
        public void A_lease_that_ran_out_with_no_heartbeat_IS_reclaimable_and_the_boundary_is_inclusive()
        {
            var job = Queued();
            job.Claim(Now, Lease);

            Assert.False(job.IsLeaseExpired(Now + Lease - TimeSpan.FromTicks(1)));
            Assert.True(job.IsLeaseExpired(Now + Lease));

            Assert.True(job.Reclaim(Now + Lease, Lease));
            Assert.Equal(ExportJobStatus.Rendering, job.Status);
            Assert.Equal(2, job.Attempts);
            Assert.Equal(Now + Lease + Lease, job.LeaseExpiresOnUtc);
        }

        [Fact]
        public void Reclaims_are_CAPPED_and_the_job_then_fails_with_a_code()
        {
            var job = Queued();
            var at = Now;
            job.Claim(at, Lease);

            for (var attempt = 2; attempt <= ExportJobAggregate.MaximumAttempts; attempt++)
            {
                at += Lease;
                Assert.True(job.Reclaim(at, Lease));
                Assert.Equal(attempt, job.Attempts);
            }

            at += Lease;
            Assert.False(job.Reclaim(at, Lease));

            Assert.Equal(ExportJobStatus.Failed, job.Status);
            Assert.Equal(ExportFailureCode.AttemptsExhausted, job.FailureCode);
            Assert.Equal(ExportJobAggregate.MaximumAttempts, job.Attempts);
            Assert.Null(job.LeaseExpiresOnUtc);
        }

        [Fact]
        public void Only_a_Rendering_job_has_a_lease_to_renew_and_a_lease_is_positive()
        {
            var job = Queued();
            Assert.Throws<InvalidOperationException>(() => job.Heartbeat(Now, Lease));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Claim(Now, TimeSpan.Zero));

            job.Claim(Now, Lease);
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Heartbeat(Now, TimeSpan.FromSeconds(-1)));
            Assert.Throws<ArgumentException>(() => job.Heartbeat(DateTime.Now, Lease));
        }

        // ---- completion, failure, cancellation ---------------------------------------------------------------

        [Fact]
        public void Completing_makes_the_artifact_downloadable_until_retention_runs_out()
        {
            var job = Queued(format: ExportFormat.Xlsx);
            job.Claim(Now, Lease);
            job.Complete(fileId: 99, rowCount: 10, sizeBytes: 2048, Now.AddMinutes(1), Retention);

            Assert.Equal(ExportJobStatus.Ready, job.Status);
            Assert.Equal(99, job.FileId);
            Assert.Equal(Now.AddMinutes(1) + Retention, job.ExpiresOnUtc);
            Assert.Null(job.LeaseExpiresOnUtc);

            Assert.True(job.IsDownloadable(Now.AddDays(6)));
            Assert.False(job.IsDue(Now.AddDays(6)));
            // At the instant of expiry it is already gone - the same inclusive boundary the purge query uses.
            Assert.False(job.IsDownloadable(job.ExpiresOnUtc!.Value));
            Assert.True(job.IsDue(job.ExpiresOnUtc!.Value));
        }

        [Fact]
        public void Expiring_drops_the_file_reference_and_keeps_the_row_as_history()
        {
            var job = Queued();
            job.Claim(Now, Lease);
            job.Complete(99, 10, 2048, Now, Retention);
            job.Expire(Now + Retention);

            Assert.Equal(ExportJobStatus.Expired, job.Status);
            Assert.Null(job.FileId);
            Assert.Equal(10, job.RowCount);
            Assert.False(job.IsDownloadable(Now));
        }

        [Fact]
        public void A_job_completes_only_from_Rendering_and_only_with_sane_numbers()
        {
            var job = Queued();
            Assert.Throws<InvalidOperationException>(() => job.Complete(99, 1, 1, Now, Retention));

            job.Claim(Now, Lease);
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Complete(0, 1, 1, Now, Retention));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Complete(99, -1, 1, Now, Retention));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Complete(99, 1, -1, Now, Retention));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Complete(99, 1, 1, Now, TimeSpan.Zero));

            // An EMPTY export is a legitimate result, not an error.
            job.Complete(99, 0, 0, Now, Retention);
            Assert.Equal(ExportJobStatus.Ready, job.Status);
        }

        [Fact]
        public void A_failure_carries_a_CODE_and_the_aggregate_has_nowhere_to_put_exception_text()
        {
            var job = Queued();
            job.Claim(Now, Lease);
            job.Fail(ExportFailureCode.RenderError, Now.AddSeconds(1));

            Assert.Equal(ExportJobStatus.Failed, job.Status);
            Assert.Equal(ExportFailureCode.RenderError, job.FailureCode);
            Assert.Null(job.LeaseExpiresOnUtc);

            // the predecessor platform stored ex.Message and showed it to the requester. The defence here is structural: there is
            // no string member a message could be written to.
            foreach (var property in typeof(ExportJobAggregate).GetProperties())
            {
                Assert.False(
                    property.PropertyType == typeof(string)
                        && (property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Contains("Message", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase)),
                    $"{property.Name} is a free-text failure member; a failure is a code, the text goes to the log");
            }
        }

        [Fact]
        public void A_queued_job_can_fail_too_and_an_undefined_code_is_refused()
        {
            var job = Queued();
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Fail((ExportFailureCode)999, Now));

            // Re-authorisation can refuse a job before it is ever claimed.
            job.Fail(ExportFailureCode.NotAuthorized, Now);
            Assert.Equal(ExportJobStatus.Failed, job.Status);
        }

        [Fact]
        public void Cancel_is_allowed_while_work_is_outstanding_and_not_after()
        {
            var queued = Queued();
            queued.Cancel(Now);
            Assert.Equal(ExportJobStatus.Cancelled, queued.Status);

            var rendering = Queued();
            rendering.Claim(Now, Lease);
            rendering.Cancel(Now.AddSeconds(1));
            Assert.Equal(ExportJobStatus.Cancelled, rendering.Status);
            Assert.Null(rendering.LeaseExpiresOnUtc);
            // A cancelled render cannot then report success over the top of the cancellation.
            Assert.Throws<InvalidOperationException>(() => rendering.Complete(99, 1, 1, Now, Retention));
            Assert.Throws<InvalidOperationException>(() => rendering.Heartbeat(Now, Lease));

            var ready = Queued();
            ready.Claim(Now, Lease);
            ready.Complete(99, 1, 1, Now, Retention);
            Assert.Throws<InvalidOperationException>(() => ready.Cancel(Now));
        }

        [Theory]
        [InlineData(ExportJobStatus.Failed)]
        [InlineData(ExportJobStatus.Cancelled)]
        [InlineData(ExportJobStatus.Expired)]
        [InlineData(ExportJobStatus.Ready)]
        public void A_terminal_or_ready_job_cannot_be_claimed_failed_or_reclaimed(ExportJobStatus status)
        {
            var job = Queued();
            switch (status)
            {
                case ExportJobStatus.Failed:
                    job.Fail(ExportFailureCode.Unknown, Now);
                    break;
                case ExportJobStatus.Cancelled:
                    job.Cancel(Now);
                    break;
                default:
                    job.Claim(Now, Lease);
                    job.Complete(99, 1, 1, Now, Retention);
                    if (status == ExportJobStatus.Expired)
                    {
                        job.Expire(Now + Retention);
                    }

                    break;
            }

            Assert.Equal(status, job.Status);
            Assert.Throws<InvalidOperationException>(() => job.Claim(Now, Lease));
            Assert.Throws<InvalidOperationException>(() => job.Fail(ExportFailureCode.Unknown, Now));
            Assert.Throws<InvalidOperationException>(() => job.Reclaim(Now.AddYears(1), Lease));
            Assert.False(job.IsLeaseExpired(Now.AddYears(1)));
        }

        // ---- format facts ----------------------------------------------------------------------------------

        [Theory]
        [InlineData(ExportFormat.Csv, "text/csv", ".csv")]
        [InlineData(ExportFormat.Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
        [InlineData(ExportFormat.Json, "application/json", ".json")]
        [InlineData(ExportFormat.Pdf, "application/pdf", ".pdf")]
        public void Each_format_has_its_content_type_and_extension(ExportFormat format, string contentType, string extension)
        {
            var job = Queued(format: format);
            Assert.Equal(contentType, job.ContentType());
            Assert.Equal(extension, job.FileExtension());
        }

        // ---- the renderer registry -------------------------------------------------------------------------

        [Fact]
        public void The_registry_resolves_by_format_and_REFUSES_two_renderers_for_one()
        {
            var registry = new ExportRendererRegistry(new ITabularRenderer[] { new CsvTabularRenderer(), new JsonTabularRenderer(), new XlsxTabularRenderer() });

            Assert.IsType<XlsxTabularRenderer>(registry.Get(ExportFormat.Xlsx));
            Assert.True(registry.TryGet(ExportFormat.Csv, out var csv));
            Assert.IsType<CsvTabularRenderer>(csv);
            Assert.False(registry.TryGet(ExportFormat.Pdf, out _));
            Assert.Throws<ExportRenderException>(() => registry.Get(ExportFormat.Pdf));

            // Last-registration-wins would let a packaged module silently replace the renderer that neutralises
            // formulas with one that does not.
            var error = Assert.Throws<InvalidOperationException>(() =>
                new ExportRendererRegistry(new ITabularRenderer[] { new CsvTabularRenderer(), new CsvTabularRenderer() }));
            Assert.Contains("Csv", error.Message);
        }

        // ---- the audit entry -------------------------------------------------------------------------------

        [Fact]
        public void An_audit_entry_pairs_a_failure_code_with_Failed_and_only_with_Failed()
        {
            var jobId = Guid.NewGuid();

            var failed = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, null, ExportAuditAction.Failed, Now, ExportFailureCode.TimedOut);
            Assert.Equal(ExportFailureCode.TimedOut, failed.FailureCode);
            Assert.Null(failed.ActorUserId);

            Assert.Throws<ArgumentException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, null, ExportAuditAction.Failed, Now));
            Assert.Throws<ArgumentException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, ExportAuditAction.Downloaded, Now, ExportFailureCode.Unknown));
        }

        [Fact]
        public void An_audit_entry_refuses_nonsense_and_has_no_mutator()
        {
            var jobId = Guid.NewGuid();
            Assert.Throws<ArgumentException>(() => ExportAuditEntryAggregate.Record(Guid.Empty, TenantId.Default, 7, jobId, 42, ExportAuditAction.Requested, Now));
            Assert.Throws<ArgumentException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, Guid.Empty, 42, ExportAuditAction.Requested, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 0, jobId, 42, ExportAuditAction.Requested, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 0, ExportAuditAction.Requested, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, (ExportAuditAction)99, Now));
            Assert.Throws<ArgumentException>(() => ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, ExportAuditAction.Requested, DateTime.Now));

            // An audit trail that can be edited is not one: no public instance method changes an entry.
            var mutators = typeof(ExportAuditEntryAggregate)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(mutators, m => !m.IsSpecialName);
            Assert.DoesNotContain(typeof(ExportAuditEntryAggregate).GetProperties(), p => p.SetMethod is { IsPublic: true });
        }
    }
}
