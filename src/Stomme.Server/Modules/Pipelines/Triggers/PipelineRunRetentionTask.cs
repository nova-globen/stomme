#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// n8n's execution pruning: the site's FINISHED runs that last changed more than <c>Pipelines:Runs:KeepDays</c>
    /// ago are deleted through the engine's instance manager, whose deleting notification takes their log, their steps'
    /// records and their bookmarks with them (the engine's own handlers). Off by default - 0 keeps every run, so an install
    /// loses nothing it did not ask to. A run still going (running, waiting, suspended) is never deleted however old. Per
    /// site tenant, on one node at a time, like the schedules task; at most <see cref="MaxPerTick"/> runs a tick, oldest
    /// first, in batches, so turning it on over a long history spreads the deleting.
    /// </summary>
    [SingleNodeTask]
    public sealed class PipelineRunRetentionTask(
        IPipelineInstanceStore instances,
        IKeyValueStore keyValues,
        IPipelineInstanceManager manager,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        IOptions<PipelineTriggerOptions> options,
        ILogger<PipelineRunRetentionTask> logger) : RecurringTask
    {
        public const int Batch = 200;

        public const int MaxPerTick = 2000;

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var days = options.Value.Runs.KeepDays;
            if (days <= 0)
            {
                return;
            }

            var cutoff = clock.UtcNow.AddDays(-days);
            long deleted = 0;
            while (deleted < MaxPerTick)
            {
                var old = await instances.SummarizeManyAsync(
                    new PipelineInstanceFilter { PipelineStatus = PipelineStatus.Finished, BeforeLastUpdated = cutoff },
                    PageArgs.FromRange(0, Batch),
                    new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending), cancellationToken).ConfigureAwait(false);
                var ids = old.Items.Select(run => run.Id).ToList();
                if (ids.Count == 0)
                {
                    break;
                }

                deleted += await manager.BulkDeleteAsync(new PipelineInstanceFilter { Ids = ids }, cancellationToken).ConfigureAwait(false);
                // What the runs saved about themselves goes with them.
                await PipelineRunDataStore.DeleteAsync(keyValues, tenantAccessor.TenantId, ids, cancellationToken).ConfigureAwait(false);
                if (ids.Count < Batch)
                {
                    break;
                }
            }

            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Count} finished pipeline run(s) older than {Days} day(s) on tenant {Tenant}.", deleted, days, tenantAccessor.TenantId);
            }
        }
    }
}
