#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Health
{
    /// <summary>
    /// How healthy something is. <b>The numeric order is load-bearing</b> — the aggregator folds a set of results
    /// to the WORST of them by taking the maximum, and the alert machine compares states with &lt; and &gt;.
    /// Renumbering these silently changes both.
    /// </summary>
    public enum HealthState
    {
        Healthy = 0,
        Degraded = 1,
        Unhealthy = 2
    }

    /// <summary>
    /// What a probe is answering ABOUT, which decides how often it runs and who may see its detail.
    /// </summary>
    /// <remarks>
    /// This is an explicit column rather than an inference from the site id, because the id cannot carry it: the
    /// mock alias the job path uses carries <c>SiteId = -1</c> (<c>TenantManager</c>), which collides with the
    /// obvious "-1 means installation-wide" sentinel. Non-site scopes therefore use <c>SiteId = 0</c> and say
    /// which scope they are; a Site-scope row with a non-positive site id is rejected rather than stored.
    /// </remarks>
    public enum HealthProbeScope
    {
        /// <summary>One answer for the whole installation (the cache, the process).</summary>
        Installation,

        /// <summary>One answer per tenant (the tenant's store).</summary>
        Tenant,

        /// <summary>One answer per site (the site's configured SMTP host).</summary>
        Site
    }

    /// <summary>What a probe is told. Deliberately one value: a probe may not reach for ambient request state.</summary>
    /// <param name="SiteId">
    /// The site being probed, or <c>0</c> for an Installation- or Tenant-scope probe. A probe that ignores it is
    /// normal; a probe that needs more than it is a probe that is reaching too far.
    /// </param>
    public sealed record HealthProbeContext(int SiteId);

    /// <summary>One probe's answer.</summary>
    /// <param name="Name">The probe's stable key, unique within its contributor (for example <c>store</c>).</param>
    /// <param name="Dependency">What was probed, in an operator's words (for example <c>PostgreSQL</c>).</param>
    /// <param name="State">The verdict.</param>
    /// <param name="Description">
    /// Why, for a human. <b>This may carry the raw exception message, so it is privileged</b> — host and port,
    /// database and user names all show up here. It is surfaced only through the gated health API, and NEVER on
    /// the anonymous endpoints or in a notification; those carry the exception TYPE instead.
    /// </param>
    /// <param name="Duration">How long the probe took — the degraded-latency threshold is applied to this.</param>
    /// <param name="Data">Optional detail for the admin UI. Same privilege as <paramref name="Description"/>.</param>
    public sealed record HealthProbeResult(
        string Name,
        string Dependency,
        HealthState State,
        string? Description,
        TimeSpan Duration,
        IReadOnlyDictionary<string, string>? Data = null);

    /// <summary>
    /// One checkable dependency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An implementation must not throw</b>, and must return <see cref="HealthState.Unhealthy"/> with a
    /// description instead. The runner catches anyway — a contributed probe is somebody else's code and the tick
    /// must survive it — but a probe that routinely throws hides its own reason behind a generic one.
    /// </para>
    /// <para>
    /// <b>A probe that only resolves a service is not a probe.</b> Asking the container for a client proves the
    /// container can build it, never that the dependency is reachable, so such a probe can only ever report
    /// Healthy — a green light wired to nothing. Such a probe is a common trap.
    /// A scanner enforces it, and until a dependency exposes a real liveness call, its probe does not ship.
    /// </para>
    /// <para>
    /// <b>It is not handed the tick's budget to obey.</b> It receives a token and should honour it, but the runner
    /// does not trust that: it abandons a probe that overruns, because the connect calls these probes make are
    /// exactly the ones that ignore tokens.
    /// </para>
    /// </remarks>
    public interface IHealthProbe
    {
        /// <summary>Stable key, unique within the contributor.</summary>
        string Name { get; }

        /// <summary>What is being probed, named for an operator.</summary>
        string Dependency { get; }

        /// <summary>Whether this runs once per installation, per tenant, or per site.</summary>
        HealthProbeScope Scope { get; }

        /// <summary>Checks the dependency. Must not throw.</summary>
        Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>What a metric IS, separately from reading one.</summary>
    /// <param name="Name">Canonical dotted key, for example <c>runtime.workingset</c>.</param>
    /// <param name="Unit">The unit the value is in, for example <c>bytes</c> — never encoded into the name.</param>
    /// <param name="Description">Optional human description for the admin UI.</param>
    public sealed record HealthMetricDescriptor(string Name, string Unit, string? Description = null);

    /// <summary>Reads one metric's current value.</summary>
    /// <remarks>
    /// Split from <see cref="HealthMetricDescriptor"/> on purpose: the catalogue of what EXISTS is needed to render
    /// a settings page and a rollup table without taking a reading, and a reading is the expensive half.
    /// </remarks>
    public interface IHealthMetricSource
    {
        /// <summary>What this source reads.</summary>
        HealthMetricDescriptor Descriptor { get; }

        /// <summary>The current value. Must not throw; an unavailable metric returns <c>null</c>.</summary>
        Task<double?> ReadAsync(HealthProbeContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A module's contribution to health: its probes and its metrics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered the way every other contribution in this framework is discovered — by assembly scan over the
    /// loaded modules — so a module contributes by existing, not by editing framework code. The framework itself
    /// contributes through exactly the same interface, with no privileged path.
    /// </para>
    /// <para>
    /// <b><see cref="ContributorId"/> must be unique across the install.</b> Duplicates are a build failure
    /// in-tree; a duplicate arriving in a packaged module is REJECTED at runtime with an Error naming both, rather
    /// than silently letting one shadow the other.
    /// </para>
    /// </remarks>
    public interface IHealthContributor
    {
        /// <summary>Stable id, unique across the installation (for example <c>stomme</c>).</summary>
        string ContributorId { get; }

        /// <summary>The probes this contributor offers. May be empty.</summary>
        IReadOnlyList<IHealthProbe> Probes { get; }

        /// <summary>The metrics this contributor offers, as descriptors. May be empty.</summary>
        IReadOnlyList<HealthMetricDescriptor> Metrics { get; }

        /// <summary>
        /// The reader for <paramref name="metricName"/>, or <c>null</c> when this contributor does not offer it.
        /// </summary>
        IHealthMetricSource? SourceFor(string metricName);
    }
}
