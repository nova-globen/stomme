#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Modules.Erasure.Application;
using Stomme.Modules.Erasure.Domain;
using Stomme.Erasure.Persistence;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Erasure
{
    /// <summary>
    /// BL-116 / D112, the runner's contract held against fakes and a controlled clock: every participant's outcome is
    /// on the receipt with the declared handling; a retention keeps the subject's ids until it falls due, then the same
    /// participant purges and the receipt drops the ids; a participant that throws fails the run CLOSED - nothing is
    /// recorded as erased, the request stays pending with the participant and the exception type, the next run
    /// re-applies everything (idempotent), and after the attempt limit it is Failed and can be retried.
    /// </summary>
    public sealed class ErasureRunnerTests
    {
        private static readonly TenantId Tenant = TenantId.From(Guid.NewGuid());

        [Fact]
        public async Task Outcomes_land_on_the_receipt_and_a_retention_holds_the_ids_until_it_is_purged()
        {
            var clock = new FakeClock(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc));
            var store = new FakeRequests();
            var settings = new FakeParticipant("Settings", ErasureDeclaration.Delete("SettingAggregate")) { Rows = 3 };
            var audit = new FakeParticipant("Identity audit", ErasureDeclaration.Retain("IdentityAuditEntryAggregate", RetentionReason.Security, TimeSpan.FromDays(30))) { Rows = 2 };
            var runner = new ErasureRunner(new ISubjectErasureParticipant[] { settings, audit }, store, new FakeUnitOfWork(), clock, NullLogger<ErasureRunner>.Instance);
            var request = ErasureRequestAggregate.Create(Tenant, new ErasureSubject(1, 42, "idp-42"), clock.UtcNow);
            store.Items.Add(request);

            Assert.Equal(ErasureRequestStatus.Retaining, await runner.RunAsync(request));
            Assert.Equal(42, request.UserId);
            Assert.Equal("idp-42", request.IdentitySubject);
            Assert.Equal(clock.UtcNow.AddDays(30), request.RetainedUntilUtc);
            var outcomes = request.ReadOutcomes();
            Assert.Equal(("Settings", "SettingAggregate", ErasureHandling.Delete, 3), (outcomes[0].Participant, outcomes[0].Store, outcomes[0].Handling, outcomes[0].Affected));
            Assert.Equal((ErasureHandling.Retain, RetentionReason.Security, clock.UtcNow.AddDays(30)), (outcomes[1].Handling, outcomes[1].Reason, outcomes[1].RetainedUntilUtc));
            Assert.Equal(1, settings.Erased);

            // Not due: a run purges nothing and changes nothing.
            clock.UtcNow = clock.UtcNow.AddDays(10);
            Assert.Equal(ErasureRequestStatus.Retaining, await runner.RunAsync(request));
            Assert.Equal(0, audit.Purged);
            Assert.DoesNotContain(request, await store.ListOpenAsync(1, clock.UtcNow));

            // Due: the same participant purges, and the receipt is final with no identity on it.
            clock.UtcNow = clock.UtcNow.AddDays(21);
            Assert.Contains(request, await store.ListOpenAsync(1, clock.UtcNow));
            Assert.Equal(ErasureRequestStatus.Done, await runner.RunAsync(request));
            Assert.Equal(1, audit.Purged);
            Assert.Null(request.UserId);
            Assert.Null(request.IdentitySubject);
            Assert.Null(request.CurrentSubject());
            Assert.Equal(clock.UtcNow, request.CompletedOnUtc);
            Assert.Equal(2, request.ReadOutcomes()[1].Affected);
            Assert.Equal(clock.UtcNow, request.ReadOutcomes()[1].PurgedOnUtc);
            Assert.Equal(32, request.SubjectErasureId.Length);
            // Done: a further run is a no-op.
            Assert.Equal(ErasureRequestStatus.Done, await runner.RunAsync(request));
            Assert.Equal(1, settings.Erased);
        }

        [Fact]
        public async Task A_throwing_participant_fails_the_run_closed_and_a_retry_re_applies_every_participant()
        {
            var clock = new FakeClock(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc));
            var store = new FakeRequests();
            var first = new FakeParticipant("First", ErasureDeclaration.Delete("A")) { Rows = 1 };
            var broken = new FakeParticipant("Broken", ErasureDeclaration.Delete("B")) { Throws = true };
            var runner = new ErasureRunner(new ISubjectErasureParticipant[] { first, broken }, store, new FakeUnitOfWork(), clock, NullLogger<ErasureRunner>.Instance);
            var request = ErasureRequestAggregate.Create(Tenant, new ErasureSubject(1, 7, null), clock.UtcNow);

            Assert.Equal(ErasureRequestStatus.Pending, await runner.RunAsync(request));
            Assert.Empty(request.ReadOutcomes());
            Assert.Null(request.ErasedOnUtc);
            Assert.Equal(("Broken: InvalidOperationException", 1), (request.LastError, request.Attempts));
            Assert.Equal(7, request.UserId);

            broken.Throws = false;
            Assert.Equal(ErasureRequestStatus.Done, await runner.RunAsync(request));
            Assert.Equal(2, first.Erased);
            Assert.Equal(2, request.ReadOutcomes().Count);
            Assert.Equal("", request.LastError);
            Assert.Null(request.UserId);
        }

        [Fact]
        public async Task After_the_attempt_limit_the_request_is_failed_and_the_host_can_retry_it()
        {
            var clock = new FakeClock(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc));
            var store = new FakeRequests();
            var broken = new FakeParticipant("Broken", ErasureDeclaration.Delete("B")) { Throws = true };
            var runner = new ErasureRunner(new[] { broken }, store, new FakeUnitOfWork(), clock, NullLogger<ErasureRunner>.Instance);
            var request = ErasureRequestAggregate.Create(Tenant, new ErasureSubject(1, 7, null), clock.UtcNow);
            for (var i = 0; i < ErasureRequestAggregate.MaximumAttempts; i++)
            {
                await runner.RunAsync(request);
            }

            Assert.Equal(ErasureRequestStatus.Failed, request.Status);
            Assert.NotNull(request.CompletedOnUtc);
            Assert.Equal(7, request.UserId); // the ids stay: nothing was erased, and the Host can still retry
            request.Retry();
            Assert.Equal((ErasureRequestStatus.Pending, 0), (request.Status, request.Attempts));
            broken.Throws = false;
            Assert.Equal(ErasureRequestStatus.Done, await runner.RunAsync(request));
        }

        [Fact]
        public void A_retention_needs_a_period()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ErasureDeclaration.Retain("X", RetentionReason.LegalClaims, TimeSpan.Zero));
            Assert.Throws<ArgumentException>(() => ErasureRequestAggregate.Create(Tenant, new ErasureSubject(0, 1, null), DateTime.UtcNow));
        }

        private sealed class FakeClock(DateTime now) : TimeProvider
        {
            public DateTime UtcNow { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
        }

        private sealed class FakeUnitOfWork : IUnitOfWork
        {
            public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);

            public void Dispose()
            {
            }
        }

        private sealed class FakeParticipant(string name, params ErasureDeclaration[] declarations) : ISubjectErasureParticipant
        {
            public int Rows { get; set; }

            public bool Throws { get; set; }

            public int Erased { get; private set; }

            public int Purged { get; private set; }

            public string Name => name;

            public IReadOnlyList<ErasureDeclaration> Declarations => declarations;

            public Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
            {
                if (Throws)
                {
                    throw new InvalidOperationException("the store is unreachable: " + subject.UserId);
                }

                Erased++;
                return Task.FromResult<IReadOnlyList<ErasureOutcome>>(declarations.Select(d => new ErasureOutcome(d.Store, d.Handling, Rows, d.Reason)).ToList());
            }

            public Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
            {
                Purged++;
                return Task.FromResult(Rows);
            }
        }

        private sealed class FakeRequests : IErasureRequestRepository
        {
            public List<ErasureRequestAggregate> Items { get; } = new();

            public Task<ErasureRequestAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

            public Task<IReadOnlyList<ErasureRequestAggregate>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ErasureRequestAggregate>>(Items.ToList());

            public Task<IReadOnlyList<ErasureRequestAggregate>> FindAsync(Expression<Func<ErasureRequestAggregate, bool>> predicate, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<ErasureRequestAggregate>>(Items.Where(predicate.Compile()).ToList());

            public Task AddAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default)
            {
                Items.Add(aggregate);
                return Task.CompletedTask;
            }

            public Task UpdateAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;

            public Task DeleteAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default)
            {
                Items.Remove(aggregate);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<ErasureRequestAggregate>> ListOpenAsync(int siteId, DateTime nowUtc, CancellationToken ct = default) =>
                new ErasureRequestRepository(this).ListOpenAsync(siteId, nowUtc, ct);

            public Task<IReadOnlyList<ErasureRequestAggregate>> ListForSiteAsync(int siteId, int take, CancellationToken ct = default) =>
                new ErasureRequestRepository(this).ListForSiteAsync(siteId, take, ct);
        }
    }
}
