#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Stomme.Erasure;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Erasure.Domain
{
    public enum ErasureRequestStatus
    {
        Pending = 0,
        Retaining = 1,
        Done = 2,
        Failed = 3,
    }

    /// <summary>One participant's outcome for one store, as the receipt keeps it.</summary>
    public sealed record ErasureOutcomeRecord(string Participant, string Store, ErasureHandling Handling, int Affected, RetentionReason? Reason, DateTime? RetainedUntilUtc, DateTime? PurgedOnUtc);

    /// <summary>
    /// A subject's erasure: the request while it runs, the RECEIPT once it is done (D112). While pending or retaining it
    /// carries the ids the participants act on (Stomme's user id, the identity provider's subject); when every store
    /// is erased and every retention purged, those are cleared and what remains holds no personal data: the opaque
    /// erasure id (random, unlinkable), the site, the timestamps, each participant's outcome with its counts, and for a
    /// retention the reason and the date the rows fell due. Idempotent by construction: a run re-applies every
    /// participant, and a participant finds nothing left to do.
    /// </summary>
    public sealed class ErasureRequestAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumErrorLength = 256;
        public const int MaximumAttempts = 10;

        private ErasureRequestAggregate()
        {
            TenantId = null!;
            SubjectErasureId = string.Empty;
            OutcomesJson = "[]";
            LastError = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>Stomme's user id while the run is pending or retaining; null once the receipt is final.</summary>
        public int? UserId { get; private set; }

        /// <summary>The identity provider's subject id while pending; null once the receipt is final.</summary>
        public string? IdentitySubject { get; private set; }

        /// <summary>The username, while the run is open: the authorship stamps name the person by it. Null once the receipt is final.</summary>
        public string? Username { get; private set; }

        public string SubjectErasureId { get; private set; }

        public ErasureRequestStatus Status { get; private set; }

        public DateTime RequestedOnUtc { get; private set; }

        public DateTime? ErasedOnUtc { get; private set; }

        public DateTime? CompletedOnUtc { get; private set; }

        public DateTime? RetainedUntilUtc { get; private set; }

        public int Attempts { get; private set; }

        public string LastError { get; private set; }

        public string OutcomesJson { get; private set; }

        public static ErasureRequestAggregate Create(TenantId tenantId, ErasureSubject subject, DateTime nowUtc)
        {
            if (subject.SiteId <= 0 || subject.UserId <= 0)
            {
                throw new ArgumentException("An erasure names a site and a user.", nameof(subject));
            }

            return new ErasureRequestAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId)),
                SiteId = subject.SiteId,
                UserId = subject.UserId,
                IdentitySubject = subject.IdentitySubject,
                Username = subject.Username,
                SubjectErasureId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Status = ErasureRequestStatus.Pending,
                RequestedOnUtc = nowUtc,
            };
        }

        /// <summary>The ids the participants act on, while the run is open; null once the receipt is final. A method: the shared module store maps every public property.</summary>
        public ErasureSubject? CurrentSubject() => UserId is { } userId ? new ErasureSubject(SiteId, userId, IdentitySubject, Username) : null;

        /// <summary>The outcomes as recorded (a method, not a property: the shared module store would try to map a collection property).</summary>
        public IReadOnlyList<ErasureOutcomeRecord> ReadOutcomes() =>
            JsonSerializer.Deserialize<List<ErasureOutcomeRecord>>(OutcomesJson) ?? new List<ErasureOutcomeRecord>();

        /// <summary>Every participant ran: the outcomes are the receipt's; retentions keep the ids until they fall due.</summary>
        public void RecordErased(IReadOnlyList<ErasureOutcomeRecord> outcomes, DateTime nowUtc)
        {
            OutcomesJson = JsonSerializer.Serialize(outcomes);
            ErasedOnUtc = nowUtc;
            LastError = string.Empty;
            Attempts++;
            var due = outcomes.Where(o => o.Handling == ErasureHandling.Retain && o.PurgedOnUtc is null).Select(o => o.RetainedUntilUtc).Where(d => d is not null).ToList();
            if (due.Count > 0)
            {
                Status = ErasureRequestStatus.Retaining;
                RetainedUntilUtc = due.Max();
                return;
            }

            Complete(nowUtc);
        }

        /// <summary>A retention was purged: the outcome is stamped; when none is left, the receipt is final.</summary>
        public void RecordPurged(string participant, string store, int affected, DateTime nowUtc)
        {
            var outcomes = ReadOutcomes().Select(o => o.Participant == participant && o.Store == store && o.Handling == ErasureHandling.Retain
                ? o with { Affected = affected, PurgedOnUtc = nowUtc }
                : o).ToList();
            OutcomesJson = JsonSerializer.Serialize(outcomes);
            var pending = outcomes.Where(o => o.Handling == ErasureHandling.Retain && o.PurgedOnUtc is null).ToList();
            if (pending.Count == 0)
            {
                Complete(nowUtc);
            }
            else
            {
                RetainedUntilUtc = pending.Max(o => o.RetainedUntilUtc);
            }
        }

        /// <summary>A participant threw: the run stays pending for a retry, visible to the Host; after too many, Failed.</summary>
        public void RecordFailure(string participant, string exceptionType, DateTime nowUtc)
        {
            Attempts++;
            LastError = Truncate($"{participant}: {exceptionType}", MaximumErrorLength);
            if (Attempts >= MaximumAttempts)
            {
                Status = ErasureRequestStatus.Failed;
                CompletedOnUtc = nowUtc;
            }
        }

        /// <summary>The Host asks for a failed run to be tried again.</summary>
        public void Retry()
        {
            if (Status == ErasureRequestStatus.Failed)
            {
                Status = ErasureRequestStatus.Pending;
                CompletedOnUtc = null;
                Attempts = 0;
            }
        }

        private void Complete(DateTime nowUtc)
        {
            Status = ErasureRequestStatus.Done;
            CompletedOnUtc = nowUtc;
            RetainedUntilUtc = null;
            // The identity behind the receipt is gone from it: nothing here links to a person any more.
            UserId = null;
            IdentitySubject = null;
            Username = null;
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    }
}
