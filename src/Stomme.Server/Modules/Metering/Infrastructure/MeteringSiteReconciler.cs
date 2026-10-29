#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Metering.Infrastructure
{
    /// <summary>One quota a module declares every site should carry. The metric key must be canonical.</summary>
    /// <param name="Metric">The canonical metric key, e.g. <c>ai.tokens</c>.</param>
    /// <param name="Period">The window the allowance counts over.</param>
    /// <param name="Limit">The allowance, or <see cref="QuotaAggregate.UnlimitedLimit"/>.</param>
    /// <param name="WarnAtPercent">Where a warning is raised once per window; 0 disables it.</param>
    public sealed record DeclaredQuota(string Metric, QuotaPeriod Period, long Limit, int WarnAtPercent);

    /// <summary>
    /// A contributor of quotas a site should carry. The metering module itself ships NONE (a stock install
    /// seeds nothing - the default quotas are a catalog an operator adopts, not rows a fresh install
    /// invents), and a later module that meters brings its own source, the way the notification template sources work.
    /// </summary>
    public interface IDeclaredQuotaSource
    {
        /// <summary>A human name, used in the log line that reports a duplicate or a failure.</summary>
        string Name { get; }

        IReadOnlyList<DeclaredQuota> Declare(int siteId);
    }

    /// <summary>
    /// Brings each site's declared quotas up to date on the boot/upgrade path - ADDITIVELY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is an <see cref="ISiteContentReconciler"/> and not a hosted service.</b> A seeder run from a hosted service
    /// would run once, for whichever tenant the ambient scope happened
    /// to hold. Instead the framework's <see cref="SiteContentReconciliationRunner"/> enters each tenant's scope
    /// EXPLICITLY, creates a fresh DI scope per site, and contains a failure to that one site - the three things
    /// a single-scope, single-catch loop does not do.
    /// </para>
    /// <para>
    /// <b>Additive by construction.</b> Each declaration names a deterministic row id, and an existing row is left
    /// exactly as it is - an operator who lowered a limit or suspended a quota keeps that across every boot. Only
    /// an absent row is created. That is also what makes a second boot a no-op on the File store, whose
    /// <c>AddAsync</c> throws on an existing path rather than upserting.
    /// </para>
    /// </remarks>
    public sealed class MeteringSiteReconciler : ISiteContentReconciler
    {
        private readonly IEnumerable<IDeclaredQuotaSource> _sources;
        private readonly ILogger<MeteringSiteReconciler> _logger;

        public MeteringSiteReconciler(IEnumerable<IDeclaredQuotaSource> sources, ILogger<MeteringSiteReconciler> logger)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Metering quotas";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var siteId = context.Site.SiteId;
            var quotas = context.Services.GetRequiredService<IQuotaRepository>();
            var unitOfWork = context.Services.GetRequiredService<IUnitOfWork>();
            var seen = new HashSet<Guid>();
            var created = new List<string>();

            foreach (var source in _sources)
            {
                foreach (var declared in source.Declare(siteId))
                {
                    string metric;
                    try
                    {
                        metric = MetricKeys.RequireMetric(declared.Metric);
                    }
                    catch (ArgumentException ex)
                    {
                        // A source is refused, never rewritten - the same rule the recording port applies.
                        _logger.LogError(ex, "The quota source '{Source}' declared the invalid metric key '{Metric}'; it is skipped.", source.Name, declared.Metric);
                        continue;
                    }

                    var id = QuotaIds.For(context.TenantId, siteId, metric, declared.Period);
                    if (!seen.Add(id))
                    {
                        _logger.LogWarning("The quota '{Metric}' ({Period}) is declared more than once for site {SiteId}; the first declaration wins.", metric, declared.Period, siteId);
                        continue;
                    }

                    if (await quotas.GetByIdAsync(id, ct) is not null)
                    {
                        continue; // exists - possibly edited by an operator; never re-processed
                    }

                    var quota = QuotaAggregate.Define(id, context.TenantId, siteId, metric, declared.Period, declared.Limit, declared.WarnAtPercent, DateTime.UtcNow);
                    await quotas.AddAsync(quota, ct);
                    created.Add($"{metric} ({declared.Period})");
                }
            }

            if (created.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation("Created {Count} quota(s) on site {SiteId}: {Quotas}", created.Count, siteId, string.Join(", ", created));
            }
        }
    }
}
