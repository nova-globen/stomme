#nullable enable
using System;
using Stomme.Tenancy.Domain;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// Translates between Stomme's framework tenant key (<see cref="Models.Tenant.TenantId"/> — an int
    /// identity column on the master store) and the persistence seam's <see cref="TenantId"/> (a GUID value
    /// object). The seam was lifted from the predecessor platform, which keys tenants by GUID; Stomme's harvested framework
    /// keys them by int. The adapter has to bridge the two, and how it bridges them is load-bearing.
    /// <para>
    /// <b>The encoding:</b> the int is written big-endian into the last four bytes of an otherwise-zero GUID.
    /// It is therefore a total, reversible mapping — no lookup table, no schema change, no new column on the
    /// master store, and no state that could drift out of sync with the tenant row.
    /// </para>
    /// <para>
    /// <b>Why big-endian into the tail, specifically:</b> it makes tenant <c>1</c> — the "Master" tenant every
    /// Stomme install creates first — encode to exactly
    /// <c>00000000-0000-0000-0000-000000000001</c>, which *is* <see cref="TenantId.Default"/>. That is not a
    /// coincidence to be enjoyed and forgotten; it is the reason this change is safe to ship. Every existing
    /// single-tenant install already has its flags (and any other seam-persisted, tenant-owned aggregate)
    /// written at <see cref="TenantId.Default"/>. Registering a real tenant context that reports the tenant's
    /// *own* id would otherwise make the tenant query filter hide all of it — every flag silently reading
    /// <c>false</c>. Under this encoding the existing rows are already under the right key, so the adapter
    /// goes live with no data migration and no behaviour change for the single-tenant case (never a
    /// non-working window). <see cref="MapsDefaultTenantToSeamDefault"/> pins the property, and a test asserts
    /// it — if anyone changes the encoding, that test fails rather than the flags silently going dark.
    /// </para>
    /// <para>
    /// Tenants 2..n get their own distinct ids and DO need their rows seeded per tenant; the tail encoding
    /// buys compatibility for the existing tenant, not for new ones.
    /// </para>
    /// </summary>
    public static class FrameworkTenantId
    {
        /// <summary>
        /// True iff framework tenant 1 encodes to <see cref="TenantId.Default"/> — the compatibility property
        /// the whole adapter rests on. Asserted by a test; stated here so a reader of the encoding can check
        /// the claim without running it.
        /// </summary>
        public static bool MapsDefaultTenantToSeamDefault => ToTenantId(1).Equals(TenantId.Default);

        /// <summary>
        /// Encodes a framework tenant key as a seam <see cref="TenantId"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The key is not a real tenant. Framework tenant keys are identity values starting at 1; 0 and the
        /// sentinel -1 (<c>TenantManager.SetTenant</c> uses -1 for AliasId/SiteId) are not tenants, and a
        /// zero key would encode to <see cref="Guid.Empty"/>, which <see cref="TenantId.From"/> rejects.
        /// Callers that may hold a non-tenant use <see cref="TryToTenantId"/>.
        /// </exception>
        public static TenantId ToTenantId(int frameworkTenantId)
        {
            if (frameworkTenantId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameworkTenantId), frameworkTenantId,
                    "Framework tenant ids are identity values starting at 1.");
            }

            // Guid(int a, short b, short c, byte[] d): a/b/c stay zero, so only the trailing 8 bytes carry
            // anything. Writing the int big-endian into the LAST four is what puts tenant 1 on
            // 00000000-0000-0000-0000-000000000001 (== TenantId.Default).
            var tail = new byte[8];
            tail[4] = (byte)(frameworkTenantId >> 24);
            tail[5] = (byte)(frameworkTenantId >> 16);
            tail[6] = (byte)(frameworkTenantId >> 8);
            tail[7] = (byte)frameworkTenantId;

            return TenantId.From(new Guid(0, 0, 0, tail));
        }

        /// <summary>
        /// Encodes a framework tenant key, or returns null when the key is not a real tenant (0 or a
        /// sentinel). The non-throwing counterpart of <see cref="ToTenantId"/>.
        /// </summary>
        public static TenantId? ToTenantIdOrNull(int frameworkTenantId) =>
            frameworkTenantId > 0 ? ToTenantId(frameworkTenantId) : null;

        /// <summary>
        /// Decodes a seam <see cref="TenantId"/> back to its framework tenant key. Returns false for any GUID
        /// this class did not produce — e.g. a tenant id minted by another building block over the same seam —
        /// so a caller can tell "not one of ours" from a valid key rather than silently reading a wrong int
        /// out of unrelated bytes.
        /// </summary>
        public static bool TryToTenantId(TenantId? tenantId, out int frameworkTenantId)
        {
            frameworkTenantId = 0;

            if (tenantId is null)
            {
                return false;
            }

            // Guid.TryWriteBytes uses the same mixed-endian layout as ToByteArray: bytes 0-3 are the int
            // field, 4-5 and 6-7 the two shorts, and 8-15 the trailing byte array verbatim. All of those are
            // zero under this encoding except the last four.
            Span<byte> bytes = stackalloc byte[16];
            if (!tenantId.Value.TryWriteBytes(bytes))
            {
                return false;
            }

            for (var i = 0; i < 12; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }
            }

            frameworkTenantId = (bytes[12] << 24) | (bytes[13] << 16) | (bytes[14] << 8) | bytes[15];
            return frameworkTenantId > 0;
        }
    }
}
