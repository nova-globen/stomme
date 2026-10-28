#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Mediator;
using Stomme.Models.Health;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Transport.Realtime;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// ONE run-now in flight per (tenant, site) - D104's binding for the only live probing besides the job. A second
    /// request while one runs is refused rather than queued: a queue would let a double-click, or a script, stack probe
    /// passes against the site's store, cache and SMTP host.
    /// </summary>
    /// <remarks>A singleton: the gate must be shared by every request scope of this process.</remarks>
    public sealed class HealthRunNowGate
    {
        private readonly ConcurrentDictionary<(Guid Tenant, int Site), byte> _running = new();

        /// <summary>Enters the gate for this key, or answers false when a run is already in flight.</summary>
        public bool TryEnter(Guid tenant, int site) => _running.TryAdd((tenant, site), 0);

        public void Exit(Guid tenant, int site) => _running.TryRemove((tenant, site), out _);
    }

    /// <summary>
    /// Runs this site's health probes NOW, plus the shared (Installation and Tenant) scopes the site's dashboard shows,
    /// through the SAME pass the job runs (D104 items 5 and 8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>health:manage</c> only, never anonymous</b>: a probe drives database round-trips, cache writes and a TCP connect
    /// to the site's SMTP host, so the only live probing is the job and this. Each run is bounded by the job's own tick
    /// budget.
    /// </para>
    /// <para>
    /// <b>The readiness snapshot is NOT touched.</b> It is the job's verdict across every site of the tenant; a run for
    /// one site must not overwrite it.
    /// </para>
    /// <para>
    /// <b>The shared scopes run under their own gate</b> (the tenant with site 0), so two sites of one tenant pressing the
    /// button together do not probe the shared store twice at once.
    /// </para>
    /// </remarks>
    [Requires(HealthPermissions.Manage)]
    public sealed record RunHealthProbesNowCommand : ICommand<HealthRunNowResult>;

    public sealed class RunHealthProbesNowCommandHandler(
        HealthProbePass pass,
        HealthRunNowGate gate,
        ILogger<RunHealthProbesNowCommandHandler> logger,
        SiteState siteState,
        ITenantContext tenantContext,
        IHealthRealtimePush? push = null)
        : ICommandHandler<RunHealthProbesNowCommand, HealthRunNowResult>
    {
        /// <summary>The refusal a caller gets while a run is in flight - the controller answers it as 409.</summary>
        public const string AlreadyRunning = "A health check is already running for this site.";

        public async Task<HealthRunNowResult> Handle(RunHealthProbesNowCommand request, CancellationToken ct)
        {
            var siteId = HealthWire.RequireSiteId(siteState);
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("The health API requires a resolved tenant.");

            if (!gate.TryEnter(tenantId.Value, siteId))
            {
                throw new InvalidOperationException(AlreadyRunning);
            }

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(HealthProbeJob.TickBudget);

                var probes = 0;
                var worst = HealthState.Healthy;
                void Take(HealthPassResult result)
                {
                    probes += result.ProbesRun;
                    worst = result.Worst > worst ? result.Worst : worst;
                }

                // The shared scopes, once per tenant, under their OWN gate: another site of this tenant may be running them.
                if (gate.TryEnter(tenantId.Value, 0))
                {
                    try
                    {
                        foreach (var scope in new[] { HealthProbeScope.Installation, HealthProbeScope.Tenant })
                        {
                            Take(await pass.RunAsync(tenantId, scope, 0, budget.Token));
                        }
                    }
                    finally
                    {
                        gate.Exit(tenantId.Value, 0);
                    }
                }

                Take(await pass.RunAsync(tenantId, HealthProbeScope.Site, siteId, budget.Token));

                // Every open health page of this site - not only the caller's - learns that a run happened.
                if (push is not null && siteState.Alias is { } alias)
                {
                    await HealthProbeJob.PushAsync(push, alias.TenantId, siteId, worst, logger);
                }

                return new HealthRunNowResult(probes, worst);
            }
            finally
            {
                gate.Exit(tenantId.Value, siteId);
            }
        }
    }
}
