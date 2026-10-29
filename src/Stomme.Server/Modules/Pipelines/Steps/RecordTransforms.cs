#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>Which records a limit keeps.</summary>
    public enum LimitKeep
    {
        First,
        Last,
    }

    /// <summary>How two record sets are merged (n8n's Merge modes).</summary>
    public enum RecordMergeMode
    {
        /// <summary>The first records, then the other records.</summary>
        Append,

        /// <summary>Only the records that match, each pair combined.</summary>
        KeepMatches,

        /// <summary>Every first record, combined with its matches when it has any.</summary>
        KeepAllFirst,

        /// <summary>Every record of both, matches combined.</summary>
        KeepEverything,

        /// <summary>Only the first records that match nothing.</summary>
        KeepFirstWithoutMatch,
    }

    /// <summary>
    /// What the record transformations share (n8n's Sort, Remove Duplicates, Summarize): a list of field paths, and
    /// a column's values compared by what they all hold - as numbers, as dates (ISO 8601, as the conditions read them), or
    /// as text.
    /// </summary>
    internal static class RecordValues
    {
        /// <summary>Field paths, one per line or separated by commas.</summary>
        public static List<string> Fields(string? text) =>
            Records.Lines(text).SelectMany(line => line.Split(',')).Select(field => field.Trim()).Where(field => field.Length > 0).ToList();

        public static bool IsEmpty(JsonNode? node) => node switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            JsonValue value => value.TryGetValue<string>(out var text) && text.Length == 0,
            _ => false,
        };

        public static bool TryNumber(string? text, out decimal number) =>
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

        // A date as the conditions read one (ISO 8601), so "3" stays a number and "May" stays text.
        public static bool TryMoment(string? text, out DateTimeOffset moment)
        {
            moment = default;
            return text is not null && PipelineConditions.IsDate(text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out moment);
        }

        /// <summary>How a column's values compare: as numbers when every one is, as moments when every one is, else as text.</summary>
        public static Comparison<string> ColumnComparison(IEnumerable<string> values, bool ignoreCase)
        {
            var present = values.ToList();
            if (present.All(value => TryNumber(value, out _)))
            {
                return (left, right) => decimal.Parse(left, NumberStyles.Float, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            if (present.All(value => TryMoment(value, out _)))
            {
                return (left, right) => DateTimeOffset.Parse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).CompareTo(DateTimeOffset.Parse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
            }

            var comparison = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return (left, right) => comparison.Compare(left, right);
        }

        /// <summary>A key over some of a record's fields (an absent field is not an empty one), or over the whole record.</summary>
        public static string Key(JsonObject row, IReadOnlyList<string> fields, bool ignoreCase)
        {
            var key = fields.Count == 0
                ? Canonical(row)
                : string.Join('\u001f', fields.Select(field => Records.Text(Records.ReadPath(row, field)) is { } text ? "v" + text : "\0"));
            return ignoreCase ? key.ToUpperInvariant() : key;
        }

        // A record as JSON with its fields in name order, so two records holding the same fields are one key.
        private static string Canonical(JsonObject row) =>
            new JsonObject(row.OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => KeyValuePair.Create(property.Key, property.Value?.DeepClone()))).ToJsonString();
    }

    /// <summary>
    /// Filter records (n8n's Filter): keeps the records that meet a condition - the editor's rules, as an If's are,
    /// tested on each record by the one evaluator (<see cref="PipelineConditions.HoldsFor"/>): <c>{{ status }}</c> reads the
    /// record's field, a name the record lacks reads a built-in (<c>{{ $today }}</c>, <see cref="PipelineBuiltIns"/>) or the
    /// pipeline's variable of that name, and anything else is empty. The
    /// records kept are the result; the rest are the Discarded output. No rule keeps every record.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Keep the records that meet a condition; the rest are the Discarded output.", DisplayName = "Filter records")]
    public class FilterRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(UIHint = PipelineInputHints.RecordCondition, Description = "Rules a record must meet - all of them, or any; {{ field }} reads the record's field.", Order = 2)]
        public Input<string?>? Condition { get; set; }

        [Output(Description = "The records that did not meet the condition, as a JSON array.")]
        public Output<string>? Discarded { get; set; }

        [Output(DisplayName = "Kept count")]
        public Output<int>? KeptCount { get; set; }

        [Output(DisplayName = "Discarded count")]
        public Output<int>? DiscardedCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            if (!PipelineConditions.TryRead(Condition?.GetOrDefault(context), out var condition))
            {
                await FailAsync(context, "CONDITION_INVALID", "The condition is not one the editor wrote: set it again in the step's inspector.").ConfigureAwait(false);
                return;
            }

            if (condition.Rules.Count > 0 && PipelineConditions.Incomplete(condition) is { } incomplete)
            {
                await FailAsync(context, "CONDITION_INVALID", "The condition " + incomplete).ConfigureAwait(false);
                return;
            }

            var variables = await Expressions.BuiltInValues.LookupAsync(context.ExpressionExecutionContext).ConfigureAwait(false);

            var kept = new List<JsonObject>();
            var discarded = new List<JsonObject>();
            try
            {
                foreach (var row in rows)
                {
                    var meets = condition.Rules.Count == 0 || PipelineConditions.HoldsFor(condition, JsonSerializer.SerializeToElement(row), variables);
                    (meets ? kept : discarded).Add(row);
                }
            }
            catch (PipelineTemplateException ex)
            {
                await FailAsync(context, "CONDITION_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            context.Set(Discarded, Steps.Records.ToJson(discarded));
            context.Set(KeptCount, kept.Count);
            context.Set(DiscardedCount, discarded.Count);
            context.AddExecutionLogEntry("Filtered", $"Kept {kept.Count} of {rows.Count} record(s).");
            await SucceedAsync(context, Steps.Records.ToJson(kept)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sort records (n8n's Sort): by one field or several, each ascending or descending (<c>field desc</c>). A column
    /// sorts as numbers when every value in it is a number, as dates when every one is a date, otherwise as text; a record
    /// without the field goes last either way, and records that tie keep their order.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Sort records by one field or several, ascending or descending.", DisplayName = "Sort records")]
    public class SortRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Sort by", UIHint = InputUIHints.MultiLine, Description = "One per line: field, or field desc (field asc is the default).", Order = 2)]
        public Input<string?>? SortBy { get; set; }

        [Input(DisplayName = "Ignore case", Description = "Sort text without regard to case.", DefaultValue = false, Order = 3)]
        public Input<bool> IgnoreCase { get; set; } = new(new Literal<bool>(false));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var keys = new List<(string Field, bool Descending)>();
            foreach (var line in Steps.Records.Lines(SortBy?.GetOrDefault(context)))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var direction = parts.Length > 1 ? parts[^1] : "asc";
                var descending = direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
                var named = descending || direction.Equals("asc", StringComparison.OrdinalIgnoreCase);
                keys.Add((named && parts.Length > 1 ? string.Join(' ', parts[..^1]) : line, descending));
            }

            if (keys.Count == 0)
            {
                await FailAsync(context, "SORT_INVALID", "Name a field to sort by.").ConfigureAwait(false);
                return;
            }

            var ignoreCase = IgnoreCase.GetOrDefault(context);
            var columns = keys.Select(key =>
            {
                var values = rows.Select(row => Steps.Records.Text(Steps.Records.ReadPath(row, key.Field))).ToList();
                return (Values: values, key.Descending, Compare: RecordValues.ColumnComparison(values.Where(v => !string.IsNullOrEmpty(v)).Cast<string>(), ignoreCase));
            }).ToList();

            var order = Enumerable.Range(0, rows.Count).ToList();
            order.Sort((a, b) =>
            {
                foreach (var column in columns)
                {
                    var left = column.Values[a];
                    var right = column.Values[b];
                    var leftEmpty = string.IsNullOrEmpty(left);
                    var rightEmpty = string.IsNullOrEmpty(right);
                    if (leftEmpty || rightEmpty)
                    {
                        if (leftEmpty != rightEmpty)
                        {
                            return leftEmpty ? 1 : -1; // the empty last, ascending or descending
                        }

                        continue;
                    }

                    var compared = column.Compare(left!, right!);
                    if (compared != 0)
                    {
                        return column.Descending ? -compared : compared;
                    }
                }

                return a.CompareTo(b);
            });

            await SucceedAsync(context, Steps.Records.ToJson(order.Select(index => rows[index]))).ConfigureAwait(false);
        }
    }

    /// <summary>Limit records (n8n's Limit): the first (or last) so many records.</summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Keep the first or last so many records.", DisplayName = "Limit records")]
    public class LimitRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Max records", Description = "How many records to keep.", DefaultValue = 10, Order = 2)]
        public Input<int> MaxRecords { get; set; } = new(new Literal<int>(10));

        [Input(Description = "First: the records at the start. Last: those at the end.", DefaultValue = LimitKeep.First, Order = 3)]
        public Input<LimitKeep> Keep { get; set; } = new(new Literal<LimitKeep>(LimitKeep.First));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var max = MaxRecords.GetOrDefault(context);
            if (max < 0)
            {
                await FailAsync(context, "LIMIT_INVALID", "Max records cannot be below zero.").ConfigureAwait(false);
                return;
            }

            var kept = Keep.GetOrDefault(context) == LimitKeep.Last ? rows.Skip(Math.Max(0, rows.Count - max)) : rows.Take(max);
            await SucceedAsync(context, Steps.Records.ToJson(kept)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Split into batches (n8n's Loop Over Items): the records in batches of so many - a list of lists, in order, the last
    /// one shorter when they do not divide - for a For each to take a batch at a time (a rate-limited API, a bulk insert).
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "The records in batches of so many - a list of lists, for a For each to take one batch at a time.", DisplayName = "Split into batches")]
    public class BatchRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Batch size", Description = "How many records in each batch.", DefaultValue = 10, Order = 2)]
        public Input<int> BatchSize { get; set; } = new(new Literal<int>(10));

        [Output(DisplayName = "Batch count")]
        public Output<int>? BatchCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var size = BatchSize.GetOrDefault(context);
            if (size < 1)
            {
                await FailAsync(context, "BATCH_INVALID", "Batch size: at least 1.").ConfigureAwait(false);
                return;
            }

            var batches = rows.Chunk(size).Select(batch => (JsonNode)new JsonArray(batch.Select(row => (JsonNode)row.DeepClone()).ToArray())).ToArray();
            context.Set(BatchCount, batches.Length);
            context.AddExecutionLogEntry("Batched", $"{rows.Count} record(s) in {batches.Length} batch(es) of up to {size}.");
            await SucceedAsync(context, new JsonArray(batches).ToJsonString()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Remove duplicates (n8n's Remove Duplicates): keeps the first record of each set that agree on the compared
    /// fields - every field when none is named, whatever their order in the record.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Keep the first of records that agree on the compared fields (all fields when none is named).", DisplayName = "Remove duplicates")]
    public class RemoveDuplicates : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Compare fields", UIHint = InputUIHints.MultiLine, Description = "The fields two records must agree on, one per line or separated by commas. Empty: every field.", Order = 2)]
        public Input<string?>? CompareFields { get; set; }

        [Input(DisplayName = "Ignore case", Description = "Compare text without regard to case.", DefaultValue = false, Order = 3)]
        public Input<bool> IgnoreCase { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Removed count")]
        public Output<int>? RemovedCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var fields = RecordValues.Fields(CompareFields?.GetOrDefault(context));
            var ignoreCase = IgnoreCase.GetOrDefault(context);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = rows.Where(row => seen.Add(RecordValues.Key(row, fields, ignoreCase))).ToList();
            context.Set(RemovedCount, rows.Count - kept.Count);
            context.AddExecutionLogEntry("Removed duplicates", $"Kept {kept.Count} of {rows.Count} record(s).");
            await SucceedAsync(context, Steps.Records.ToJson(kept)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Summarize records (n8n's Summarize - a pivot): one record per group of records that agree on the Group by
    /// fields (one record for all when none is named), holding those fields and an aggregation per line: <c>count</c>,
    /// <c>count field</c> (its non-empty values), <c>count distinct field</c>, <c>sum field</c>, <c>average field</c>,
    /// <c>min field</c>, <c>max field</c>, <c>concatenate field</c>, <c>list field</c>, <c>list distinct field</c>, each optionally <c>as name</c> (else
    /// <c>sum_field</c> and so on). Sum and average take numbers and fail on anything else; empty values are left out of
    /// every aggregation but <c>count</c>. Groups come in the order they first appear.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Group records and count, sum, average, min, max or concatenate their fields.", DisplayName = "Summarize records")]
    public class SummarizeRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Group by", UIHint = InputUIHints.MultiLine, Description = "The fields that make a group, one per line or separated by commas. Empty: one group of every record.", Order = 2)]
        public Input<string?>? GroupBy { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: count | count field | count distinct field | list field | list distinct field | sum field | average field | min field | max field | concatenate field, each optionally 'as name'. Empty: count.", Order = 3)]
        public Input<string?>? Aggregations { get; set; }

        [Input(Description = "What concatenate puts between values.", DefaultValue = ", ", Order = 4)]
        public Input<string?>? Separator { get; set; }

        [Output(DisplayName = "Group count")]
        public Output<int>? GroupCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            List<Aggregation> aggregations;
            try
            {
                aggregations = Steps.Records.Lines(Aggregations?.GetOrDefault(context)).Select(Aggregation.Parse).ToList();
            }
            catch (FormatException ex)
            {
                await FailAsync(context, "SUMMARY_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            if (aggregations.Count == 0)
            {
                aggregations.Add(Aggregation.Parse("count"));
            }

            var groupBy = RecordValues.Fields(GroupBy?.GetOrDefault(context));
            var separator = Separator?.GetOrDefault(context) ?? ", ";
            var groups = new List<List<JsonObject>>();
            var index = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = groupBy.Count == 0 ? string.Empty : RecordValues.Key(row, groupBy, ignoreCase: false);
                if (!index.TryGetValue(key, out var group))
                {
                    index[key] = group = new List<JsonObject>();
                    groups.Add(group);
                }

                group.Add(row);
            }

            if (groupBy.Count == 0 && groups.Count == 0)
            {
                groups.Add(new List<JsonObject>()); // one summary of no records: count 0
            }

            var output = new List<JsonObject>();
            foreach (var group in groups)
            {
                var summary = new JsonObject();
                foreach (var field in groupBy)
                {
                    summary[field] = Steps.Records.ReadPath(group[0], field)?.DeepClone();
                }

                foreach (var aggregation in aggregations)
                {
                    if (!aggregation.TryApply(group, separator, out var value, out var error))
                    {
                        await FailAsync(context, "SUMMARY_NOT_A_NUMBER", error!).ConfigureAwait(false);
                        return;
                    }

                    summary[aggregation.Name] = value;
                }

                output.Add(summary);
            }

            context.Set(GroupCount, output.Count);
            await SucceedAsync(context, Steps.Records.ToJson(output)).ConfigureAwait(false);
        }

        private sealed record Aggregation(string Kind, string? Field, string Name)
        {
            private static readonly string[] Kinds = ["count distinct", "count", "list distinct", "list", "sum", "average", "min", "max", "concatenate"];

            public static Aggregation Parse(string line)
            {
                var name = (string?)null;
                var cut = line.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                if (cut > 0)
                {
                    name = line[(cut + 4)..].Trim();
                    line = line[..cut].Trim();
                    if (name.Length == 0)
                    {
                        throw new FormatException("'as' needs a name after it.");
                    }
                }

                var spelled = line.StartsWith("avg ", StringComparison.OrdinalIgnoreCase) ? "average " + line[4..]
                    : line.StartsWith("concat ", StringComparison.OrdinalIgnoreCase) ? "concatenate " + line[7..]
                    : line;
                foreach (var kind in Kinds)
                {
                    if (spelled.Equals(kind, StringComparison.OrdinalIgnoreCase) || spelled.StartsWith(kind + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        var field = spelled[kind.Length..].Trim();
                        if (field.Length == 0 && kind != "count")
                        {
                            throw new FormatException($"'{line}' needs a field: {kind} field.");
                        }

                        var named = field.Length == 0 ? null : field;
                        return new Aggregation(kind, named, name ?? (named is null ? kind : kind.Replace(' ', '_') + "_" + named));
                    }
                }

                throw new FormatException($"'{line}' is not an aggregation: count, count field, count distinct field, list, list distinct, sum, average, min, max or concatenate field.");
            }

            public bool TryApply(List<JsonObject> group, string separator, out JsonNode? value, out string? error)
            {
                value = null;
                error = null;
                if (Field is null)
                {
                    value = JsonValue.Create(group.Count);
                    return true;
                }

                var nodes = group.Select(row => Steps.Records.ReadPath(row, Field)).Where(node => !RecordValues.IsEmpty(node)).ToList();
                var texts = nodes.Select(node => Steps.Records.Text(node)!).ToList();
                switch (Kind)
                {
                    case "count":
                        value = JsonValue.Create(texts.Count);
                        return true;
                    case "count distinct":
                        value = JsonValue.Create(texts.Distinct(StringComparer.Ordinal).Count());
                        return true;
                    case "concatenate":
                        value = JsonValue.Create(string.Join(separator, texts));
                        return true;
                    case "list":
                        value = new JsonArray(nodes.Select(node => node!.DeepClone()).ToArray());
                        return true;
                    case "list distinct":
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        value = new JsonArray(nodes.Where((_, at) => seen.Add(texts[at])).Select(node => node!.DeepClone()).ToArray());
                        return true;
                    case "min":
                    case "max":
                        if (texts.Count > 0)
                        {
                            var compare = RecordValues.ColumnComparison(texts, ignoreCase: false);
                            var best = 0;
                            for (var i = 1; i < texts.Count; i++)
                            {
                                var compared = compare(texts[i], texts[best]);
                                if (Kind == "min" ? compared < 0 : compared > 0)
                                {
                                    best = i;
                                }
                            }

                            value = nodes[best]!.DeepClone();
                        }

                        return true;
                }

                var sum = 0m;
                foreach (var text in texts)
                {
                    if (!RecordValues.TryNumber(text, out var number))
                    {
                        error = $"{Kind} {Field}: '{text}' is not a number.";
                        return false;
                    }

                    sum += number;
                }

                value = Kind == "sum" ? JsonValue.Create(sum) : texts.Count == 0 ? null : JsonValue.Create(sum / texts.Count);
                return true;
            }
        }
    }

    /// <summary>
    /// Split out (n8n's Split Out): one record per element of a list field - an element that is an object gives its
    /// fields, any other its value under the field's name (or the name given) - with the record's other fields kept unless
    /// that is switched off. A record whose field is empty or absent gives none; a field holding one value gives one.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "One record per element of a list field, the record's other fields kept.", DisplayName = "Split out")]
    public class SplitOut : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(Description = "The field holding the list (a dotted path for one inside another).", Order = 2)]
        public Input<string> Field { get; set; } = null!;

        [Input(DisplayName = "Keep other fields", Description = "Keep the record's other fields on every record it gives.", DefaultValue = true, Order = 3)]
        public Input<bool> KeepOtherFields { get; set; } = new(new Literal<bool>(true));

        [Input(DisplayName = "Name", Description = "The field an element that is not an object goes in. Empty: the list field's own name.", Order = 4)]
        public Input<string?>? Destination { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var field = Required(context, Field, "Field");
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var parseError))
            {
                await FailAsync(context, "RECORDS_INVALID", parseError!).ConfigureAwait(false);
                return;
            }

            var keepOthers = KeepOtherFields.GetOrDefault(context);
            var destination = Destination?.GetOrDefault(context) is { Length: > 0 } named ? named.Trim() : field.Split('.')[^1];
            var topLevel = field.Split('.')[0];
            var output = new List<JsonObject>();
            foreach (var row in rows)
            {
                var node = Steps.Records.ReadPath(row, field);
                if (RecordValues.IsEmpty(node))
                {
                    continue;
                }

                var elements = node is JsonArray array ? array.ToList() : new List<JsonNode?> { node };
                foreach (var element in elements)
                {
                    var record = new JsonObject();
                    if (keepOthers)
                    {
                        foreach (var property in row.Where(property => property.Key != topLevel))
                        {
                            record[property.Key] = property.Value?.DeepClone();
                        }
                    }

                    if (element is JsonObject fields && Destination?.GetOrDefault(context) is not { Length: > 0 })
                    {
                        foreach (var property in fields)
                        {
                            record[property.Key] = property.Value?.DeepClone();
                        }
                    }
                    else
                    {
                        record[destination] = element?.DeepClone();
                    }

                    output.Add(record);
                }
            }

            context.AddExecutionLogEntry("Split out", $"{rows.Count} record(s) gave {output.Count}.");
            await SucceedAsync(context, Steps.Records.ToJson(output)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merge records (n8n's Merge): two record sets as one - appended, or joined on match fields (<c>field</c> when
    /// both sides call it the same, <c>field = other field</c> when not; every line must agree, and an absent value matches
    /// nothing). A combined record holds the first record's fields and the other record's fields the first lacks.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Append two record sets, or join them on matching fields.", DisplayName = "Merge records")]
    public class MergeRecords : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The first records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Other records", UIHint = InputUIHints.MultiLine, Description = "The records to merge with them - a variable another branch saved, usually.", Order = 2)]
        public Input<string> OtherRecords { get; set; } = null!;

        [Input(Description = "Append | KeepMatches | KeepAllFirst (a left join) | KeepEverything | KeepFirstWithoutMatch.", DefaultValue = RecordMergeMode.Append, Order = 3)]
        public Input<RecordMergeMode> Mode { get; set; } = new(new Literal<RecordMergeMode>(RecordMergeMode.Append));

        [Input(DisplayName = "Match fields", UIHint = InputUIHints.MultiLine, Description = "For a join, one per line: field, or field = other field.", Order = 4)]
        public Input<string?>? MatchFields { get; set; }

        [Input(DisplayName = "Ignore case", Description = "Match text without regard to case.", DefaultValue = false, Order = 5)]
        public Input<bool> IgnoreCase { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Matched count", Description = "How many first records found a match.")]
        public Output<int>? MatchedCount { get; set; }

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

            var mode = Mode.GetOrDefault(context);
            if (mode == RecordMergeMode.Append)
            {
                context.Set(MatchedCount, 0);
                await SucceedAsync(context, Steps.Records.ToJson(first.Concat(other))).ConfigureAwait(false);
                return;
            }

            var pairs = Steps.Records.Lines(MatchFields?.GetOrDefault(context)).Select(line =>
            {
                var cut = line.IndexOf('=');
                return cut < 0 ? (First: line, Other: line) : (First: line[..cut].Trim(), Other: line[(cut + 1)..].Trim());
            }).Where(pair => pair.First.Length > 0 && pair.Other.Length > 0).ToList();
            if (pairs.Count == 0)
            {
                await FailAsync(context, "MERGE_INVALID", $"{mode} joins on fields: name them in Match fields.").ConfigureAwait(false);
                return;
            }

            var ignoreCase = IgnoreCase.GetOrDefault(context);
            var otherFields = pairs.Select(pair => pair.Other).ToList();
            var firstFields = pairs.Select(pair => pair.First).ToList();
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < other.Count; i++)
            {
                if (JoinKey(other[i], otherFields, ignoreCase) is { } key)
                {
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        byKey[key] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }

            var output = new List<JsonObject>();
            var matchedOther = new HashSet<int>();
            var matched = 0;
            foreach (var row in first)
            {
                var matches = JoinKey(row, firstFields, ignoreCase) is { } key && byKey.TryGetValue(key, out var found) ? found : null;
                if (matches is not null)
                {
                    matched++;
                    matchedOther.UnionWith(matches);
                    if (mode != RecordMergeMode.KeepFirstWithoutMatch)
                    {
                        output.AddRange(matches.Select(index => Combine(row, other[index])));
                    }
                }
                else if (mode is RecordMergeMode.KeepAllFirst or RecordMergeMode.KeepEverything or RecordMergeMode.KeepFirstWithoutMatch)
                {
                    output.Add(row);
                }
            }

            if (mode == RecordMergeMode.KeepEverything)
            {
                output.AddRange(other.Where((_, index) => !matchedOther.Contains(index)));
            }

            context.Set(MatchedCount, matched);
            context.AddExecutionLogEntry("Merged", $"{matched} of {first.Count} first record(s) matched; {output.Count} record(s) out.");
            await SucceedAsync(context, Steps.Records.ToJson(output)).ConfigureAwait(false);
        }

        // Null when a match field is absent or empty: such a record matches nothing.
        private static string? JoinKey(JsonObject row, IReadOnlyList<string> fields, bool ignoreCase)
        {
            var parts = new List<string>(fields.Count);
            foreach (var field in fields)
            {
                var node = Steps.Records.ReadPath(row, field);
                if (RecordValues.IsEmpty(node))
                {
                    return null;
                }

                parts.Add(Steps.Records.Text(node)!);
            }

            var key = string.Join('\u001f', parts);
            return ignoreCase ? key.ToUpperInvariant() : key;
        }

        private static JsonObject Combine(JsonObject row, JsonObject other)
        {
            var combined = (JsonObject)row.DeepClone();
            foreach (var property in other)
            {
                if (!combined.ContainsKey(property.Key))
                {
                    combined[property.Key] = property.Value?.DeepClone();
                }
            }

            return combined;
        }
    }
}
