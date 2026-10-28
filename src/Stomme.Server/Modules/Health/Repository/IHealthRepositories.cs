#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Health.Persistence
{
    /// <summary>
    /// The module's closed read surface (D104).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No predicate here mentions the subject.</b> It is a string, and strings do not agree across the three
    /// providers a module store can be: bare equality is case-sensitive on EF and the File store and
    /// case-INSENSITIVE on RavenDB, and <c>RavenRepository.FindAsync</c> has no rewriting, so <c>Contains</c>
    /// throws outright. Every filter therefore uses only the safe set — <c>int</c> site ids, enums, and UTC
    /// <see cref="DateTime"/> ranges — and any subject matching happens in memory over the narrowed set, or, better,
    /// is avoided entirely by addressing the row through its deterministic id.
    /// </para>
    /// <para>
    /// <b>The by-id rule.</b> Every by-id read goes through <c>GetForCurrentSiteAsync</c>, so a caller naming
    /// another site's row gets the SAME not-found answer as one naming a row that does not exist. Deterministic
    /// ids are guessable by construction — that is what makes them deterministic — so a by-id read that only
    /// checked existence would turn the id space into an oracle for other sites' configuration and health.
    /// </para>
    /// </remarks>
    public interface IHealthRollupRepository : IRepository<HealthRollupAggregate, Guid>
    {
        /// <summary>Every rollup of one window for a site, newest window first.</summary>
        Task<IReadOnlyList<HealthRollupAggregate>> ListAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, CancellationToken ct = default);

        /// <summary>The rollups of one window inside a half-open UTC range — what a downsample re-reads.</summary>
        Task<IReadOnlyList<HealthRollupAggregate>> ListForRangeAsync(
            HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

        /// <summary>The rollups of one window older than a cutoff — what retention deletes.</summary>
        Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAsync(
            HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default);

        /// <summary>Every rollup of one window older than a cutoff, for ANY scope and site — the retention sweep.</summary>
        Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAnywhereAsync(
            int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default);
    }

    public interface IHealthAlertStateRepository : IRepository<HealthAlertStateAggregate, Guid>
    {
        /// <summary>Every alert row for a site, whatever its state.</summary>
        Task<IReadOnlyList<HealthAlertStateAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>The alert row for one subject, addressed by its deterministic id.</summary>
        Task<HealthAlertStateAggregate?> GetAsync(TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject, CancellationToken ct = default);

        /// <summary>The row with this id IF it belongs to <paramref name="siteId"/>; otherwise null.</summary>
        Task<HealthAlertStateAggregate?> GetForCurrentSiteAsync(Guid id, int siteId, CancellationToken ct = default);
    }
}
