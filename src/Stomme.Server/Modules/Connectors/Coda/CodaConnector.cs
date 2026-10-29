#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Coda
{
    /// <summary>
    /// Coda (n8n's Coda node: a table's or a view's rows read, got, created, upserted, updated and deleted, its buttons
    /// pushed, its columns, a doc's formulas and controls read): Coda's API v1 (<c>/apis/v1</c>), an API token as a bearer
    /// token, over the host's egress. A test names the token.
    /// </summary>
    /// <remarks>
    /// <c>rows</c> (a query, a sort, visible rows only, a value format, page by page up to <c>max</c>), <c>get_row</c>,
    /// <c>create_rows</c> (one or a list; with key columns an upsert), <c>update_row</c>, <c>delete_rows</c>,
    /// <c>push_button</c>; <c>docs</c>, <c>tables</c> (views too), <c>columns</c>, <c>get_column</c>, <c>formulas</c>,
    /// <c>get_formula</c>, <c>controls</c>, <c>get_control</c>. Coda applies a write later, answering with its request's id; a
    /// write with <c>wait</c> waits until Coda says it is applied, so the next step reads it. A table, row, column, formula or
    /// control is its id or its name. Every one held to its shape before anything is sent. A throttled call waits as Coda says;
    /// a failure is thrown with its status and Coda's word.
    /// </remarks>
    public sealed class CodaConnector : IConnector, IConnectorExtractor
    {
        public const string RowsOperation = "rows";

        public const string GetRowOperation = "get_row";

        public const string CreateRowsOperation = "create_rows";

        public const string UpdateRowOperation = "update_row";

        public const string DeleteRowsOperation = "delete_rows";

        public const string PushButtonOperation = "push_button";

        public const string DocsOperation = "docs";

        public const string TablesOperation = "tables";

        public const string GetTableOperation = "get_table";

        public const string ColumnsOperation = "columns";

        public const string GetColumnOperation = "get_column";

        public const string FormulasOperation = "formulas";

        public const string GetFormulaOperation = "get_formula";

        public const string ControlsOperation = "controls";

        public const string GetControlOperation = "get_control";

        public const string DefaultEndpoint = "https://coda.io";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const int MostAtOnce = 500;

        private static readonly Regex DocId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        // An id or a name - any text of a line but a run of dots, which a path would read as a step up.
        private static readonly Regex IdOrName = new("^(?!\\.+$)[^\\x00-\\x1F]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RowsOperation, GetRowOperation, CreateRowsOperation, UpdateRowOperation, DeleteRowsOperation, PushButtonOperation, DocsOperation, TablesOperation, GetTableOperation, ColumnsOperation, GetColumnOperation, FormulasOperation, GetFormulaOperation, ControlsOperation, GetControlOperation };

        private static readonly string[] SortOrders = { "createdAt", "natural", "updatedAt" };

        private static readonly string[] ValueFormats = { "simple", "simpleWithArrays", "rich" };

        // A write with wait asks Coda each second whether it is applied, for a minute at most.
        private const int WaitTries = 60;

        private readonly TimeSpan waitPause;

        public CodaConnector()
            : this(TimeSpan.FromSeconds(1))
        {
        }

        // A test's own pause between the asks; the host builds the connector through the public constructor.
        internal CodaConnector(TimeSpan waitPause) => this.waitPause = waitPause;

        public string ConnectorType => "coda";

        public string DisplayName => "Coda";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://coda.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://coda.io/developers/apis/v1");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField DocField = new("doc", "Doc", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - the part after _d in its address, AbCDeFGH.");

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its name - grid-pqRst-U or Orders; a view's too.");

        private static readonly ConnectorConfigField RowField = new("row", "Row", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its display column's value - i-tuVwxYz.");

        private static readonly ConnectorConfigField WaitField = new("wait", "Wait", ConnectorConfigFieldType.Text, Required: false, Description: "true: wait until Coda has applied it, so the next step reads it; false (the default): go on once Coda has it.");

        private static ConnectorConfigField MaxField() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Fields(string description) => new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: description);

        private static ConnectorConfigField ColumnNames() => new("column_names", "Column names", ConnectorConfigFieldType.Text, Required: false, Description: "true (the default): each value under its column's name; false: under its column's id.");

        private static ConnectorConfigField Format() => new("value_format", "Value format", ConnectorConfigFieldType.Text, Required: false, Description: "simple (the default), simpleWithArrays or rich.");

        private static ConnectorConfigField Parse() => new("parse", "Parse", ConnectorConfigFieldType.Text, Required: false, Description: "true (the default): Coda reads each value by its column's type; false: as written.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RowsOperation, "Get rows", "A table's or a view's rows - n8n's Coda Get All Rows.")
            {
                Parameters = Schema(
                    DocField, TableField,
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Text, Required: false, Description: "A column and a value - Status:\"Paid\", the value as JSON."),
                    new ConnectorConfigField("sort_by", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: "createdAt (the default), natural (the table's own order) or updatedAt."),
                    new ConnectorConfigField("visible_only", "Visible only", ConnectorConfigFieldType.Text, Required: false, Description: "true: only the rows and columns the table shows."),
                    ColumnNames(), Format(), MaxField()),
            },
            new ConnectorOperation(GetRowOperation, "Get a row", "One row - n8n's Coda Get Row.") { Parameters = Schema(DocField, TableField, RowField, ColumnNames(), Format()) },
            new ConnectorOperation(CreateRowsOperation, "Create rows", "A row, or a list of them; with key columns, the row matching them updated instead - n8n's Coda Create Row.")
            {
                Parameters = Schema(
                    DocField, TableField, Fields("A JSON object of columns and values - {\"Name\": \"Ada\", \"Total\": 99.5}; a list of them for many."),
                    new ConnectorConfigField("key_columns", "Key columns", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated columns a row is found by - an upsert."),
                    Parse(), WaitField),
            },
            new ConnectorOperation(UpdateRowOperation, "Update a row", "Changes the fields given - n8n's Coda Update View Row.") { Parameters = Schema(DocField, TableField, RowField, Fields("A JSON object of columns and values - {\"Paid\": true}."), Parse(), WaitField) },
            new ConnectorOperation(DeleteRowsOperation, "Delete rows", "Deletes rows by their ids - n8n's Coda Delete Row.")
            {
                Parameters = Schema(DocField, TableField, new ConnectorConfigField("rows", "Rows", ConnectorConfigFieldType.Text, Required: true, Description: "Comma-separated row ids - i-tuVwxYz,i-AbCdEf."), WaitField),
            },
            new ConnectorOperation(PushButtonOperation, "Push a button", "Pushes a row's button - n8n's Coda Push Button.")
            {
                Parameters = Schema(DocField, TableField, RowField, new ConnectorConfigField("column", "Button column", ConnectorConfigFieldType.Text, Required: true, Description: "The button's column - its id or its name."), WaitField),
            },
            new ConnectorOperation(DocsOperation, "Get docs", "The docs the token sees.") { Parameters = Schema(MaxField()) },
            new ConnectorOperation(TablesOperation, "Get tables", "A doc's tables and views - n8n's Coda Get All Views too.")
            {
                Parameters = Schema(DocField, new ConnectorConfigField("types", "Types", ConnectorConfigFieldType.Text, Required: false, Description: "table, view or both - table,view."), MaxField()),
            },
            new ConnectorOperation(GetTableOperation, "Get a table", "A table's or a view's type and how many rows it has - n8n's Coda Get View.") { Parameters = Schema(DocField, TableField) },
            new ConnectorOperation(ColumnsOperation, "Get columns", "A table's columns - n8n's Coda Get All Columns.") { Parameters = Schema(DocField, TableField, MaxField()) },
            new ConnectorOperation(GetColumnOperation, "Get a column", "One column - n8n's Coda Get Column.")
            {
                Parameters = Schema(DocField, TableField, new ConnectorConfigField("column", "Column", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its name.")),
            },
            new ConnectorOperation(FormulasOperation, "Get formulas", "A doc's named formulas - n8n's Coda Get All Formulas.") { Parameters = Schema(DocField, MaxField()) },
            new ConnectorOperation(GetFormulaOperation, "Get a formula", "A named formula and its value - n8n's Coda Get Formula.")
            {
                Parameters = Schema(DocField, new ConnectorConfigField("formula", "Formula", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its name.")),
            },
            new ConnectorOperation(ControlsOperation, "Get controls", "A doc's controls - n8n's Coda Get All Controls.") { Parameters = Schema(DocField, MaxField()) },
            new ConnectorOperation(GetControlOperation, "Get a control", "A control and its value - n8n's Coda Get Control.")
            {
                Parameters = Schema(DocField, new ConnectorConfigField("control", "Control", ConnectorConfigFieldType.Text, Required: true, Description: "Its id or its name.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout, waitPause);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/whoami", null, cancellationToken);
                return ConnectionTestResult.Ok($"Coda: the token {(Text(me?["tokenName"]) is { Length: > 0 } name ? $"'{name}' " : "")}answers.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? RowsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Coda has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var doc = operation == DocsOperation ? null
                : Values.Get(p, "doc") is { } named ? DocId.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a doc's id - letters, digits, _ and -.") : throw new InvalidOperationException("Give the doc - its id.");
            var table = operation is DocsOperation or TablesOperation or FormulasOperation or GetFormulaOperation or ControlsOperation or GetControlOperation ? null : Named(p, "table");
            var row = operation is GetRowOperation or UpdateRowOperation or PushButtonOperation ? Named(p, "row") : null;
            var column = operation is PushButtonOperation or GetColumnOperation ? Named(p, "column") : null;
            var formula = operation == GetFormulaOperation ? Named(p, "formula") : null;
            var control = operation == GetControlOperation ? Named(p, "control") : null;
            var wait = Flag(p, "wait", false);

            var rowIds = new List<string>();
            if (operation == DeleteRowsOperation)
            {
                rowIds = List(p, "rows").Distinct(StringComparer.Ordinal).ToList();
                if (rowIds.Count > MostAtOnce)
                {
                    throw new InvalidOperationException($"Give up to {MostAtOnce} rows at once.");
                }
            }

            var query = new List<string>();
            if (operation is RowsOperation or GetRowOperation)
            {
                query.Add("useColumnNames=" + (Flag(p, "column_names", true) ? "true" : "false"));
                if (Values.Get(p, "value_format") is { } format)
                {
                    query.Add("valueFormat=" + (ValueFormats.FirstOrDefault(known => string.Equals(known, format, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{format}' is not simple, simpleWithArrays or rich (value format).")));
                }
            }

            if (operation == RowsOperation)
            {
                if (Values.Get(p, "query") is { } wanted)
                {
                    query.Add("query=" + Uri.EscapeDataString(wanted.Contains(':', StringComparison.Ordinal) && !wanted.Any(char.IsControl) ? wanted : throw new InvalidOperationException("The query is a column and a value on one line - Status:\"Paid\".")));
                }

                if (Values.Get(p, "sort_by") is { } order)
                {
                    query.Add("sortBy=" + (SortOrders.FirstOrDefault(known => string.Equals(known, order, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{order}' is not createdAt, natural or updatedAt (sort by).")));
                }

                if (Values.Get(p, "visible_only") is not null)
                {
                    query.Add("visibleOnly=" + (Flag(p, "visible_only", false) ? "true" : "false"));
                }
            }

            if (operation == TablesOperation && Values.Get(p, "types") is not null)
            {
                var types = List(p, "types").Select(type => type.ToLowerInvariant()).Distinct().ToList();
                query.Add("tableTypes=" + (types.All(type => type is "table" or "view") ? string.Join(",", types) : throw new InvalidOperationException("The types are table, view or both - table,view.")));
            }

            var rows = new List<JsonObject>();
            if (operation is CreateRowsOperation or UpdateRowOperation)
            {
                rows = Json(Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object.")) switch
                {
                    JsonObject one when one.Count > 0 => new List<JsonObject> { one },
                    JsonArray many when operation == CreateRowsOperation && many.Count is > 0 and <= MostAtOnce && many.All(item => item is JsonObject { Count: > 0 }) => many.Select(item => (JsonObject)item!.DeepClone()).ToList(),
                    JsonArray => throw new InvalidOperationException(operation == CreateRowsOperation ? $"Give one to {MostAtOnce} rows - JSON objects with a field or more." : "Give one row's fields - a JSON object."),
                    _ => throw new InvalidOperationException("The fields are not a JSON object with a field or more - {\"Name\": \"Ada\"}."),
                };
                if (!Flag(p, "parse", true))
                {
                    query.Add("disableParsing=true");
                }
            }

            var keyColumns = operation == CreateRowsOperation && Values.Get(p, "key_columns") is not null ? List(p, "key_columns") : new List<string>();

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout, waitPause);
            using var client = session.Client;
            var tail = query.Count == 0 ? "" : "?" + string.Join("&", query);
            var tablePath = $"/docs/{doc}/tables/{Escape(table)}";
            switch (operation)
            {
                case RowsOperation:
                    await foreach (var item in session.PagesAsync(tablePath + "/rows", query, max, cancellationToken))
                    {
                        yield return Row(item);
                    }

                    yield break;
                case GetRowOperation:
                    yield return Row(await session.SendAsync(HttpMethod.Get, $"{tablePath}/rows/{Escape(row)}{tail}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateRowsOperation:
                    {
                        var body = new JsonObject { ["rows"] = new JsonArray(rows.Select(fields => (JsonNode)new JsonObject { ["cells"] = Cells(fields) }).ToArray()) };
                        if (keyColumns.Count > 0)
                        {
                            body["keyColumns"] = new JsonArray(keyColumns.Select(key => (JsonNode)key).ToArray());
                        }

                        var answer = await session.SendAsync(HttpMethod.Post, tablePath + "/rows" + tail, body, cancellationToken);
                        var request = await session.SettledAsync(answer, wait, cancellationToken);

                        // Coda names the rows it added, in order, only when it matched none - with key columns a row may have been updated.
                        var added = (answer?["addedRowIds"] as JsonArray ?? new JsonArray()).Select(Text).ToList();
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["request_id"] = request };
                            if (keyColumns.Count == 0 && added.Count == rows.Count)
                            {
                                fields["id"] = added[i];
                            }

                            yield return Written(fields, rows[i]);
                        }

                        yield break;
                    }

                case UpdateRowOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Put, $"{tablePath}/rows/{Escape(row)}{tail}", new JsonObject { ["row"] = new JsonObject { ["cells"] = Cells(rows[0]) } }, cancellationToken);
                        var request = await session.SettledAsync(answer, wait, cancellationToken);
                        yield return Written(new Dictionary<string, object?>(StringComparer.Ordinal) { ["request_id"] = request, ["id"] = Text(answer?["id"]) ?? row }, rows[0]);
                        yield break;
                    }

                case DeleteRowsOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Delete, tablePath + "/rows", new JsonObject { ["rowIds"] = new JsonArray(rowIds.Select(id => (JsonNode)id).ToArray()) }, cancellationToken);
                        var request = await session.SettledAsync(answer, wait, cancellationToken);
                        foreach (var id in rowIds)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["id"] = id, ["deleted"] = true, ["request_id"] = request });
                        }

                        yield break;
                    }

                case PushButtonOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, $"{tablePath}/rows/{Escape(row)}/buttons/{Escape(column)}", null, cancellationToken);
                        var request = await session.SettledAsync(answer, wait, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["row"] = Text(answer?["rowId"]) ?? row, ["column"] = Text(answer?["columnId"]) ?? column, ["request_id"] = request });
                        yield break;
                    }

                case DocsOperation:
                    await foreach (var item in session.PagesAsync("/docs", query, max, cancellationToken))
                    {
                        yield return Named(item);
                    }

                    yield break;
                case TablesOperation:
                    await foreach (var item in session.PagesAsync($"/docs/{doc}/tables", query, max, cancellationToken))
                    {
                        yield return Named(item, ("type", Text(item["tableType"])));
                    }

                    yield break;
                case GetTableOperation:
                    {
                        var item = await session.SendAsync(HttpMethod.Get, tablePath, null, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Named(item, ("type", Text(item["tableType"])), ("rows", item["rowCount"] is JsonValue count && count.TryGetValue<long>(out var total) ? (long?)total : null));
                        yield break;
                    }

                case ColumnsOperation:
                    await foreach (var item in session.PagesAsync(tablePath + "/columns", query, max, cancellationToken))
                    {
                        yield return Column(item);
                    }

                    yield break;
                case GetColumnOperation:
                    yield return Column(await session.SendAsync(HttpMethod.Get, $"{tablePath}/columns/{Escape(column)}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case FormulasOperation:
                    await foreach (var item in session.PagesAsync($"/docs/{doc}/formulas", query, max, cancellationToken))
                    {
                        yield return Named(item);
                    }

                    yield break;
                case GetFormulaOperation:
                    {
                        var item = await session.SendAsync(HttpMethod.Get, $"/docs/{doc}/formulas/{Escape(formula)}", null, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Named(item, ("value", Plain(item["value"])));
                        yield break;
                    }

                case ControlsOperation:
                    await foreach (var item in session.PagesAsync($"/docs/{doc}/controls", query, max, cancellationToken))
                    {
                        yield return Named(item, ("type", Text(item["controlType"])));
                    }

                    yield break;
                default:
                    {
                        var item = await session.SendAsync(HttpMethod.Get, $"/docs/{doc}/controls/{Escape(control)}", null, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Named(item, ("type", Text(item["controlType"])), ("value", Plain(item["value"])));
                        yield break;
                    }
            }
        }

        private static string Named(IReadOnlyDictionary<string, string> parameters, string key) =>
            Values.Get(parameters, key) is { } given
                ? IdOrName.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {key}'s id or name - one line, not dots alone.")
                : throw new InvalidOperationException($"Give the {key} - its id or its name.");

        // A comma-separated list of ids or names, each held to its shape.
        private static List<string> List(IReadOnlyDictionary<string, string> parameters, string key) =>
            (Values.Get(parameters, key) ?? throw new InvalidOperationException($"Give the {key} - comma-separated.")).Split(',', StringSplitOptions.TrimEntries)
                .Select(item => IdOrName.IsMatch(item) ? item : throw new InvalidOperationException($"'{item}' is not an id or a name ({key}).")).ToList();

        private static bool Flag(IReadOnlyDictionary<string, string> parameters, string key, bool fallback) => Values.Get(parameters, key) switch
        {
            null => fallback,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({key.Replace('_', ' ')})."),
        };

        private static string Escape(string? segment) => Uri.EscapeDataString(segment ?? "");

        private static JsonNode? Json(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A row's fields as Coda's cells - each a column and its value.
        private static JsonArray Cells(JsonObject fields) =>
            new(fields.Select(field => (JsonNode)new JsonObject { ["column"] = field.Key, ["value"] = field.Value?.DeepClone() }).ToArray());

        // A row: its id, then its values by their columns' names (or ids) - a list or an object as its JSON.
        private static ConnectorRecord Row(JsonObject row)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(row["id"]) };
            foreach (var (name, value) in row["values"] as JsonObject ?? new JsonObject())
            {
                fields[name] = Plain(value);
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord Written(Dictionary<string, object?> fields, JsonObject written)
        {
            foreach (var (name, value) in written)
            {
                fields[name] = Plain(value);
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord Named(JsonObject item, params (string Name, object? Value)[] more)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]), ["name"] = Text(item["name"]) };
            foreach (var (name, value) in more)
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord Column(JsonObject item) => Named(
            item,
            ("type", Text(item["format"]?["type"])),
            ("display", item["display"] is JsonValue display && display.TryGetValue<bool>(out var shown) && shown),
            ("calculated", item["calculated"] is JsonValue calculated && calculated.TryGetValue<bool>(out var formulaic) && formulaic),
            ("formula", Text(item["formula"])));

        private static object? Plain(JsonNode? value) => value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            _ => value.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Coda's address, the token as a bearer token; a throttled call waiting as Coda says; a failure
        // thrown with its status and Coda's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string token;
            private readonly TimeSpan pause;

            private Session(string api, string token, HttpClient client, TimeSpan pause)
            {
                this.api = api;
                this.token = token;
                this.pause = pause;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeSpan pause)
            {
                // Coda's address, or its API's own - https://coda.io or https://coda.io/apis/v1.
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                var api = root.EndsWith("/apis/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/apis/v1";
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Coda connection has no API token.");
                return new Session(api, token, egress.CreateHttpClient(timeout), pause);
            }

            // A list's items, page by page by Coda's page token, up to max.
            public async IAsyncEnumerable<JsonObject> PagesAsync(string path, IReadOnlyList<string> query, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                string? next = null;
                do
                {
                    var asked = Math.Min(PageSize, max - read);
                    var page = await SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"limit={asked}").Concat(next is null ? Array.Empty<string>() : new[] { "pageToken=" + Uri.EscapeDataString(next) })), null, cancellationToken);
                    foreach (var item in (page?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return item;
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = Text(page?["nextPageToken"]);
                }
                while (next is { Length: > 0 });
            }

            // A write's request id; with wait, once Coda says the write is applied.
            public async Task<string?> SettledAsync(JsonNode? answer, bool wait, CancellationToken cancellationToken)
            {
                var request = Text(answer?["requestId"]);
                if (!wait || request is null)
                {
                    return request;
                }

                for (var attempt = 0; attempt < WaitTries; attempt++)
                {
                    var status = await SendAsync(HttpMethod.Get, "/mutationStatus/" + Uri.EscapeDataString(request), null, cancellationToken);
                    if (status?["completed"] is JsonValue completed && completed.TryGetValue<bool>(out var done) && done)
                    {
                        return request;
                    }

                    await Task.Delay(pause, cancellationToken);
                }

                throw new InvalidOperationException($"Coda had not applied the change after {WaitTries} tries (request {request}).");
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Coda's - a sign-in page, a proxy's - read as no rows would hide that it never was.
                    throw new InvalidOperationException($"Coda answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["statusMessage"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
