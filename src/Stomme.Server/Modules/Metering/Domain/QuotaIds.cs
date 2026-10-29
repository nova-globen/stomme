#nullable enable
using System;
using System.Globalization;
using Stomme.Metering;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// Deterministic identities for metering rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deterministic id is what makes the seeder and the recorder idempotent WITHOUT a transaction: the same
    /// (tenant, site, metric, period) always names the same quota row, and the same (tenant, site, metric,
    /// operation) always names the same ledger row, so "create if absent" is a GetById followed by an Add or an
    /// Update.
    /// </para>
    /// <para>
    /// It is not enough on its own, and that was measured: only RavenDB's Add is a blind upsert, while EF raises a
    /// primary-key violation and the File store throws on an existing path. So every idempotent write reads by id
    /// first rather than relying on the id alone.
    /// </para>
    /// <para>
    /// <b>The TENANT is part of every key.</b> Two tenants can share
    /// one physical database and point at the SAME site — site-per-alias is a supported topology, and
    /// <c>MeteringJobTenantScopeTests</c> constructs exactly it. Without the tenant, both tenants' rows for that
    /// site hash to the SAME Guid, which in one shared table is a primary-key collision: on EF the second tenant's
    /// write throws, and where the write is the idempotent GetById-then-Update the tenant query filter hides the
    /// other tenant's row, so the second tenant reads a MISS and overwrites. The defect was found while writing
    /// equivalent ids elsewhere, and is fixed here by keying on the tenant, because changing the identity of every
    /// metering row would be far more expensive once anyone has data (<c>Metering:Enabled</c> defaults to false).
    /// </para>
    /// </remarks>
    public static class QuotaIds
    {
        /// <summary>The id of the quota for a metric and period on a site of a tenant.</summary>
        public static Guid For(TenantId tenantId, int siteId, string metric, QuotaPeriod period) =>
            AiUsageRollupAggregate.DeterministicId(
                string.Join('|', "metering.quota", Tenant(tenantId), siteId.ToString(CultureInfo.InvariantCulture),
                    MetricKeys.Normalize(metric), period.ToString()));

        /// <summary>The id of the ledger row for one operation's consumption of one metric on a site of a tenant.</summary>
        public static Guid ForUsage(TenantId tenantId, int siteId, string metric, Guid operationId) =>
            AiUsageRollupAggregate.DeterministicId(
                string.Join('|', "metering.usage", Tenant(tenantId), siteId.ToString(CultureInfo.InvariantCulture),
                    MetricKeys.Normalize(metric), operationId.ToString("N")));

        /// <summary>The id of the AI ledger row for one operation on a site of a tenant.</summary>
        public static Guid ForAiUsage(TenantId tenantId, int siteId, Guid operationId) =>
            AiUsageRollupAggregate.DeterministicId(
                string.Join('|', "metering.aiusage", Tenant(tenantId), siteId.ToString(CultureInfo.InvariantCulture),
                    operationId.ToString("N")));

        private static string Tenant(TenantId tenantId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return tenantId.Value.ToString("N", CultureInfo.InvariantCulture);
        }
    }
}
