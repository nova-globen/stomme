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
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Enums;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// n8n's "save successful executions", per pipeline: a published pipeline whose graph says not to keep the runs that
    /// finish well (<see cref="Models.Pipelines.PipelineGraph.KeepSuccessfulRuns"/> false) has each such run deleted - its log, its
    /// steps' records and its run data with it, as the site's retention deletes - once it has been finished for
    /// <see cref="Grace"/>, so its page can still be opened just after it ends. A run that failed, was cancelled or is still going
    /// is kept. Per site tenant, on one node at a time, every <c>Pipelines:Runs:SettingsIntervalSeconds</c>.
    /// <para>
    /// And n8n Enterprise's execution data redaction: a published pipeline whose graph says its runs keep their steps' data only
    /// when they fail, or never (<see cref="PipelineStepDataKeeping"/>), has each such run's step records emptied of what the
    /// steps received and produced, and the run of what it was started with and returned (<see cref="KeepRunInput.Empty"/>),
    /// once it has been finished for <see cref="Grace"/> - the run, its status and times, its errors, its log and its saved
    /// values stay; a record, and a run started with something, says the data was not kept. The first pass takes every finished run,
    /// so the setting reaches the runs already kept; after it, only those finished since (a mark per pipeline in the site's
    /// key-value store).
    /// </para>
    /// </summary>
    [SingleNodeTask]
    public sealed class PipelineRunSettingsTask(
        IPipelineDefinitionStore definitions,
        IPipelineInstanceStore instances,
        IPipelineInstanceManager manager,
        IKeyValueStore keyValues,
        IStepExecutionStore steps,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        ILogger<PipelineRunSettingsTask> logger) : RecurringTask
    {
        public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

        public const int Batch = 200;

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var published = await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Published }, cancellationToken).ConfigureAwait(false);
            var cutoff = clock.UtcNow - Grace;
            foreach (var definition in published.Where(definition => PipelineGraphCodec.KeepsSuccessfulRuns(definition.StringData) == false))
            {
                long deleted = 0;
                while (true)
                {
                    var finished = await instances.SummarizeManyAsync(
                        new PipelineInstanceFilter { DefinitionId = definition.DefinitionId, PipelineSubStatus = PipelineSubStatus.Finished, BeforeLastUpdated = cutoff },
                        PageArgs.FromRange(0, Batch),
                        new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending), cancellationToken).ConfigureAwait(false);
                    var ids = finished.Items.Select(run => run.Id).ToList();
                    if (ids.Count == 0)
                    {
                        break;
                    }

                    deleted += await manager.BulkDeleteAsync(new PipelineInstanceFilter { Ids = ids }, cancellationToken).ConfigureAwait(false);
                    await PipelineRunDataStore.DeleteAsync(keyValues, tenantAccessor.TenantId, ids, cancellationToken).ConfigureAwait(false);
                    if (ids.Count < Batch)
                    {
                        break;
                    }
                }

                if (deleted > 0)
                {
                    logger.LogInformation("Deleted {Count} run(s) of '{Pipeline}' that finished well - it keeps only the runs that fail.", deleted, definition.Name);
                }
            }

            foreach (var definition in published)
            {
                if (PipelineGraphCodec.KeepsStepData(definition.StringData) is { } keep)
                {
                    await EmptyStepDataAsync(definition, keep, cutoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>The key-value key of a pipeline's mark: the last finish time whose runs have had their step data emptied.</summary>
        public static string StepDataMarkKey(string? tenantId, string definitionId) => "pipelines:step-data-mark:" + tenantId + ":" + definitionId;

        private async Task EmptyStepDataAsync(PipelineDefinition definition, string keep, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            var markKey = StepDataMarkKey(tenantAccessor.TenantId, definition.DefinitionId);
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = markKey }, cancellationToken).ConfigureAwait(false);
            DateTimeOffset? mark = DateTimeOffset.TryParse(stored?.SerializedValue, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            if (mark is { } at && at.TimeOfDay == TimeSpan.Zero)
            {
                mark = at.AddTicks(-1); // a time of exactly midnight is read as a whole day by the timestamp filter
            }

            var filter = new PipelineInstanceFilter
            {
                DefinitionId = definition.DefinitionId,
                BeforeLastUpdated = cutoff,
                TimestampFilters = mark is null
                    ? null
                    : new[] { new TimestampFilter { Column = nameof(PipelineInstance.UpdatedAt), Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = mark.Value } },
            };
            if (keep == PipelineStepDataKeeping.WhenFailed)
            {
                filter.PipelineSubStatus = PipelineSubStatus.Finished; // the runs that finished well; a failed or cancelled one keeps its data
            }
            else
            {
                filter.PipelineStatus = PipelineStatus.Finished; // every run that has ended, however it ended
            }

            long emptied = 0;
            DateTimeOffset? reached = mark;
            for (var offset = 0; ; offset += Batch)
            {
                var page = await instances.SummarizeManyAsync(filter, PageArgs.FromRange(offset, Batch),
                    new PipelineInstanceOrder<DateTimeOffset?>(x => x.UpdatedAt, OrderDirection.Ascending), cancellationToken).ConfigureAwait(false);
                foreach (var run in page.Items)
                {
                    foreach (var record in await steps.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = run.Id }, cancellationToken).ConfigureAwait(false))
                    {
                        if (Empty(record))
                        {
                            await steps.SaveAsync(record, cancellationToken).ConfigureAwait(false);
                            emptied++;
                        }
                    }

                    // What the run was started with and what it returned - its page shows the one, Run again reuses it. Saved as
                    // read, so its UpdatedAt - which this paging is ordered and filtered by - does not move.
                    if (await instances.FindAsync(new PipelineInstanceFilter { Id = run.Id }, cancellationToken).ConfigureAwait(false) is { } instance
                        && KeepRunInput.Empty(instance.PipelineState))
                    {
                        await instances.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
                        emptied++;
                    }

                    if (run.UpdatedAt is { } updated && (reached is null || updated > reached))
                    {
                        reached = updated;
                    }
                }

                if (page.Items.Count < Batch)
                {
                    break;
                }
            }

            // The next pass starts where this one ended - never later than the cutoff, so a run that ends meanwhile is not passed.
            var next = reached is null ? cutoff : (reached.Value < cutoff ? reached.Value : cutoff);
            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = markKey, SerializedValue = next.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            if (emptied > 0)
            {
                logger.LogInformation("Emptied the data of {Count} step record(s) and run(s) of '{Pipeline}' - it keeps {Keep}.", emptied, definition.Name,
                    keep == PipelineStepDataKeeping.WhenFailed ? "the data of a run that fails only" : "no run's data");
            }
        }

        /// <summary>A step record emptied of what the step received and produced; false when there was nothing to empty.</summary>
        public static bool Empty(StepExecutionRecord record)
        {
            if (record.StepState is null && record.Outputs is null && record.Payload is null)
            {
                return false;
            }

            record.StepState = null;
            record.Outputs = null;
            record.Payload = null;
            record.Metadata ??= new System.Collections.Generic.Dictionary<string, object>();
            record.Metadata[PipelineStepDataKeeping.NotKeptKey] = true;
            return true;
        }
    }
}
