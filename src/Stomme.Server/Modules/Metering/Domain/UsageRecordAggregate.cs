#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// One appended row of the usage ledger: what was consumed, by whom, under which operation (D102).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ledger is append-only and is the FACT; the quota counter is a fold of it, and the repair pass exists
    /// because the two are separate commits. The id is derived from (site, metric, operation), so a producer that
    /// retries the same operation appends once - the only idempotency available when there is no transaction
    /// spanning the append and the fold.
    /// </para>
    /// <para>
    /// <see cref="UserId"/> is nullable and is NULLED on user deletion rather than removed with the row: the count
    /// stays factual while ceasing to be personal. <see cref="Source"/> is a bounded canonical key, never free
    /// text, so the ledger cannot quietly become a personal-data store.
    /// </para>
    /// </remarks>
    public sealed class UsageRecordAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private UsageRecordAggregate()
        {
            TenantId = null!;
            Metric = string.Empty;
        }

        private UsageRecordAggregate(Guid id, TenantId tenantId, int siteId, string metric, long quantity, int? userId, string? source, Guid operationId, DateTime occurredOnUtc)
        {
            Id = id;
            TenantId = tenantId;
            SiteId = siteId;
            Metric = metric;
            Quantity = quantity;
            UserId = userId;
            Source = source;
            OperationId = operationId;
            OccurredOnUtc = occurredOnUtc;
            FoldState = FoldState.Pending;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The canonical metric key - already lowercase, so it filters directly.</summary>
        public string Metric { get; private set; }

        public long Quantity { get; private set; }

        /// <summary>The user this consumption is attributed to; NULLED on erasure, never caller-supplied.</summary>
        public int? UserId { get; private set; }

        /// <summary>A bounded canonical key naming the producer.</summary>
        public string? Source { get; private set; }

        /// <summary>The operation this row belongs to; the id is derived from it.</summary>
        public Guid OperationId { get; private set; }

        /// <summary>Server clock, UTC - never supplied by the caller, so it cannot be backdated.</summary>
        public DateTime OccurredOnUtc { get; private set; }

        /// <summary>Whether this row has been folded into its quota counter.</summary>
        public FoldState FoldState { get; private set; }

        public static UsageRecordAggregate Append(Guid id, TenantId tenantId, int siteId, string metric, long quantity, int? userId, string? source, Guid operationId, DateTime occurredOnUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(metric);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId));
            }

            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Consumption is positive.");
            }

            return new UsageRecordAggregate(id, tenantId, siteId, metric, quantity, userId, source, operationId, occurredOnUtc);
        }

        /// <summary>Marks this row as counted. Called after the fold commits.</summary>
        public void MarkApplied() => FoldState = FoldState.Applied;

        /// <summary>Pseudonymises the row on user deletion: the count stays, the person does not.</summary>
        public void ForgetUser() => UserId = null;
    }
}
