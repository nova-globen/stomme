#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a Data table step does with its table's rows.</summary>
    public enum DataTableOperation
    {
        /// <summary>The rows that meet the condition (every row with none), oldest first.</summary>
        Get,

        /// <summary>A new row per record.</summary>
        Insert,

        /// <summary>The values set on every row that meets the condition.</summary>
        Update,

        /// <summary>Per record: the rows agreeing with it on the match columns updated, or a new row when none does.</summary>
        Upsert,

        /// <summary>Every row that meets the condition deleted.</summary>
        Delete,
    }

    /// <summary>
    /// Data table (n8n's Data table node): reads and writes one of the site's data tables
    /// (<see cref="PipelineSiteTables"/>, kept by <see cref="PipelineSiteTableStore"/>), so a run remembers what an earlier one did.
    /// The table is named (or given by id); a record's fields that are not its columns are passed over, and a value its column
    /// cannot hold fails the step naming both. Get, Update and Delete take the Filter step's rules, tested on each row by the one
    /// evaluator - <c>{{ column }}</c> reads the row's column, any other name a built-in or the pipeline's variable - and Update and
    /// Delete refuse to run without a rule, so an empty condition never changes every row. The result is the rows read or written
    /// (each with its id, createdAt and updatedAt); Count says how many.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Keep records between runs in one of the site's data tables - get, insert, update, upsert or delete rows.", DisplayName = "Data table")]
    public class DataTable : DataStep
    {
        [Input(UIHint = PipelineInputHints.SiteTablePicker, Description = "The data table, by name - one of the site's (Pipelines > Data tables).", Order = 1)]
        public Input<string> Table { get; set; } = null!;

        [Input(Description = "Get | Insert | Update | Upsert | Delete.", DefaultValue = DataTableOperation.Get, Order = 2)]
        public Input<DataTableOperation> Operation { get; set; } = new(new Literal<DataTableOperation>(DataTableOperation.Get));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Insert and Upsert: the records to write - fields that are not the table's columns are passed over. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Records { get; set; }

        [Input(UIHint = PipelineInputHints.RecordCondition, Description = "Get, Update and Delete: the rows it applies to - {{ column }} reads the row's column. Get with no rule reads every row; Update and Delete need one.", Order = 4)]
        public Input<string?>? Condition { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Update: the values to set, one 'column: value' per line or a JSON object.", Order = 5)]
        public Input<string?>? Values { get; set; }

        [Input(DisplayName = "Match columns", UIHint = InputUIHints.MultiLine, Description = "Upsert: the columns a row must agree on with a record to be updated - one per line or separated by commas. A record no row agrees with is a new row.", Order = 6)]
        public Input<string?>? MatchColumns { get; set; }

        [Input(DisplayName = "Max rows", Description = "Get: at most so many rows. 0: every one.", DefaultValue = 0, Order = 7)]
        public Input<int> MaxRows { get; set; } = new(0);

        [Input(DisplayName = "Create the table if missing", Description = "Insert and Upsert: make the table when the site has none of that name - a column per field of the records, each holding what all its values are (numbers, true or false, dates, else text).", DefaultValue = false, Order = 8)]
        public Input<bool> CreateIfMissing { get; set; } = new(false);

        [Output(Description = "How many rows it read, inserted, updated, upserted or deleted.")]
        public Output<int>? Count { get; set; }

        [Output(Description = "Insert and Upsert: only the rows it made new, as a JSON array - with Upsert, the records no row agreed with, so a pipeline goes on with only what it has not seen before.")]
        public Output<string>? Inserted { get; set; }

        [Output(DisplayName = "Inserted count")]
        public Output<int>? InsertedCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var keyValues = context.GetService<IKeyValueStore>()
                ?? throw new FaultException("DATA_TABLES_UNAVAILABLE", "Pipelines", "Configuration", "The engine's key-value store is not composed in this host.");
            var tenantId = context.GetService<ITenantAccessor>()?.TenantId;
            var name = Required(context, Table, "Table");

            StoredSiteTable? table;
            try
            {
                table = await PipelineSiteTableStore.FindAsync(keyValues, tenantId, name, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await FailAsync(context, "TABLE_AMBIGUOUS", ex.Message).ConfigureAwait(false);
                return;
            }

            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            var operation = context.Get(Operation);
            if (table is null && operation is DataTableOperation.Insert or DataTableOperation.Upsert && context.Get(CreateIfMissing)
                && Steps.Records.TryParse(TextOrLastResult(context, Records), out var sample, out _) && sample.Count > 0)
            {
                try
                {
                    table = await PipelineSiteTableStore.CreateAsync(keyValues, tenantId, new PipelineSiteTableRequest { Name = name.Trim(), Columns = InferColumns(sample) },
                        context.GetRequiredService<IIdentityGenerator>().GenerateId(), now, context.CancellationToken).ConfigureAwait(false);
                    context.AddExecutionLogEntry("Created", $"Made the data table '{table.Name}': " + string.Join(", ", table.Columns.Select(column => column.Name + " (" + PipelineSiteTables.Label(column.Type) + ")")) + ".");
                }
                catch (ArgumentException ex)
                {
                    await FailAsync(context, "TABLE_NOT_CREATED", $"The data table '{name}' could not be made: {ex.Message}").ConfigureAwait(false);
                    return;
                }
            }

            if (table is null)
            {
                await FailAsync(context, "TABLE_NOT_FOUND", $"The site has no data table '{name}' - make it on the pipelines list, under Data tables.").ConfigureAwait(false);
                return;
            }

            var rows = await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenantId, table.Id, context.CancellationToken).ConfigureAwait(false);
            var outcome = operation switch
            {
                DataTableOperation.Insert => await InsertAsync(context, keyValues, tenantId, table, rows, now).ConfigureAwait(false),
                DataTableOperation.Upsert => await UpsertAsync(context, keyValues, tenantId, table, rows, now).ConfigureAwait(false),
                _ => await ByConditionAsync(context, keyValues, tenantId, table, rows, operation, now).ConfigureAwait(false),
            };

            if (outcome.Error is { } failure)
            {
                await FailAsync(context, failure.Code, failure.Message).ConfigureAwait(false);
                return;
            }

            var written = outcome.Rows.Select(row => PipelineSiteTableStore.Project(table, row)).ToList();
            var made = (outcome.New ?? new List<JsonObject>()).Select(row => PipelineSiteTableStore.Project(table, row)).ToList();
            context.Set(Count, written.Count);
            context.Set(Inserted, Steps.Records.ToJson(made));
            context.Set(InsertedCount, made.Count);
            context.AddExecutionLogEntry(operation.ToString(), outcome.Log);
            await SucceedAsync(context, Steps.Records.ToJson(written)).ConfigureAwait(false);
        }

        private async Task<Outcome> InsertAsync(StepExecutionContext context, IKeyValueStore keyValues, string? tenantId, StoredSiteTable table, List<JsonObject> rows, DateTimeOffset now)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var records, out var parseError))
            {
                return Outcome.Failed("RECORDS_INVALID", parseError!);
            }

            if (rows.Count + records.Count > PipelineSiteTables.MaxRows)
            {
                return Outcome.Failed("TABLE_FULL", $"'{table.Name}' holds {rows.Count} rows; {records.Count} more would pass the {PipelineSiteTables.MaxRows} a table may hold.");
            }

            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            var inserted = new List<JsonObject>();
            for (var index = 0; index < records.Count; index++)
            {
                var row = PipelineSiteTableStore.NewRow(PipelineSiteTableStore.NewRowId(now, index), now);
                if (!PipelineSiteTableStore.TrySet(table, row, records[index], now, out var error, ignored))
                {
                    return Outcome.Failed("VALUE_INVALID", $"Record {index + 1}, {error}", inserted);
                }

                inserted.Add(row);
            }

            // Checked first, written after: a value that does not fit leaves the table as it was.
            foreach (var row in inserted)
            {
                await PipelineSiteTableStore.SaveRowAsync(keyValues, tenantId, table, row, context.CancellationToken).ConfigureAwait(false);
            }

            return new Outcome(inserted, $"Inserted {inserted.Count} row(s) into '{table.Name}'." + Ignored(ignored), New: inserted);
        }

        private async Task<Outcome> UpsertAsync(StepExecutionContext context, IKeyValueStore keyValues, string? tenantId, StoredSiteTable table, List<JsonObject> rows, DateTimeOffset now)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var records, out var parseError))
            {
                return Outcome.Failed("RECORDS_INVALID", parseError!);
            }

            var match = new List<PipelineTableColumn>();
            foreach (var field in RecordValues.Fields(MatchColumns?.GetOrDefault(context)))
            {
                if (table.Column(field) is not { } column)
                {
                    return Outcome.Failed("MATCH_INVALID", $"'{table.Name}' has no column '{field}' to match on.");
                }

                match.Add(column);
            }

            if (match.Count == 0)
            {
                return Outcome.Failed("MATCH_INVALID", "An Upsert needs its match columns: the columns a row must agree on with a record to be updated.");
            }

            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            var written = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var made = new List<JsonObject>();
            var (updated, inserted) = (0, 0);
            for (var index = 0; index < records.Count; index++)
            {
                var keys = new List<(string Column, string Text)>();
                foreach (var column in match)
                {
                    var given = records[index].FirstOrDefault(property => string.Equals(property.Key, column.Name, StringComparison.OrdinalIgnoreCase)).Value;
                    if (!PipelineSiteTables.TryConvert(given, column.Type, out var converted, out var why))
                    {
                        return Outcome.Failed("VALUE_INVALID", $"Record {index + 1}, {column.Name}: {why}.");
                    }

                    if (converted is null)
                    {
                        return Outcome.Failed("VALUE_INVALID", $"Record {index + 1} has no {column.Name} to match on.");
                    }

                    keys.Add((column.Name, PipelineSiteTables.Text(converted)));
                }

                var agreeing = rows.Where(row => keys.All(key => PipelineSiteTables.Text(row[key.Column]) == key.Text)).ToList();
                if (agreeing.Count == 0)
                {
                    if (rows.Count + 1 > PipelineSiteTables.MaxRows)
                    {
                        return Outcome.Failed("TABLE_FULL", $"'{table.Name}' holds {PipelineSiteTables.MaxRows} rows, as many as a table may.");
                    }

                    var row = PipelineSiteTableStore.NewRow(PipelineSiteTableStore.NewRowId(now, inserted), now);
                    rows.Add(row); // a later record with the same keys updates it
                    agreeing.Add(row);
                    made.Add(row);
                    inserted++;
                }
                else
                {
                    updated += agreeing.Count(row => !written.ContainsKey(PipelineSiteTableStore.IdOf(row)));
                }

                foreach (var row in agreeing)
                {
                    if (!PipelineSiteTableStore.TrySet(table, row, records[index], now, out var error, ignored))
                    {
                        return Outcome.Failed("VALUE_INVALID", $"Record {index + 1}, {error}");
                    }

                    written[PipelineSiteTableStore.IdOf(row)] = row;
                }
            }

            foreach (var row in written.Values)
            {
                await PipelineSiteTableStore.SaveRowAsync(keyValues, tenantId, table, row, context.CancellationToken).ConfigureAwait(false);
            }

            return new Outcome(written.Values.ToList(), $"Upserted {records.Count} record(s) into '{table.Name}': {inserted} new row(s), {updated} updated." + Ignored(ignored), New: made);
        }

        private async Task<Outcome> ByConditionAsync(StepExecutionContext context, IKeyValueStore keyValues, string? tenantId, StoredSiteTable table, List<JsonObject> rows, DataTableOperation operation, DateTimeOffset now)
        {
            if (!PipelineConditions.TryRead(Condition?.GetOrDefault(context), out var condition))
            {
                return Outcome.Failed("CONDITION_INVALID", "The condition is not one the editor wrote: set it again in the step's inspector.");
            }

            if (condition.Rules.Count > 0 && PipelineConditions.Incomplete(condition) is { } incomplete)
            {
                return Outcome.Failed("CONDITION_INVALID", "The condition " + incomplete);
            }

            if (condition.Rules.Count == 0 && operation != DataTableOperation.Get)
            {
                return Outcome.Failed("CONDITION_REQUIRED", $"{(operation == DataTableOperation.Update ? "An Update" : "A Delete")} needs a condition - the rows it applies to - so it never changes every row of '{table.Name}' by mistake.");
            }

            Dictionary<string, JsonNode?>? values = null;
            if (operation == DataTableOperation.Update)
            {
                values = PipelineSiteTables.ParseValues(Values?.GetOrDefault(context), out var valuesError);
                if (values is null || values.Count == 0)
                {
                    return Outcome.Failed("VALUES_INVALID", valuesError ?? "An Update needs its values: one 'column: value' per line, or a JSON object.");
                }
            }

            var variables = await Expressions.BuiltInValues.LookupAsync(context.ExpressionExecutionContext).ConfigureAwait(false);
            var meeting = new List<JsonObject>();
            try
            {
                foreach (var row in rows)
                {
                    if (condition.Rules.Count == 0 || PipelineConditions.HoldsFor(condition, JsonSerializer.SerializeToElement(PipelineSiteTableStore.Project(table, row)), variables))
                    {
                        meeting.Add(row);
                    }
                }
            }
            catch (PipelineTemplateException ex)
            {
                return Outcome.Failed("CONDITION_INVALID", ex.Message);
            }

            switch (operation)
            {
                case DataTableOperation.Update:
                    foreach (var row in meeting)
                    {
                        if (!PipelineSiteTableStore.TrySet(table, row, values!, now, out var error))
                        {
                            return Outcome.Failed("VALUE_INVALID", error!);
                        }
                    }

                    foreach (var row in meeting)
                    {
                        await PipelineSiteTableStore.SaveRowAsync(keyValues, tenantId, table, row, context.CancellationToken).ConfigureAwait(false);
                    }

                    return new Outcome(meeting, $"Updated {meeting.Count} of {rows.Count} row(s) in '{table.Name}'.");
                case DataTableOperation.Delete:
                    foreach (var row in meeting)
                    {
                        await PipelineSiteTableStore.DeleteRowAsync(keyValues, tenantId, table.Id, PipelineSiteTableStore.IdOf(row), context.CancellationToken).ConfigureAwait(false);
                    }

                    return new Outcome(meeting, $"Deleted {meeting.Count} of {rows.Count} row(s) from '{table.Name}'.");
                default:
                    var max = context.Get(MaxRows);
                    var found = max > 0 ? meeting.Take(max).ToList() : meeting;
                    return new Outcome(found, $"Found {found.Count} of {rows.Count} row(s) in '{table.Name}'.");
            }
        }

        /// <summary>
        /// The columns a table made from records takes: one per field a column may be named (at most
        /// <see cref="PipelineSiteTables.MaxColumns"/>, in the order the fields first appear), each holding numbers when every value
        /// of it is one, else true or false, else dates, else text - a list or a record as its JSON.
        /// </summary>
        public static List<PipelineTableColumn> InferColumns(IReadOnlyList<JsonObject> records)
        {
            var columns = new List<PipelineTableColumn>();
            foreach (var field in records.SelectMany(record => record.Select(property => property.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!PipelineSiteTables.IsColumnName(field) || columns.Count == PipelineSiteTables.MaxColumns)
                {
                    continue;
                }

                var values = records.Select(record => record.FirstOrDefault(property => string.Equals(property.Key, field, StringComparison.OrdinalIgnoreCase)).Value)
                    .Where(value => value is not null && PipelineSiteTables.Text(value).Length > 0).ToList();
                var type = values.Count == 0 ? PipelineTableColumnType.Text
                    : new[] { PipelineTableColumnType.Number, PipelineTableColumnType.Boolean, PipelineTableColumnType.Date }
                        .FirstOrDefault(candidate => values.All(value => value is JsonValue && PipelineSiteTables.TryConvert(value, candidate, out _, out _)), PipelineTableColumnType.Text);
                columns.Add(new PipelineTableColumn { Name = field, Type = type });
            }

            return columns;
        }

        private static string Ignored(IReadOnlyCollection<string> ignored) =>
            ignored.Count == 0 ? "" : " Not columns, so not kept: " + string.Join(", ", ignored) + ".";

        private sealed record Outcome(List<JsonObject> Rows, string Log, (string Code, string Message)? Error = null, List<JsonObject>? New = null)
        {
            public static Outcome Failed(string code, string message, List<JsonObject>? rows = null) => new(rows ?? new List<JsonObject>(), "", (code, message));
        }
    }
}
