#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Mediator;
using Stomme.Models.Health;
using Stomme.Modules.Health.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The gated read surface. Every request carries <c>[Requires]</c>, and none of them names a site, a tenant or a
    /// user — all three are resolved server-side, so there is no parameter to forge.
    /// </summary>
    internal static class HealthWire
    {
        /// <summary>The caller's site, or a refusal. A query that cannot tell which site it is for must not guess.</summary>
        public static int RequireSiteId(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0
                ? siteId
                : throw new InvalidOperationException("The health API requires a resolved site.");

        /// <summary>
        /// Turns an alert row into its wire shape, revealing the privileged description only when the caller is
        /// entitled to it.
        /// </summary>
        /// <remarks>
        /// The description carries the raw exception message — host, port, database, user. A site's
        /// <c>health:view</c> holder may see it for that site's OWN subjects; for a Tenant- or Installation-scope
        /// subject, which describes a dependency SHARED with other sites, it is Host-only. The site's operator
        /// still sees the state and the timestamp, which is what they can act on.
        /// </remarks>
        public static HealthSubjectView ToView(HealthAlertStateAggregate row, bool isHost) => new(
            row.Subject.Value,
            DependencyOf(row.Subject),
            row.Scope,
            row.CurrentState,
            row.Scope == HealthProbeScope.Site || isHost ? row.LastDescription : null,
            0d,
            row.BadSinceUtc,
            row.AlertRaisedOnUtc,
            row.AcknowledgedOnUtc,
            row.AlertCount);

        public static HealthRollupView ToView(HealthRollupAggregate row) => new(
            row.Subject.Value,
            row.Scope,
            row.Window.ToString(),
            row.PeriodStartUtc,
            row.Checks,
            row.Healthy,
            row.Degraded,
            row.Unhealthy,
            row.AverageLatencyMs,
            row.MaxLatencyMs,
            row.AvailabilityPercent);

        /// <summary>The probe half of a subject, for display — "stomme:store" reads as "store".</summary>
        private static string DependencyOf(HealthSubject subject)
        {
            var value = subject.Value;
            var at = value.IndexOf(':');
            return at >= 0 && at < value.Length - 1 ? value[(at + 1)..] : value;
        }
    }

    /// <summary>This site's current health, plus whatever alert is outstanding.</summary>
    [Requires(global::Stomme.Health.HealthPermissions.View)]
    public sealed record GetHealthDashboardQuery : IQuery<HealthDashboard>;

    /// <remarks>
    /// <b>The switch is read from the MERGED configuration</b> (<see cref="Microsoft.Extensions.Configuration.IConfigurationRoot"/>),
    /// the one the probe job reads through <c>HostConfiguration</c>: the container's plain <c>IConfiguration</c> lacks
    /// <c>appsettings.override.json</c>, where an install keeps <c>Health:Enabled</c>. Optional, so a caller that
    /// composes the handler by hand still can; absent, it answers the switch's own default.
    /// </remarks>
    public sealed class GetHealthDashboardQueryHandler(
        IHealthAlertStateRepository alertStates,
        HealthSnapshot snapshot,
        SiteState siteState,
        ICurrentUser currentUser,
        Microsoft.Extensions.Configuration.IConfigurationRoot? configuration = null)
        : IQueryHandler<GetHealthDashboardQuery, HealthDashboard>
    {
        public async Task<HealthDashboard> Handle(GetHealthDashboardQuery request, CancellationToken ct)
        {
            var siteId = HealthWire.RequireSiteId(siteState);
            // Host is a ROLE on the principal, not a permission key: the privileged description of a SHARED
            // dependency is installation-wide information, so it sits above this site's own keys.
            var isHost = currentUser.Roles.Contains(RoleNames.Host);

            // This site's own rows, plus the wider-scope rows that describe dependencies it stands on. Those
            // carry SiteId 0, so they are not "another site's rows" - they are shared, and every site is entitled
            // to know whether the store it uses is up.
            var own = await alertStates.ListBySiteAsync(siteId, ct);
            var shared = await alertStates.ListBySiteAsync(0, ct);

            var subjects = own.Concat(shared)
                .OrderBy(row => row.Scope)
                .ThenBy(row => row.Subject.Value, StringComparer.Ordinal)
                .Select(row => HealthWire.ToView(row, isHost))
                .ToList();

            var last = snapshot.Last;
            return new HealthDashboard(
                subjects.Count == 0 ? HealthState.Healthy : subjects.Max(view => view.State),
                last?.AtUtc,
                subjects,
                subjects.Where(view => view.AlertRaisedOnUtc is not null).ToList(),
                HealthSettings.ProbingEnabled(configuration));
        }
    }

    /// <summary>This site's rollup history for one window.</summary>
    [Requires(global::Stomme.Health.HealthPermissions.View)]
    public sealed record ListHealthRollupsQuery(string Window = "Hour", int Take = 200) : IQuery<IReadOnlyList<HealthRollupView>>;

    public sealed class ListHealthRollupsQueryHandler(IHealthRollupRepository rollups, SiteState siteState)
        : IQueryHandler<ListHealthRollupsQuery, IReadOnlyList<HealthRollupView>>
    {
        public async Task<IReadOnlyList<HealthRollupView>> Handle(ListHealthRollupsQuery request, CancellationToken ct)
        {
            var siteId = HealthWire.RequireSiteId(siteState);
            var window = Enum.TryParse<HealthRollupWindow>(request.Window, ignoreCase: true, out var parsed)
                ? parsed
                : HealthRollupWindow.Hour;

            var rows = new List<HealthRollupAggregate>();
            foreach (var scope in new[] { HealthProbeScope.Site, HealthProbeScope.Tenant, HealthProbeScope.Installation })
            {
                rows.AddRange(await rollups.ListAsync(scope, scope == HealthProbeScope.Site ? siteId : 0, window, ct));
            }

            return rows
                .OrderByDescending(row => row.PeriodStartUtc)
                .Take(Math.Clamp(request.Take, 1, 1000))
                .Select(HealthWire.ToView)
                .ToList();
        }
    }

    /// <summary>Acknowledges one outstanding alert of THIS site. Acknowledging silences nothing; it records who knows.</summary>
    [Requires(global::Stomme.Health.HealthPermissions.Manage)]
    public sealed record AcknowledgeHealthAlertCommand(Guid Id) : ICommand<Unit>;

    public sealed class AcknowledgeHealthAlertCommandHandler(
        IHealthAlertStateRepository alertStates,
        IUnitOfWork unitOfWork,
        SiteState siteState)
        : ICommandHandler<AcknowledgeHealthAlertCommand, Unit>
    {
        public async Task<Unit> Handle(AcknowledgeHealthAlertCommand request, CancellationToken ct)
        {
            var siteId = HealthWire.RequireSiteId(siteState);

            // Through the site-scoped read, never a bare GetByIdAsync: ids here are DETERMINISTIC and therefore
            // guessable, so a by-id handler that only checked existence would be a cross-site IDOR.
            var row = await alertStates.GetForCurrentSiteAsync(request.Id, siteId, ct)
                ?? throw new InvalidOperationException("No such health alert on this site.");

            row.Acknowledge(DateTime.UtcNow);
            await alertStates.UpdateAsync(row, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }
}
