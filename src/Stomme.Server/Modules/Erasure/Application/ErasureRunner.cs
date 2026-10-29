#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Modules.Erasure.Domain;
using Stomme.Erasure.Persistence;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Erasure.Application
{
    /// <summary>
    /// Queues an erasure: the request row that the job picks up on its next tick, with the ids the participants
    /// need. Deleting a user calls this - after the identity subject is captured and before the account row goes.
    /// </summary>
    public sealed class ErasureQueue(IErasureRequestRepository requests, IUnitOfWork unitOfWork, ITenantContext tenantContext, TimeProvider clock) : IErasureQueue
    {
        public async Task<Guid> EnqueueAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("An erasure is queued inside a tenant.");
            var request = ErasureRequestAggregate.Create(tenantId, subject, clock.GetUtcNow().UtcDateTime);
            await requests.AddAsync(request, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return request.Id;
        }
    }

    /// <summary>
    /// Runs one erasure request against every participant. Fail closed: a participant that throws stops the run
    /// for that subject, the request stays pending with the participant and the exception TYPE on it (never a message,
    /// which could carry data), and the next tick tries again; after the attempt limit it is Failed and visible to the
    /// Host. Idempotent: every participant is re-applied on a retry and finds nothing left for what it already erased.
    /// A retaining request is revisited when a retention falls due, and its retained rows are purged by the same
    /// participants; when the last one is purged the receipt drops the subject's ids.
    /// </summary>
    public sealed class ErasureRunner(IEnumerable<ISubjectErasureParticipant> participants, IErasureRequestRepository requests, IUnitOfWork unitOfWork, TimeProvider clock, ILogger<ErasureRunner> logger)
    {
        public IReadOnlyList<ISubjectErasureParticipant> Participants => participants.ToList();

        public async Task<ErasureRequestStatus> RunAsync(ErasureRequestAggregate request, CancellationToken ct = default)
        {
            var subject = request.CurrentSubject();
            if (subject is null || request.Status is ErasureRequestStatus.Done or ErasureRequestStatus.Failed)
            {
                return request.Status;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (request.Status == ErasureRequestStatus.Pending)
            {
                var outcomes = new List<ErasureOutcomeRecord>();
                foreach (var participant in participants)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var declared = participant.Declarations.ToDictionary(d => d.Store, StringComparer.Ordinal);
                        foreach (var outcome in await participant.EraseAsync(subject, ct))
                        {
                            declared.TryGetValue(outcome.Store, out var declaration);
                            var handling = declaration?.Handling ?? outcome.Handling;
                            // A retention falls due from the RUN's clock and the DECLARED period, so every retained store on one
                            // receipt shares the date the receipt reports; a participant's own estimate is only a fallback.
                            var due = handling == ErasureHandling.Retain
                                ? (declaration?.Period is { } period ? now.Add(period) : outcome.RetainedUntilUtc ?? now)
                                : (DateTime?)null;
                            outcomes.Add(new ErasureOutcomeRecord(participant.Name, outcome.Store, handling, outcome.Affected, declaration?.Reason ?? outcome.Reason, due, null));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Erasure {Request} on site {Site}: participant {Participant} failed; the run stays pending.", request.Id, request.SiteId, participant.Name);
                        request.RecordFailure(participant.Name, ex.GetType().Name, now);
                        return await PersistAsync(request, ct);
                    }
                }

                request.RecordErased(outcomes, now);
                return await PersistAsync(request, ct);
            }

            // Retaining: purge what fell due.
            foreach (var outcome in request.ReadOutcomes().Where(o => o.Handling == ErasureHandling.Retain && o.PurgedOnUtc is null && o.RetainedUntilUtc is { } due && due <= now).ToList())
            {
                var participant = participants.FirstOrDefault(p => p.Name == outcome.Participant);
                if (participant is null)
                {
                    logger.LogWarning("Erasure {Request}: the participant {Participant} that retained {Store} is no longer composed; its rows stay until it returns.", request.Id, outcome.Participant, outcome.Store);
                    continue;
                }

                try
                {
                    var purged = await participant.PurgeRetainedAsync(subject, ct);
                    request.RecordPurged(participant.Name, outcome.Store, purged, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Erasure {Request}: purging {Store} retained by {Participant} failed.", request.Id, outcome.Store, participant.Name);
                    request.RecordFailure(participant.Name, ex.GetType().Name, now);
                    break;
                }
            }

            return await PersistAsync(request, ct);
        }

        // The ONE write of a run (the per-write-store invariant): every path ends here.
        private async Task<ErasureRequestStatus> PersistAsync(ErasureRequestAggregate request, CancellationToken ct)
        {
            await requests.UpdateAsync(request, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return request.Status;
        }
    }
}
