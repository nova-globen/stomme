#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Health;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Connectivity.Health
{
    /// <summary>The switch: <c>Connectivity:HealthProbes:Enabled</c>, DEFAULT OFF (D110 Q3) - a probe dials out on the site's behalf every minute, which an operator turns on.</summary>
    public static class ConnectivityHealthProbes
    {
        public const string Key = "Connectivity:HealthProbes:Enabled";

        public static bool Enabled(IConfiguration? configuration) => bool.TryParse(configuration?[Key], out var enabled) && enabled;
    }

    /// <summary>
    /// The Connectivity module's health contribution: ONE Site-scope probe, <c>connections</c>,
    /// that tests every connection of the site it is asked about through the shared <see cref="ConnectionTestRunner"/>
    /// (the host's egress, the site's secrets, a category back), writes each verdict on its row, and answers the worst
    /// of them with each connection's outcome in its data. It replaces the predecessor platform's system-scoped heartbeat job, which
    /// dialled every connection of every tenant with no guard and no switch. When the switch is off the contributor
    /// offers NO probe, so nothing dials. The seam's probe list is fixed and the site is only known when a probe runs,
    /// which is why this is one probe per site over its connections rather than one probe per connection (D110's
    /// wording); a connection's own status is what the admin reads per connection.
    /// </summary>
    public sealed class ConnectionHealthContributor(bool enabled, IConnectionRepository connections, IUnitOfWork unitOfWork, ConnectionTestRunner runner, TimeProvider clock, ILogger<ConnectionHealthContributor> logger) : IHealthContributor
    {
        public const string Id = "connectivity";

        public const string ProbeName = "connections";

        public string ContributorId => Id;

        public IReadOnlyList<IHealthProbe> Probes { get; } = enabled
            ? new IHealthProbe[] { new ConnectionsProbe(connections, unitOfWork, runner, clock, logger) }
            : Array.Empty<IHealthProbe>();

        public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = Array.Empty<HealthMetricDescriptor>();

        public IHealthMetricSource? SourceFor(string metricName) => null;

        private sealed class ConnectionsProbe(IConnectionRepository connections, IUnitOfWork unitOfWork, ConnectionTestRunner runner, TimeProvider clock, ILogger logger) : IHealthProbe
        {
            public string Name => ProbeName;

            public string Dependency => "Connections";

            public HealthProbeScope Scope => HealthProbeScope.Site;

            public async Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                var watch = Stopwatch.StartNew();
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                IReadOnlyList<ConnectionAggregate> rows;
                try
                {
                    rows = await connections.ListForSiteAsync(context.SiteId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("The connections of site {SiteId} could not be listed for the health probe: {ExceptionType}.", context.SiteId, ex.GetType().FullName);
                    return new HealthProbeResult(Name, Dependency, HealthState.Unhealthy, "The site's connections could not be listed: " + ex.GetType().Name, watch.Elapsed);
                }

                var failing = new List<string>();
                foreach (var connection in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = await TestAndRecordAsync(connection, cancellationToken).ConfigureAwait(false);
                    data[connection.Name] = outcome.ToString();
                    if (!ConnectorOutcomes.IsSuccess(outcome))
                    {
                        failing.Add(connection.Name);
                    }
                }

                var state = failing.Count == 0 ? HealthState.Healthy : HealthState.Unhealthy;
                var description = rows.Count == 0
                    ? "No connections."
                    : failing.Count == 0 ? $"{rows.Count} connection(s) healthy." : $"{rows.Count - failing.Count} of {rows.Count} connection(s) healthy; failing: {string.Join(", ", failing)}.";
                return new HealthProbeResult(Name, Dependency, state, description, watch.Elapsed, data);
            }

            /// <summary>One connection: the shared runner's category, written on that row - one write per call, contained so one connection's trouble never costs another its verdict.</summary>
            private async Task<ConnectorOutcome> TestAndRecordAsync(ConnectionAggregate connection, CancellationToken cancellationToken)
            {
                var outcome = await runner.TestAsync(connection, cancellationToken).ConfigureAwait(false);
                try
                {
                    connection.RecordTestResult(ConnectorOutcomes.IsSuccess(outcome), ConnectorOutcomes.Message(outcome), clock.GetUtcNow().UtcDateTime);
                    await connections.UpdateAsync(connection, cancellationToken).ConfigureAwait(false);
                    await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("The verdict for connection {ConnectionId} could not be written: {ExceptionType}.", connection.Id, ex.GetType().FullName);
                }

                return outcome;
            }
        }
    }
}
