#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Models;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// Fires due schedules (BL-95 increment (e)): a recurring task the ENGINE runs per site tenant (its tenant task
    /// manager starts every recurring task under each tenant's scope, so the trigger store, the key-value store and
    /// the stimulus sender below are the site's), on one node at a time (<see cref="SingleNodeTaskAttribute"/>, the
    /// engine's distributed lock). Each tick lists the stored <c>Stomme.Schedule</c> triggers, and for each distinct
    /// cron computes the next occurrence after its LAST FIRE (kept in the engine's key-value store under the trigger's
    /// hash; a schedule seen for the first time is anchored at that tick, so publishing one does not replay the past);
    /// when it is due, ONE stimulus starts every pipeline indexed under that cron, and the fire is recorded before the
    /// next tick can see it. A misfire (the host was down over an occurrence) fires once at the next tick, never once
    /// per missed occurrence. The last fire is kept under the SITE's key (<see cref="LastFiredKey"/>): a key-value key is
    /// one row per installation while the store reads per site, so two sites with the same cron once shared one row, took
    /// it from each other every tick, and neither ever fired (found 2026-10-07).
    /// </summary>
    [SingleNodeTask]
    public sealed class ScheduledPipelinesTask(
        ITriggerStore triggers,
        IKeyValueStore keyValues,
        IStimulusSender stimulusSender,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        IOptions<PipelineTriggerOptions> options,
        ILogger<ScheduledPipelinesTask> logger) : RecurringTask
    {
        public const string LastFiredKeyPrefix = "pipelines:schedule:last:";

        /// <summary>Where a site keeps a schedule's last fire: its tenant, then the trigger's hash.</summary>
        public static string LastFiredKey(string? tenantId, string hash) => LastFiredKeyPrefix + tenantId + ":" + hash;

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (!options.Value.Schedules.Enabled)
            {
                return;
            }

            var now = clock.UtcNow;
            var stored = await triggers.FindManyAsync(new TriggerFilter { Name = StepTypeName }, cancellationToken).ConfigureAwait(false);
            foreach (var group in stored.Where(trigger => trigger.Hash is not null).GroupBy(trigger => trigger.Hash!))
            {
                var cron = ReadCron(group.First());
                if (cron is null)
                {
                    continue;
                }

                var expression = Schedule.TryParse(cron, out var error);
                if (expression is null)
                {
                    logger.LogWarning("Schedule {Hash} on tenant {Tenant} carries an expression the task cannot read: {Error}", group.Key, tenantAccessor.TenantId, error);
                    continue;
                }

                // BL-229: the zone the cron is read in (UTC when none); the occurrence is found there, daylight saving followed.
                var zoneName = ReadTimeZone(group.First());
                var zone = Schedule.FindZone(zoneName, out var zoneError);
                if (zone is null)
                {
                    logger.LogWarning("Schedule {Hash} on tenant {Tenant} names a time zone this server does not know: {Error}", group.Key, tenantAccessor.TenantId, zoneError);
                    continue;
                }

                var key = LastFiredKey(tenantAccessor.TenantId, group.Key);
                // Before 10.18.0 the key carried no tenant: a site's fire kept there still anchors it, so the upgrade loses none.
                var last = await keyValues.FindAsync(new KeyValueFilter { Key = key }, cancellationToken).ConfigureAwait(false)
                    ?? await keyValues.FindAsync(new KeyValueFilter { Key = LastFiredKeyPrefix + group.Key }, cancellationToken).ConfigureAwait(false);
                if (last is null)
                {
                    // First sight: anchor here. The schedule fires at its next occurrence from now on.
                    await keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = now.ToString("O", CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!DateTimeOffset.TryParse(last.SerializedValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var anchor))
                {
                    anchor = now;
                }

                var next = expression.GetNextOccurrence(anchor.UtcDateTime, zone, inclusive: false);
                if (next is null || next.Value > now.UtcDateTime)
                {
                    continue;
                }

                await keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = now.ToString("O", CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
                var result = await stimulusSender.SendAsync(StepTypeName, Schedule.Stimulus(cron, zoneName), new StimulusMetadata
                {
                    Input = new Dictionary<string, object> { [Schedule.FiredAtInputKey] = now.ToString("O", CultureInfo.InvariantCulture) },
                }, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Schedule '{Cron}' on tenant {Tenant} fired {Count} pipeline(s) (due {Due:O}).", cron, tenantAccessor.TenantId, result.PipelineInstanceResponses.Count, next.Value);
            }
        }

        /// <summary>The step type name the engine indexes the trigger under (its <c>[Step]</c> namespace and class name).</summary>
        public static string StepTypeName => "Stomme.Schedule";

        /// <summary>The cron of a stored trigger's payload: typed when the payload serializer kept the type, else read from JSON.</summary>
        internal static string? ReadCron(StoredTrigger trigger) => trigger.Payload switch
        {
            ScheduleStimulus stimulus => stimulus.Cron,
            ZonedScheduleStimulus zoned => zoned.Cron,
            var payload => ReadText(payload, nameof(ScheduleStimulus.Cron)),
        };

        /// <summary>The time zone of a stored trigger's payload; null - UTC - for a UTC schedule, which has none.</summary>
        internal static string? ReadTimeZone(StoredTrigger trigger) => trigger.Payload switch
        {
            ScheduleStimulus => null,
            ZonedScheduleStimulus zoned => zoned.TimeZone,
            var payload => ReadText(payload, nameof(ZonedScheduleStimulus.TimeZone)),
        };

        private static string? ReadText(object? payload, string name)
        {
            switch (payload)
            {
                case JsonElement element when element.ValueKind == JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        {
                            return property.Value.GetString();
                        }
                    }

                    return null;
                case IDictionary<string, object> dictionary:
                    return dictionary.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
                default:
                    return null;
            }
        }
    }
}
