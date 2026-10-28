#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Compare records (BL-229, n8n's Compare Datasets): two record sets matched on key fields (<c>field</c>, or
    /// <c>field = other field</c>, a line each, as Merge records takes them) and sorted into four outputs - the records only in
    /// the first set, only in the other, in both and the same, in both and different - with, for the different, each field
    /// that differs and its two values. The fields compared are every field either side has, or those named; a key that
    /// repeats on a side pairs its records in order, and the extra ones count as only on their side. The result is the four
    /// counts; the outputs are records, for a variable each.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Compare two record sets on key fields: only in the first, only in the other, the same, and different - with what differs.", DisplayName = "Compare records")]
    public class CompareRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The first records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Other records", UIHint = InputUIHints.MultiLine, Description = "The records to compare them with - a variable another step saved, usually.", Order = 2)]
        public Input<string> OtherRecords { get; set; } = null!;

        [Input(DisplayName = "Match fields", UIHint = InputUIHints.MultiLine, Description = "The key, one per line: field, or field = other field.", Order = 3)]
        public Input<string> MatchFields { get; set; } = null!;

        [Input(DisplayName = "Compare fields", UIHint = InputUIHints.MultiLine, Description = "The fields that must agree, one per line or separated by commas. Empty: every field either side has.", Order = 4)]
        public Input<string?>? CompareFields { get; set; }

        [Input(DisplayName = "Ignore case", Description = "Compare text without regard to case.", DefaultValue = false, Order = 5)]
        public Input<bool> IgnoreCase { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Only in first", Description = "The first set's records the other set has no match for.")]
        public Output<string>? OnlyInFirst { get; set; }

        [Output(DisplayName = "Only in other", Description = "The other set's records the first set has no match for.")]
        public Output<string>? OnlyInOther { get; set; }

        [Output(Description = "The first set's records whose match agrees on every compared field.")]
        public Output<string>? Same { get; set; }

        [Output(Description = "The pairs that differ: each { key, first, other, fields: [{ field, first, other }] }.")]
        public Output<string>? Different { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var first, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            if (!Steps.Records.TryParse(OtherRecords?.GetOrDefault(context), out var other, out var otherError))
            {
                await FailAsync(context, "RECORDS_INVALID", "Other records: " + otherError).ConfigureAwait(false);
                return;
            }

            var pairs = Steps.Records.Lines(MatchFields?.GetOrDefault(context)).Select(line =>
            {
                var cut = line.IndexOf('=');
                return cut < 0 ? (First: line.Trim(), Other: line.Trim()) : (First: line[..cut].Trim(), Other: line[(cut + 1)..].Trim());
            }).Where(pair => pair.First.Length > 0 && pair.Other.Length > 0).ToList();
            if (pairs.Count == 0)
            {
                await FailAsync(context, "COMPARE_INVALID", "Name the key in Match fields: field, or field = other field.").ConfigureAwait(false);
                return;
            }

            var ignoreCase = IgnoreCase.GetOrDefault(context);
            var named = RecordValues.Fields(CompareFields?.GetOrDefault(context));
            var keyOfOther = pairs.ToDictionary(pair => pair.First, pair => pair.Other, StringComparer.Ordinal);

            // The other side's records by key, in order, so a repeated key pairs its records one to one.
            var waiting = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (var index = 0; index < other.Count; index++)
            {
                if (Key(other[index], pairs.Select(pair => pair.Other).ToList(), ignoreCase) is { } key)
                {
                    if (!waiting.TryGetValue(key, out var queue))
                    {
                        waiting[key] = queue = new Queue<int>();
                    }

                    queue.Enqueue(index);
                }
            }

            var onlyFirst = new List<JsonObject>();
            var same = new List<JsonObject>();
            var different = new List<JsonObject>();
            var matched = new HashSet<int>();
            foreach (var record in first)
            {
                var key = Key(record, pairs.Select(pair => pair.First).ToList(), ignoreCase);
                if (key is null || !waiting.TryGetValue(key, out var queue) || queue.Count == 0)
                {
                    onlyFirst.Add(record);
                    continue;
                }

                var match = queue.Dequeue();
                matched.Add(match);
                var differences = Differences(record, other[match], named, keyOfOther, ignoreCase);
                if (differences.Count == 0)
                {
                    same.Add(record);
                }
                else
                {
                    different.Add(new JsonObject
                    {
                        ["key"] = string.Join(" / ", pairs.Select(pair => Steps.Records.Text(Steps.Records.ReadPath(record, pair.First)))),
                        ["first"] = record.DeepClone(),
                        ["other"] = other[match].DeepClone(),
                        ["fields"] = new JsonArray(differences.Select(difference => (JsonNode)difference).ToArray()),
                    });
                }
            }

            var onlyOther = other.Where((_, index) => !matched.Contains(index)).ToList();
            context.Set(OnlyInFirst, Steps.Records.ToJson(onlyFirst));
            context.Set(OnlyInOther, Steps.Records.ToJson(onlyOther));
            context.Set(Same, Steps.Records.ToJson(same));
            context.Set(Different, Steps.Records.ToJson(different));
            context.AddExecutionLogEntry("Compared", $"{same.Count} the same, {different.Count} different, {onlyFirst.Count} only in the first, {onlyOther.Count} only in the other.");
            var counts = new JsonObject
            {
                ["same"] = same.Count,
                ["different"] = different.Count,
                ["onlyInFirst"] = onlyFirst.Count,
                ["onlyInOther"] = onlyOther.Count,
            };
            await SucceedAsync(context, counts.ToJsonString()).ConfigureAwait(false);
        }

        // Null when a key field is absent or empty: such a record matches nothing.
        private static string? Key(JsonObject record, IReadOnlyList<string> fields, bool ignoreCase)
        {
            var parts = new List<string>(fields.Count);
            foreach (var field in fields)
            {
                var node = Steps.Records.ReadPath(record, field);
                if (RecordValues.IsEmpty(node))
                {
                    return null;
                }

                parts.Add(Steps.Records.Text(node)!);
            }

            var key = string.Join('\u001f', parts);
            return ignoreCase ? key.ToUpperInvariant() : key;
        }

        // Each compared field whose values differ - as text, so 100 and "100" agree, and absent equals empty. A key field is
        // compared under its own name on each side.
        private static List<JsonObject> Differences(JsonObject first, JsonObject other, IReadOnlyList<string> named, IReadOnlyDictionary<string, string> keyOfOther, bool ignoreCase)
        {
            var fields = named.Count > 0
                ? named
                : first.Select(property => property.Key).Concat(other.Select(property => property.Key).Where(field => !keyOfOther.Values.Contains(field) || keyOfOther.ContainsKey(field)))
                    .Distinct(StringComparer.Ordinal).ToList();
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var differences = new List<JsonObject>();
            foreach (var field in fields)
            {
                var otherField = keyOfOther.TryGetValue(field, out var renamed) ? renamed : field;
                var left = Steps.Records.ReadPath(first, field);
                var right = Steps.Records.ReadPath(other, otherField);
                if (!string.Equals(Steps.Records.Text(left) ?? string.Empty, Steps.Records.Text(right) ?? string.Empty, comparison))
                {
                    differences.Add(new JsonObject
                    {
                        ["field"] = field,
                        ["first"] = left?.DeepClone(),
                        ["other"] = right?.DeepClone(),
                    });
                }
            }

            return differences;
        }
    }
}
