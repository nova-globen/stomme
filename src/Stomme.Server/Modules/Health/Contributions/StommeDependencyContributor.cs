#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;

namespace Stomme.Modules.Health.Contributions
{
    /// <summary>
    /// The framework's own probes and metrics, contributed through exactly the same seam a module uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three probes, not five.</b> the predecessor platform registered five; two of them — the message bus and SignalR — only
    /// called <c>GetRequiredService</c>, which proves the container can build a client and nothing about whether
    /// anything is reachable. Such a probe can only ever report Healthy, so it is a green light wired to nothing,
    /// and it is worse than having no probe at all because it answers the question falsely. They do not ship here
    /// until those dependencies expose a real liveness call; the gap is recorded rather than papered over with a
    /// probe that always passes.
    /// </para>
    /// <para>
    /// <b>The store probe is provider-NEUTRAL, and that is a deviation worth stating.</b> The design said
    /// "relational round-trip through the framework seam, Raven <c>GetStatisticsOperation</c>" — a per-provider
    /// branch. Doing that would put a RavenDB package reference inside a storage-blind module, which is the one
    /// thing the clean-arch stack forbids (D79/D80): a module authors no persistence code and no provider branch.
    /// Instead it loads a known-absent id through the module's own repository. That is a real round-trip on every
    /// provider — a keyed lookup on EF, a document load on Raven, a file probe on the File store — it returns
    /// nothing rather than reading anyone's data, and it exercises the store the health rows THEMSELVES live in,
    /// which is the store that most needs to be up.
    /// </para>
    /// <para>
    /// <b>The SMTP probe is enumerated only when the site has a host configured.</b> the predecessor platform gated it on
    /// <c>SMTPEnabled</c>, which defaults to "True" with an empty host — so a stock site would have reported its
    /// mail server unreachable for ever, having never been asked to have one. A site with no host has nothing to
    /// probe, and says nothing rather than something false.
    /// </para>
    /// </remarks>
    public sealed class StommeDependencyContributor : IHealthContributor
    {
        public const string Id = "stomme";

        public const string StoreProbe = "store";
        public const string CacheProbe = "cache";
        public const string SmtpProbe = "smtp";

        public const string WorkingSetMetric = "runtime.workingset";
        public const string GcHeapMetric = "runtime.gcheap";

        private readonly IRepository<HealthAlertStateAggregate, Guid> _alertStates;
        private readonly ICacheManager _cache;
        private readonly ISettingRepository _settings;
        private readonly HealthSettingsAccessor _thresholds;
        private readonly Lazy<IReadOnlyList<IHealthProbe>> _probes;

        public StommeDependencyContributor(
            IRepository<HealthAlertStateAggregate, Guid> alertStates,
            ICacheManager cache,
            ISettingRepository settings,
            HealthSettingsAccessor thresholds)
        {
            _alertStates = alertStates;
            _cache = cache;
            _settings = settings;
            _thresholds = thresholds;
            _probes = new Lazy<IReadOnlyList<IHealthProbe>>(BuildProbes);
        }

        public string ContributorId => Id;

        public IReadOnlyList<IHealthProbe> Probes => _probes.Value;

        public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = new[]
        {
            new HealthMetricDescriptor(WorkingSetMetric, "bytes", "Resident memory of the host process."),
            new HealthMetricDescriptor(GcHeapMetric, "bytes", "Managed heap currently allocated.")
        };

        public IHealthMetricSource? SourceFor(string metricName) => metricName switch
        {
            WorkingSetMetric => new RuntimeMetricSource(Metrics[0], () => Environment.WorkingSet),
            GcHeapMetric => new RuntimeMetricSource(Metrics[1], () => GC.GetTotalMemory(forceFullCollection: false)),
            _ => null
        };

        private IReadOnlyList<IHealthProbe> BuildProbes() => new IHealthProbe[]
        {
            new DependencyHealthProbe(
                StoreProbe,
                "Module Store",
                HealthProbeScope.Tenant,
                async (_, ct) =>
                {
                    // A known-absent id: a genuine round-trip that reads nobody's data and returns nothing.
                    await _alertStates.GetByIdAsync(Guid.Empty, ct).ConfigureAwait(false);
                },
                _thresholds.Current.DegradedLatency,
                _thresholds.Current.ProbeTimeout),

            new DependencyHealthProbe(
                CacheProbe,
                "Cache",
                HealthProbeScope.Installation,
                async (_, _) =>
                {
                    // Write through the factory then remove: a read alone would be served from L1 and prove
                    // nothing about an L2 that has gone away.
                    var key = "health:probe:" + Guid.NewGuid().ToString("N");
                    await _cache.GetCacheAsync(key, _ => Task.FromResult("ok")).ConfigureAwait(false);
                    await _cache.RemoveCacheAsync(key).ConfigureAwait(false);
                },
                _thresholds.Current.DegradedLatency,
                _thresholds.Current.ProbeTimeout),

            new DependencyHealthProbe(
                SmtpProbe,
                "Mail Server",
                HealthProbeScope.Site,
                async (context, ct) =>
                {
                    var (host, port) = await SmtpEndpointAsync(context.SiteId).ConfigureAwait(false);
                    if (host is null)
                    {
                        // Enumeration should have excluded this site; if anything changed under us, say nothing
                        // false rather than reporting a mail server that was never configured as unreachable.
                        return;
                    }

                    using var client = new TcpClient();
                    await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                },
                _thresholds.Current.DegradedLatency,
                _thresholds.Current.ProbeTimeout)
        };

        /// <summary>
        /// Whether <paramref name="probe"/> applies to <paramref name="siteId"/> — the enumeration decision that
        /// keeps a site with no mail server from being told its mail server is down.
        /// </summary>
        public async Task<bool> AppliesAsync(IHealthProbe probe, int siteId)
        {
            if (probe.Name != SmtpProbe)
            {
                return true;
            }

            var (host, _) = await SmtpEndpointAsync(siteId).ConfigureAwait(false);
            return host is not null;
        }

        private async Task<(string? Host, int Port)> SmtpEndpointAsync(int siteId)
        {
            try
            {
                var rows = await _settings.GetSettingsAsync(EntityNames.Site, siteId, EntityNames.Host, -1);
                var host = _settings.GetSettingValue(rows, "SMTPHost", string.Empty);
                if (string.IsNullOrWhiteSpace(host))
                {
                    return (null, 0);
                }

                var raw = _settings.GetSettingValue(rows, "SMTPPort", string.Empty);
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
                    ? (host, port)
                    : (null, 0);
            }
            catch
            {
                // Unreadable settings are not a mail-server verdict.
                return (null, 0);
            }
        }

        /// <summary>A process counter, read on demand.</summary>
        private sealed class RuntimeMetricSource : IHealthMetricSource
        {
            private readonly Func<double> _read;

            public RuntimeMetricSource(HealthMetricDescriptor descriptor, Func<double> read)
            {
                Descriptor = descriptor;
                _read = read;
            }

            public HealthMetricDescriptor Descriptor { get; }

            public Task<double?> ReadAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                try
                {
                    return Task.FromResult<double?>(_read());
                }
                catch
                {
                    return Task.FromResult<double?>(null);
                }
            }
        }
    }
}
