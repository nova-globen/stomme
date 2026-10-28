#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Modules.Pipelines.Application;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>Which records a <see cref="RemoveSeenRecords"/> keeps.</summary>
    public enum SeenKeep
    {
        /// <summary>The records whose compared fields hold values no earlier run had.</summary>
        NotSeenBefore,

        /// <summary>The records whose one compared field holds a number or a date higher than any an earlier run had.</summary>
        HigherThanBefore,
    }

    /// <summary>Whose history a <see cref="RemoveSeenRecords"/> reads and writes.</summary>
    public enum SeenScope
    {
        /// <summary>This step's own.</summary>
        ThisStep,

        /// <summary>Shared by every Remove records seen before of the pipeline that names it so.</summary>
        ThePipeline,
    }

    /// <summary>
    /// Remove records seen before (BL-229, n8n's Remove Duplicates "remove items processed in previous executions"): keeps the
    /// records no earlier run of the pipeline had - by the values of the compared fields - or, by one field, those whose number
    /// or date is higher than any before; a schedule polling a feed or an API then passes on only what is new. What was seen
    /// is kept per site and pipeline (and step, unless shared), in the engine's key-value store, as HASHES of the compared
    /// values - never the values; "higher" keeps the highest value, a number or a date. The newest <see cref="HistorySize"/>
    /// values are kept, the oldest forgotten. A step test reads what the pipeline has seen and remembers nothing. Two runs at
    /// once take turns through the engine's lock, so a record is new to one of them only.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Keep the records no earlier run had - or those higher than any before - and remember them.", DisplayName = "Remove records seen before")]
    public class RemoveSeenRecords : DataStep
    {
        public const string KeyPrefix = "pipelines:seen:";

        public const int DefaultHistory = 10_000;

        public const int MaxHistory = 100_000;

        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(Description = "NotSeenBefore: the records whose compared values no earlier run had. HigherThanBefore: those whose one compared field holds a number or a date higher than any before.", DefaultValue = SeenKeep.NotSeenBefore, Order = 2)]
        public Input<SeenKeep> Keep { get; set; } = new(new Literal<SeenKeep>(SeenKeep.NotSeenBefore));

        [Input(DisplayName = "Compare fields", UIHint = InputUIHints.MultiLine, Description = "The fields that make a record the one seen before - an id - one per line or separated by commas. Empty: every field. HigherThanBefore takes one field.", Order = 3)]
        public Input<string?>? CompareFields { get; set; }

        [Input(DisplayName = "Ignore case", Description = "Compare text without regard to case.", DefaultValue = false, Order = 4)]
        public Input<bool> IgnoreCase { get; set; } = new(new Literal<bool>(false));

        [Input(Description = "ThisStep: what this step has seen. ThePipeline: shared by every step of the pipeline set to ThePipeline.", DefaultValue = SeenScope.ThisStep, Order = 5)]
        public Input<SeenScope> Scope { get; set; } = new(new Literal<SeenScope>(SeenScope.ThisStep));

        [Input(DisplayName = "History size", Description = "The most values remembered; the oldest are forgotten first. At most 100,000.", DefaultValue = DefaultHistory, Order = 6)]
        public Input<int> HistorySize { get; set; } = new(new Literal<int>(DefaultHistory));

        [Input(DisplayName = "Forget what was seen", Description = "Forget everything seen before this run first - every record is then new, and remembered.", DefaultValue = false, Order = 7)]
        public Input<bool> Forget { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Removed count")]
        public Output<int>? RemovedCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var keep = Keep.GetOrDefault(context);
            var fields = RecordValues.Fields(CompareFields?.GetOrDefault(context));
            if (keep == SeenKeep.HigherThanBefore && fields.Count != 1)
            {
                await FailAsync(context, "SEEN_ONE_FIELD", "HigherThanBefore compares one field - name it in Compare fields.").ConfigureAwait(false);
                return;
            }

            var ignoreCase = IgnoreCase.GetOrDefault(context);
            var asked = HistorySize.GetOrDefault(context);
            var size = Math.Clamp(asked > 0 ? asked : DefaultHistory, 1, MaxHistory);
            var test = StepTestCommitGate.IsStepTest(context.PipelineExecutionContext);
            var key = Key(context.GetService<ITenantAccessor>()?.TenantId, Reliability.PipelineCallerPolicy.CallerOf(context.PipelineExecutionContext),
                Scope.GetOrDefault(context) == SeenScope.ThePipeline ? "*" : context.Step.Id);
            var store = context.GetRequiredService<IKeyValueStore>();
            var locks = context.GetRequiredService<IDistributedLockProvider>();
            var timeout = context.GetRequiredService<IOptions<DistributedLockingOptions>>().Value.LockAcquisitionTimeout;

            List<System.Text.Json.Nodes.JsonObject> kept;
            await using (await locks.AcquireLockAsync(key, timeout, context.CancellationToken).ConfigureAwait(false))
            {
                var history = Forget.GetOrDefault(context) ? new SeenHistory() : SeenHistory.Read((await store.FindAsync(new KeyValueFilter { Key = key }, context.CancellationToken).ConfigureAwait(false))?.SerializedValue);
                if (keep == SeenKeep.NotSeenBefore)
                {
                    // A record new to the history and to this run - its twin later in the same records is not new either.
                    var known = new HashSet<string>(history.Seen, StringComparer.Ordinal);
                    kept = new List<System.Text.Json.Nodes.JsonObject>();
                    foreach (var row in rows)
                    {
                        var hash = Hash(RecordValues.Key(row, fields, ignoreCase));
                        if (known.Add(hash))
                        {
                            kept.Add(row);
                            history.Seen.Add(hash);
                        }
                    }

                    history.Seen.RemoveRange(0, Math.Max(0, history.Seen.Count - size)); // the oldest forgotten first
                }
                else
                {
                    var values = new List<(System.Text.Json.Nodes.JsonObject Row, string? Text)>();
                    foreach (var row in rows)
                    {
                        values.Add((row, Steps.Records.Text(Steps.Records.ReadPath(row, fields[0]))));
                    }

                    if (!Comparable(values.Select(value => value.Text), history.Highest, out var kind, out var problem))
                    {
                        await FailAsync(context, "SEEN_NOT_COMPARABLE", problem!).ConfigureAwait(false);
                        return;
                    }

                    var highest = history.Highest;
                    kept = values.Where(value => highest is null || Compare(value.Text!, highest, kind) > 0).Select(value => value.Row).ToList();
                    foreach (var value in values)
                    {
                        if (history.Highest is null || Compare(value.Text!, history.Highest, kind) > 0)
                        {
                            history.Highest = value.Text;
                        }
                    }

                    history.Kind = kind;
                }

                if (!test)
                {
                    await store.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = history.Write() }, context.CancellationToken).ConfigureAwait(false);
                }
            }

            context.Set(RemovedCount, rows.Count - kept.Count);
            context.AddExecutionLogEntry("Removed records seen before", $"Kept {kept.Count} of {rows.Count} record(s)" + (test ? "; a test - nothing remembered." : "."));
            await SucceedAsync(context, Steps.Records.ToJson(kept)).ConfigureAwait(false);
        }

        /// <summary>Where a history is kept: per site, pipeline and step (or "*", the pipeline's own).</summary>
        public static string Key(string? tenantId, string? definitionId, string scope) => KeyPrefix + tenantId + ":" + definitionId + ":" + scope;

        /// <summary>Every history of a pipeline on a site begins with this.</summary>
        public static string PipelinePrefix(string? tenantId, string definitionId) => KeyPrefix + tenantId + ":" + definitionId + ":";

        // A value's hash: what is kept of it - a record's key is never stored.
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

        // Every value present and a number, or every one a date (ISO 8601) - with the highest kept, of the same kind.
        private static bool Comparable(IEnumerable<string?> values, string? highest, out string kind, out string? problem)
        {
            var all = values.ToList();
            problem = null;
            kind = "number";
            if (all.Any(string.IsNullOrEmpty))
            {
                problem = "A record has no value in the compared field - HigherThanBefore needs a number or a date in every record.";
                return false;
            }

            var candidates = highest is null ? all : all.Append(highest).ToList();
            if (candidates.All(value => RecordValues.TryNumber(value, out _)))
            {
                return true;
            }

            kind = "date";
            if (candidates.All(value => RecordValues.TryMoment(value, out _)))
            {
                return true;
            }

            problem = "HigherThanBefore compares numbers or dates (ISO 8601), every record's and the highest seen alike - '"
                + candidates.First(value => !RecordValues.TryNumber(value, out _) && !RecordValues.TryMoment(value, out _)) + "' is neither.";
            return false;
        }

        private static int Compare(string left, string right, string kind) => kind == "number"
            ? decimal.Parse(left, NumberStyles.Float, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right, NumberStyles.Float, CultureInfo.InvariantCulture))
            : DateTimeOffset.Parse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).CompareTo(DateTimeOffset.Parse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

        /// <summary>What a step has seen, as it is kept: the hashes oldest first, or the highest value and its kind.</summary>
        internal sealed class SeenHistory
        {
            public List<string> Seen { get; set; } = new();

            public string? Highest { get; set; }

            public string? Kind { get; set; }

            // Written by this class only; anything unreadable is nothing seen rather than a failed run.
            public static SeenHistory Read(string? json)
            {
                try
                {
                    return JsonSerializer.Deserialize<SeenHistory>(json ?? "{}") ?? new SeenHistory();
                }
                catch (JsonException)
                {
                    return new SeenHistory();
                }
            }

            public string Write() => JsonSerializer.Serialize(this);
        }
    }
}
