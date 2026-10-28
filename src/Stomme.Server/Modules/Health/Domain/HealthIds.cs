#nullable enable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stomme.Health;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// The deterministic identities every health row is addressed by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what replaces a query.</b> The natural way to write these jobs is "find the row for this subject
    /// and update it", but the subject is a STRING and string predicates do not agree across the three providers
    /// the module store can be (bare equality case-sensitive on EF and File, case-insensitive on Raven;
    /// <c>Contains</c> throws on Raven; the File store filters in memory with last-write-wins). Deriving the id
    /// from the natural key instead turns every upsert into GetById-then-Add-or-Update, which behaves identically
    /// everywhere and needs no index.
    /// </para>
    /// <para>
    /// <b>The site id is inside the key, always.</b> Two sites reporting the same subject are two rows; leaving it
    /// out would have one site's probe silently overwrite another's — and, worse, make one site's health readable
    /// by fetching the other's id.
    /// </para>
    /// <para>
    /// <b>And so is the TENANT, which is less obvious and matters more.</b> Two tenants can share one physical
    /// database — site-per-alias is a supported topology, and the job gates exercise it — and site ids are NOT
    /// unique across tenants in that arrangement. A key of (scope, site, subject) would then produce the same
    /// primary key for two different tenants' rows in one table: a straight collision, after which the tenant
    /// query filter hides the surviving row from at least one of them. Putting the tenant in the key removes the
    /// class outright instead of relying on site ids happening not to overlap.
    /// </para>
    /// <para>
    /// The hash is SHA-256 truncated to 16 bytes, the same construction the metering ids use. It is a naming
    /// scheme, not a security boundary: ids are guessable by design (that is what makes them deterministic), which
    /// is exactly why every by-id read goes through the site-scoped repository rather than a bare
    /// <c>GetByIdAsync</c>.
    /// </para>
    /// </remarks>
    public static class HealthIds
    {
        /// <summary>The id of one rollup row: tenant, scope, site, subject, window and the window's start.</summary>
        public static Guid ForRollup(TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject, HealthRollupWindow window, DateTime periodStartUtc) =>
            DeterministicId(string.Join(
                '|',
                "health.rollup",
                tenantId.Value.ToString("N", CultureInfo.InvariantCulture),
                scope.ToString(),
                siteId.ToString(CultureInfo.InvariantCulture),
                subject.Value,
                window.ToString(),
                periodStartUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        /// <summary>
        /// The id of one alert-state row: scope, site and subject. Deliberately NOT time-based — there is exactly
        /// ONE alert state per subject per scope, which is what makes "one shared store outage raises one alert"
        /// true rather than one alert per site.
        /// </summary>
        public static Guid ForAlertState(TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject) =>
            DeterministicId(string.Join(
                '|',
                "health.alert",
                tenantId.Value.ToString("N", CultureInfo.InvariantCulture),
                scope.ToString(),
                siteId.ToString(CultureInfo.InvariantCulture),
                subject.Value));

        /// <summary>A stable GUID from a key — the same shape the metering ids use.</summary>
        internal static Guid DeterministicId(string key)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            return new Guid(bytes);
        }
    }
}
