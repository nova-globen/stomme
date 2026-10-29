#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>What a data table's column holds.</summary>
    public enum PipelineTableColumnType
    {
        Text,
        Number,
        Boolean,
        Date,
    }

    /// <summary>One column of a data table: its name, as <c>{{ name }}</c> reads it, and what it holds.</summary>
    public class PipelineTableColumn
    {
        public string Name { get; set; } = "";

        public PipelineTableColumnType Type { get; set; }

        /// <summary>In a table changed: the name this column had, when it is a kept column renamed - its values go with it. Never kept.</summary>
        public string? Was { get; set; }
    }

    /// <summary>One of the site's data tables, as the pages and the editor show it.</summary>
    public class PipelineSiteTableView
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public List<PipelineTableColumn> Columns { get; set; } = new();

        public DateTime CreatedOnUtc { get; set; }

        public DateTime UpdatedOnUtc { get; set; }
    }

    /// <summary>A table made or changed: its name and its columns.</summary>
    public class PipelineSiteTableRequest
    {
        public string Name { get; set; } = "";

        public List<PipelineTableColumn> Columns { get; set; } = new();
    }

    /// <summary>A page of a table's rows, oldest first: each row's values as text by column, with its id, createdAt and updatedAt.</summary>
    public class PipelineTableRowsView
    {
        public PipelineSiteTableView Table { get; set; } = new();

        public List<Dictionary<string, string>> Rows { get; set; } = new();

        public int Total { get; set; }

        public int Skip { get; set; }
    }

    /// <summary>A row added or changed by hand: its values as text by column; a column left out is left as it is, an empty one cleared.</summary>
    public class PipelineTableRowRequest
    {
        public Dictionary<string, string> Values { get; set; } = new();
    }

    /// <summary>A table's rows as CSV, with the file name a download takes.</summary>
    public class PipelineTableCsv
    {
        public string FileName { get; set; } = "";

        public string Csv { get; set; } = "";
    }

    /// <summary>A CSV whose rows are added to a table.</summary>
    public class PipelineTableImportRequest
    {
        public string Csv { get; set; } = "";
    }

    /// <summary>What a CSV import did: how many rows it added, and the CSV's columns that are none of the table's.</summary>
    public class PipelineTableImportResult
    {
        public int Imported { get; set; }

        public List<string> Ignored { get; set; } = new();
    }

    /// <summary>
    /// n8n's Data tables: tables a site keeps for its pipelines - rows a run inserts, finds, updates, upserts or deletes
    /// with the Data table step, and that its authors see and change on the Data tables page - so a run remembers what an earlier
    /// one did (the orders already sent, a counter, a list of subscribers). Each column holds text, a number, true or false, or a
    /// date; every row also has its <c>id</c>, <c>createdAt</c> and <c>updatedAt</c>. Not for secrets: whoever may view the site's
    /// pipelines sees them. The one rule for both sides.
    /// </summary>
    public static class PipelineSiteTables
    {
        public const int MaxTables = 20;

        public const int MaxColumns = 30;

        public const int MaxRows = 10000;

        public const int MaxNameLength = 60;

        public const int MaxValueLength = 10000;

        public const string IdColumn = "id";

        public const string CreatedColumn = "createdAt";

        public const string UpdatedColumn = "updatedAt";

        /// <summary>The columns every row has, which no table may name as its own.</summary>
        public static readonly IReadOnlyList<string> SystemColumns = new[] { IdColumn, CreatedColumn, UpdatedColumn };

        /// <summary>A table's name: some text, at most <see cref="MaxNameLength"/> characters, no control characters.</summary>
        public static bool IsTableName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);

        /// <summary>A column's name: what <c>{{ name }}</c> reads - a letter or '_', then letters, digits or '_' - and not a system column's.</summary>
        public static bool IsColumnName(string? name) =>
            PipelineSiteVariables.IsName(name) && !SystemColumns.Contains(name!, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a table may be kept with this name and these columns; the reason when not.</summary>
        public static bool Check(string? name, IReadOnlyList<PipelineTableColumn>? columns, out string? error)
        {
            error = null;
            columns ??= Array.Empty<PipelineTableColumn>();
            if (!IsTableName(name))
            {
                error = $"A table's name is some text of at most {MaxNameLength} characters.";
            }
            else if (columns.Count == 0)
            {
                error = "A table has at least one column.";
            }
            else if (columns.Count > MaxColumns)
            {
                error = $"A table has at most {MaxColumns} columns.";
            }
            else if (columns.FirstOrDefault(column => !IsColumnName(column?.Name)) is { } bad)
            {
                error = $"'{bad?.Name}' cannot be a column's name: a letter or '_', then letters, digits or '_', at most {PipelineSiteVariables.MaxNameLength} - and not id, createdAt or updatedAt.";
            }
            else if (columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
            {
                error = $"Two columns are named '{twice.Key}'.";
            }
            else if (columns.FirstOrDefault(column => !Enum.IsDefined(column.Type)) is { } unknown)
            {
                error = $"'{unknown.Name}' holds nothing a column can: text, a number, true or false, or a date.";
            }

            return error is null;
        }

        /// <summary>
        /// A value as a column of the type keeps it: text as text (a list or an object as its JSON), a number from a number or
        /// its text, true or false from a boolean or true/false/yes/no/1/0, a date from an ISO 8601 date (kept in UTC). Empty is
        /// null. False, with the reason, for a value the column cannot hold.
        /// </summary>
        public static bool TryConvert(JsonNode? value, PipelineTableColumnType type, out JsonNode? converted, out string? error)
        {
            converted = null;
            error = null;
            if (value is null || (value is JsonValue nothing && nothing.GetValueKind() == JsonValueKind.Null))
            {
                return true;
            }

            var text = value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : value.ToJsonString();
            if (type == PipelineTableColumnType.Text)
            {
                if (text.Length > MaxValueLength)
                {
                    error = $"is over {MaxValueLength} characters";
                    return false;
                }

                converted = text.Length == 0 ? null : JsonValue.Create(text);
                return true;
            }

            if (value is JsonObject or JsonArray)
            {
                error = $"is a {(value is JsonArray ? "list" : "record")}, not {Describe(type)}";
                return false;
            }

            return TryConvert(text, type, out converted, out error);
        }

        /// <summary>A value typed as text (the Data tables page, an Update's values) as the column keeps it - <see cref="TryConvert(JsonNode?, PipelineTableColumnType, out JsonNode?, out string?)"/>.</summary>
        public static bool TryConvert(string? text, PipelineTableColumnType type, out JsonNode? converted, out string? error)
        {
            converted = null;
            error = null;
            text = type == PipelineTableColumnType.Text ? text ?? "" : (text ?? "").Trim();
            if (text.Length == 0)
            {
                return true;
            }

            switch (type)
            {
                case PipelineTableColumnType.Text:
                    if (text.Length > MaxValueLength)
                    {
                        error = $"is over {MaxValueLength} characters";
                        return false;
                    }

                    converted = JsonValue.Create(text);
                    return true;
                case PipelineTableColumnType.Number:
                    if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        // Without trailing zeros, so 3, 3.0 and "3.00" are one value - an Upsert's match compares them.
                        converted = JsonValue.Create(number / 1.0000000000000000000000000000m);
                        return true;
                    }

                    break;
                case PipelineTableColumnType.Boolean:
                    switch (text.ToLowerInvariant())
                    {
                        case "true" or "yes" or "1":
                            converted = JsonValue.Create(true);
                            return true;
                        case "false" or "no" or "0":
                            converted = JsonValue.Create(false);
                            return true;
                    }

                    break;
                case PipelineTableColumnType.Date:
                    if (PipelineConditions.IsDate(text)
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                    {
                        converted = JsonValue.Create(Moment(moment));
                        return true;
                    }

                    break;
            }

            error = $"'{(text.Length > 40 ? text[..40] + "..." : text)}' is not {Describe(type)}";
            return false;
        }

        /// <summary>A moment as a table keeps it: ISO 8601, in UTC.</summary>
        public static string Moment(DateTimeOffset moment) =>
            moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

        /// <summary>A kept value as the page shows it and a cell is typed: text as it is, a number in invariant form, true or false.</summary>
        public static string Text(JsonNode? value) => value switch
        {
            null => "",
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => scalar.GetValue<string>(),
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.True => "true",
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.False => "false",
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Null => "",
            _ => value.ToJsonString(),
        };

        /// <summary>A column's type as the page and the editor name it.</summary>
        public static string Label(PipelineTableColumnType type) => type switch
        {
            PipelineTableColumnType.Number => "number",
            PipelineTableColumnType.Boolean => "true or false",
            PipelineTableColumnType.Date => "date",
            _ => "text",
        };

        /// <summary>What a column of the type holds, in words.</summary>
        public static string Describe(PipelineTableColumnType type) => type switch
        {
            PipelineTableColumnType.Number => "a number",
            PipelineTableColumnType.Boolean => "true or false",
            PipelineTableColumnType.Date => "an ISO 8601 date",
            _ => "text",
        };

        /// <summary>
        /// Values given as one <c>column: value</c> per line, or as a JSON object - an Update's values. Null, with the reason, when
        /// neither.
        /// </summary>
        public static Dictionary<string, JsonNode?>? ParseValues(string? text, out string? error)
        {
            error = null;
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var trimmed = (text ?? "").Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    if (JsonNode.Parse(trimmed) is not JsonObject given)
                    {
                        error = "The values are not a JSON object.";
                        return null;
                    }

                    foreach (var (name, value) in given)
                    {
                        values[name.Trim()] = value?.DeepClone();
                    }
                }
                catch (JsonException ex)
                {
                    error = "The values are not a JSON object: " + ex.Message;
                    return null;
                }

                return values;
            }

            foreach (var line in trimmed.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                var at = line.IndexOf(':');
                if (at <= 0)
                {
                    error = $"'{line}' is not 'column: value'.";
                    return null;
                }

                values[line[..at].Trim()] = JsonValue.Create(line[(at + 1)..].Trim());
            }

            return values;
        }
    }
}
