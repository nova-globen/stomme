#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.Snowflake
{
    /// <summary>
    /// Snowflake (BL-229, n8n's Snowflake node: a query run, rows inserted, rows updated): the account's SQL API v2 with a
    /// programmatic access token as a bearer, over the host's egress. A test names who the token signs in as.
    /// </summary>
    /// <remarks>
    /// <c>query</c> - SQL in the step's Query box, its values written <c>@name</c> (or <c>?</c>, given as <c>@1</c>, <c>@2</c>)
    /// and bound, never spliced in - an <c>@name</c> no parameter names left as it is, a stage; the statement waited for no longer
    /// than <c>wait</c> (60 seconds), every partition of its result read up to <c>max</c> (1,000) rows, each value typed by the
    /// result's columns: a whole number a number when it fits, a float a number, a boolean a flag, a date and a time as text,
    /// a timestamp its moment, a decimal kept as text (it has more digits than a number holds), a variant its JSON.
    /// <c>insert_rows</c> - a JSON object or a list of them (10,000 at most) into a table in statements of 200 rows, an object or a
    /// list among the values parsed as JSON; <c>update_rows</c> - each row by its key column's value, its other columns set.
    /// Tables and columns are unquoted identifiers (letters, digits, _ and $), held to that shape, so nothing is spliced in.
    /// The warehouse, database, schema and role per step or the connection's. A statement is sent asynchronously and its status
    /// asked of the account's own address - never an address an answer names. A throttled call waits; a failure is thrown with
    /// its status, Snowflake's code, message and SQL state.
    /// </remarks>
    public sealed class SnowflakeConnector : IConnector, IConnectorExtractor
    {
        public const string QueryOperation = "query";

        public const string InsertRowsOperation = "insert_rows";

        public const string UpdateRowsOperation = "update_rows";

        public const int DefaultMax = 1000;

        public const int DefaultWait = 60;

        private const int Batch = 200;

        private const int MostRows = 10000;

        private const int MostUpdates = 1000;

        private static readonly string[] Known = { QueryOperation, InsertRowsOperation, UpdateRowsOperation };

        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_$]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex Named = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

        private static readonly Regex Positional = new("^[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant);

        private static readonly Regex Handle = new("^[0-9A-Za-z\\-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "snowflake";

        public string DisplayName => "Snowflake";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => "https://kafe-analytics.snowflakecomputing.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = "Programmatic access token (Snowsight > your profile > Settings > Authentication)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.snowflake.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.snowflake.com/en/developer-guide/sql-api/reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("warehouse", "Warehouse", ConnectorConfigFieldType.Text, Required: false, Description: "The warehouse a step names none of - the user's default when left empty."),
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: false, Description: "The database a step names none of."),
            new ConnectorConfigField("schema", "Schema", ConnectorConfigFieldType.Text, Required: false, Description: "The schema a step names none of."),
            new ConnectorConfigField("role", "Role", ConnectorConfigFieldType.Text, Required: false, Description: "The role a step names none of - the token's own when it is restricted to one."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField[] Context =
        {
            Field("warehouse", "Warehouse", "The connection's when left empty."),
            Field("database", "Database", "The connection's when left empty."),
            Field("schema", "Schema", "The connection's when left empty."),
            Field("role", "Role", "The connection's when left empty."),
        };

        private static readonly ConnectorConfigField TableField = Field("table", "Table", "ORDERS, or SALES.PUBLIC.ORDERS - unquoted names.", required: true);

        private static readonly ConnectorConfigField ColumnsField = Field("columns", "Columns", "The columns to write, separated by commas - every key of the rows when left empty.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(QueryOperation, "Run a query", "SQL, its rows as records - n8n's Snowflake Execute Query.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Field("query", "Query", "SQL in the step's Query box - SELECT NAME, TOTAL FROM ORDERS WHERE CITY = @city; each value written @name (or ?) and given as an '@name = value' (or '@1 = value') parameter: bound, never spliced in.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("max", "At most", "How many rows, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    Field("wait", "Wait", "How many seconds to wait for the statement, at most 600.", ConnectorConfigFieldType.Integer) with { Default = DefaultWait.ToString(CultureInfo.InvariantCulture) },
                }.Concat(Context).ToArray()),
            },
            new ConnectorOperation(InsertRowsOperation, "Insert rows", "Into a table - n8n's Snowflake Insert.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField,
                    Field("rows", "Rows", "A JSON object or a list of them, by column - {\"NAME\": \"Kafé AB\", \"TOTAL\": 129.5}.", ConnectorConfigFieldType.Multiline, required: true),
                    ColumnsField,
                }.Concat(Context).ToArray()),
            },
            new ConnectorOperation(UpdateRowsOperation, "Update rows", "Each by its key column's value - n8n's Snowflake Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField,
                    Field("key", "Key column", "The column whose value finds each row - ID.", required: true) with { Default = "ID" },
                    Field("rows", "Rows", "A JSON object or a list of them, by column, each with its key - {\"ID\": 8, \"STATUS\": \"Paid\"}.", ConnectorConfigFieldType.Multiline, required: true),
                    ColumnsField,
                }.Concat(Context).ToArray()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = ApiOf(context.Endpoint);
                var token = TokenOf(context.Credentials);
                var session = SessionOf(new Dictionary<string, string>(StringComparer.Ordinal), context.Settings);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await RunAsync(client, token, api, "SELECT CURRENT_USER(), CURRENT_ROLE(), CURRENT_WAREHOUSE()", null, session, DefaultWait, cancellationToken);
                var row = ((answer["data"] as JsonArray)?.FirstOrDefault() as JsonArray)?.Select(Words).ToList() ?? new List<string?>();
                string At(int i) => i < row.Count && row[i] is { Length: > 0 } text ? text : "none";
                return ConnectionTestResult.Ok($"Snowflake: signed in as {At(0)} with the role {At(1)} - the warehouse {At(2)}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? QueryOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Snowflake has no operation '{context.OperationName}' - {QueryOperation}, {InsertRowsOperation} or {UpdateRowsOperation}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var api = ApiOf(context.Endpoint);
            var token = TokenOf(context.Credentials);
            var session = SessionOf(p, context.Settings);
            int NumberOf(string name, int fallback, int most) => Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= most ? n : throw new InvalidOperationException($"'{given}' is not a number from 1 to {most.ToString("N0", CultureInfo.InvariantCulture)} ({name}).")
                : fallback;
            var wait = NumberOf("wait", DefaultWait, 600);

            if (operation == QueryOperation)
            {
                var (sql, bindings) = Bind(Values.Get(p, "query") ?? throw new InvalidOperationException("Give the query (query)."), p);
                var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var cap) && cap > 0 ? cap : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await RunAsync(client, token, api, sql, bindings, session, wait, cancellationToken);
                var handle = Words(answer["statementHandle"]) is { } named && Handle.IsMatch(named) ? named : null;
                var columns = (answer["resultSetMetaData"]?["rowType"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                var partitions = (answer["resultSetMetaData"]?["partitionInfo"] as JsonArray)?.Count ?? 1;
                var read = 0;
                for (var partition = 0; partition < partitions; partition++)
                {
                    if (partition > 0)
                    {
                        answer = (await SendAsync(client, token, HttpMethod.Get, $"{api}/api/v2/statements/{handle ?? throw new InvalidOperationException("Snowflake answered a result in parts with no statement to ask for the rest.")}?partition={partition.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken)).Json as JsonObject
                            ?? throw new InvalidOperationException($"Snowflake answered part {partition + 1} of the result with nothing.");
                    }

                    foreach (var row in (answer["data"] as JsonArray ?? new JsonArray()).OfType<JsonArray>())
                    {
                        yield return new ConnectorRecord(Row(columns, row));
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }
                }

                yield break;
            }

            var table = TableOf(Values.Get(p, "table") ?? throw new InvalidOperationException("Give the table - ORDERS, or SALES.PUBLIC.ORDERS (table)."));
            var rows = RowsOf(Values.Get(p, "rows"), operation == UpdateRowsOperation ? MostUpdates : MostRows);
            var listed = Values.Get(p, "columns") is { } given ? ColumnsOf(given) : null;
            using (var client = context.Egress.CreateHttpClient(context.Timeout))
            {
                if (operation == InsertRowsOperation)
                {
                    var columns = listed ?? rows.SelectMany(row => row.Select(pair => pair.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var column in columns)
                    {
                        _ = Identifier.IsMatch(column) ? column : throw new InvalidOperationException($"'{column}' is not a column's name - letters, digits, _ and $ (rows).");
                    }

                    var statements = rows.Chunk(Batch).Select(batch => Insert(table, columns, batch)).ToList();
                    long written = 0;
                    for (var i = 0; i < statements.Count; i++)
                    {
                        try
                        {
                            written += Count(await RunAsync(client, token, api, statements[i].Sql, statements[i].Bindings, session, wait, cancellationToken));
                        }
                        catch (InvalidOperationException ex) when (i > 0)
                        {
                            throw new InvalidOperationException($"{ex.Message} - rows {(i * Batch) + 1} on were not written, {written} before them were.", ex);
                        }
                    }

                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["inserted"] = written, ["table"] = table });
                    yield break;
                }

                var key = Values.Get(p, "key") is { } k ? Identifier.IsMatch(k) ? k : throw new InvalidOperationException($"'{k}' is not a column's name - letters, digits, _ and $ (key).") : "ID";
                var updates = rows.Select((row, at) => Update(table, key, listed, row, at + 1)).ToList();
                long updated = 0;
                for (var i = 0; i < updates.Count; i++)
                {
                    try
                    {
                        updated += Count(await RunAsync(client, token, api, updates[i].Sql, updates[i].Bindings, session, wait, cancellationToken));
                    }
                    catch (InvalidOperationException ex) when (i > 0)
                    {
                        throw new InvalidOperationException($"{ex.Message} - row {i + 1} and those after it were not written; rows 1 to {i} were.", ex);
                    }
                }

                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["updated"] = updated, ["rows"] = (long)rows.Count, ["table"] = table });
            }
        }

        // The account's address: https://{account}.snowflakecomputing.com - nothing after it.
        private static string ApiOf(string? endpoint)
        {
            var given = (endpoint ?? "").Trim().TrimEnd('/');
            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var uri) && uri.Scheme is "https" or "http" && uri.AbsolutePath is "/" or "" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
                ? uri.GetLeftPart(UriPartial.Authority)
                : throw new InvalidOperationException("Give the account's address - https://kafe-analytics.snowflakecomputing.com.");
        }

        private static string TokenOf(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") is { Length: > 0 } token && !token.Any(char.IsWhiteSpace)
                ? token
                : throw new InvalidOperationException("Give the programmatic access token (token) - Snowsight > your profile > Settings > Authentication.");

        // The session's warehouse, database, schema and role: the step's, or the connection's - each an unquoted name.
        private static Dictionary<string, string> SessionOf(IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> settings)
        {
            var session = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "warehouse", "database", "schema", "role" })
            {
                if ((Values.Get(parameters, name) ?? Values.Get(settings, name)) is { } given)
                {
                    session[name] = Identifier.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {name}'s name - letters, digits, _ and $ ({name}).");
                }
            }

            return session;
        }

        private static string TableOf(string given)
        {
            var parts = given.Split('.');
            return parts.Length <= 3 && parts.All(Identifier.IsMatch) ? given : throw new InvalidOperationException($"'{given}' is not a table's name - ORDERS, or SALES.PUBLIC.ORDERS, unquoted (table).");
        }

        private static List<string> ColumnsOf(string given)
        {
            var columns = given.Split(',').Select(column => column.Trim()).Where(column => column.Length > 0).ToList();
            return columns.Count > 0 && columns.All(Identifier.IsMatch) && columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == columns.Count
                ? columns
                : throw new InvalidOperationException($"'{given}' is not a list of columns - NAME, TOTAL (columns).");
        }

        // The rows to write: a JSON object or a list of them.
        private static List<JsonObject> RowsOf(string? given, int most)
        {
            JsonNode? parsed;
            try
            {
                parsed = given is null ? throw new InvalidOperationException("Give the rows - a JSON object or a list of them (rows).") : JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            var rows = parsed switch
            {
                JsonObject one => new List<JsonObject> { one },
                JsonArray many when many.All(item => item is JsonObject) => many.OfType<JsonObject>().ToList(),
                _ => throw new InvalidOperationException("The rows are not a JSON object or a list of them - {\"NAME\": \"Kafé AB\"} (rows)."),
            };
            if (rows.Count is 0 || rows.Count > most || rows.Any(row => row.Count == 0))
            {
                throw new InvalidOperationException($"Give from 1 to {most.ToString("N0", CultureInfo.InvariantCulture)} rows, each with a column (rows).");
            }

            foreach (var name in rows.SelectMany(row => row.Select(pair => pair.Key)))
            {
                _ = Identifier.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a column's name - letters, digits, _ and $ (rows).");
            }

            return rows;
        }

        // An insert of a batch: the values as a VALUES table, a column holding an object or a list parsed as JSON - Snowflake parses
        // none in a VALUES clause itself - and a missing or null value a NULL.
        private static (string Sql, JsonObject Bindings) Insert(string table, IReadOnlyList<string> columns, IReadOnlyList<JsonObject> rows)
        {
            var json = columns.Select(column => rows.Any(row => ValueOf(row, column) is JsonObject or JsonArray)).ToList();
            var bindings = new JsonObject();
            var values = rows.Select(row => "(" + string.Join(", ", columns.Select((column, i) => Placeholder(bindings, ValueOf(row, column), json[i]))) + ")");
            var select = string.Join(", ", columns.Select((_, i) => json[i] ? $"PARSE_JSON(${i + 1})" : $"${i + 1}"));
            return ($"INSERT INTO {table} ({string.Join(", ", columns)}) SELECT {select} FROM VALUES {string.Join(", ", values)}", bindings);
        }

        // An update of one row by its key's value: the listed columns, or every other key of the row.
        private static (string Sql, JsonObject Bindings) Update(string table, string key, IReadOnlyList<string>? listed, JsonObject row, int number)
        {
            var match = row.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not JsonValue keyValue || keyValue.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
            {
                throw new InvalidOperationException($"Row {number} has no value of {key}, a text or a number, to find it by (rows).");
            }

            var columns = listed ?? row.Select(pair => pair.Key).Where(name => !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (columns.Count == 0)
            {
                throw new InvalidOperationException($"Row {number} has nothing to set but its key (rows).");
            }

            var bindings = new JsonObject();
            var set = columns.Select(column =>
            {
                var value = row.FirstOrDefault(pair => string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase));
                if (value.Key is null)
                {
                    throw new InvalidOperationException($"Row {number} has no {column} (rows).");
                }

                var placeholder = Placeholder(bindings, value.Value, value.Value is JsonObject or JsonArray);
                return $"{column} = {(value.Value is JsonObject or JsonArray ? $"PARSE_JSON({placeholder})" : placeholder)}";
            }).ToList();
            var where = Placeholder(bindings, keyValue, false);
            return ($"UPDATE {table} SET {string.Join(", ", set)} WHERE {key} = {where}", bindings);
        }

        // A value's placeholder and its binding, typed by the value: a whole number FIXED, a fraction REAL, a flag BOOLEAN, text
        // TEXT, an object or a list its JSON as TEXT; a null - or nothing - a NULL, bound to nothing.
        private static string Placeholder(JsonObject bindings, JsonNode? value, bool asJson)
        {
            if (value is null || value is JsonValue nothing && nothing.GetValueKind() == JsonValueKind.Null)
            {
                return "NULL";
            }

            var (type, text) = asJson ? ("TEXT", value.ToJsonString(Wire)) : value is JsonValue scalar ? scalar.GetValueKind() switch
            {
                JsonValueKind.Number => (scalar.ToJsonString().Contains('.') || scalar.ToJsonString().Contains('e') || scalar.ToJsonString().Contains('E') ? "REAL" : "FIXED", scalar.ToJsonString()),
                JsonValueKind.True => ("BOOLEAN", "true"),
                JsonValueKind.False => ("BOOLEAN", "false"),
                _ => ("TEXT", scalar.GetValue<string>()),
            } : ("TEXT", value.ToJsonString(Wire));
            bindings[(bindings.Count + 1).ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["type"] = type, ["value"] = text };
            return "?";
        }

        // The query's values bound: each ? the next of @1, @2, ... and each @name a parameter names a ?, both outside text, quoted
        // names and comments; an @name no parameter names left as it is (a stage). Every parameter given has to be used.
        private static (string Sql, JsonObject? Bindings) Bind(string sql, IReadOnlyDictionary<string, string> parameters)
        {
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            var positional = new Dictionary<int, string>();
            foreach (var (key, value) in parameters.Where(pair => pair.Key.StartsWith('@')))
            {
                var name = key[1..];
                if (Positional.IsMatch(name))
                {
                    positional[int.Parse(name, CultureInfo.InvariantCulture)] = value;
                }
                else
                {
                    named[Named.IsMatch(name) ? name : throw new InvalidOperationException($"'{key}' is not a parameter's name - @ and letters, digits and _, or @1, @2 for each ? (parameters).")] = value;
                }
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new JsonObject();
            var question = 0;
            var output = new StringBuilder(sql.Length);
            void Bound(string value) => bindings[(bindings.Count + 1).ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["type"] = "TEXT", ["value"] = value };
            for (var i = 0; i < sql.Length;)
            {
                var c = sql[i];
                int end;
                if (c is '\'' or '"')
                {
                    // Text ('...', a quote after a backslash its own) or a quoted name ("..."); a doubled quote closes one and opens
                    // the next, which leaves inside and outside where they were.
                    end = i + 1;
                    while (end < sql.Length)
                    {
                        if (c == '\'' && sql[end] == '\\')
                        {
                            end += 2;
                        }
                        else if (sql[end] == c)
                        {
                            end++;
                            break;
                        }
                        else
                        {
                            end++;
                        }
                    }

                    end = Math.Min(end, sql.Length);
                }
                else if (c == '$' && i + 1 < sql.Length && sql[i + 1] == '$')
                {
                    end = sql.IndexOf("$$", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? sql.Length : end + 2;
                }
                else if ((c == '-' || c == '/') && i + 1 < sql.Length && sql[i + 1] == c)
                {
                    end = sql.IndexOf('\n', i);
                    end = end < 0 ? sql.Length : end;
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? sql.Length : end + 2;
                }
                else if (c == '?')
                {
                    question++;
                    Bound(positional.TryGetValue(question, out var value) ? value : throw new InvalidOperationException($"The query's ? number {question} has no value - give it as '@{question} = value' (parameters)."));
                    output.Append('?');
                    i++;
                    continue;
                }
                else if (c == '@' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    end = i + 1;
                    while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
                    {
                        end++;
                    }

                    var name = sql[(i + 1)..end];
                    if (named.TryGetValue(name, out var value) && (end >= sql.Length || sql[end] != '.' && sql[end] != '/'))
                    {
                        Bound(value);
                        used.Add(name);
                        output.Append('?');
                        i = end;
                        continue;
                    }
                }
                else
                {
                    end = i + 1;
                }

                output.Append(sql, i, end - i);
                i = end;
            }

            if (named.Keys.FirstOrDefault(name => !used.Contains(name)) is { } unused)
            {
                throw new InvalidOperationException($"The query has no @{unused} - give a parameter only for a value it names (parameters).");
            }

            if (positional.Keys.FirstOrDefault(number => number > question) is var extra && extra > 0)
            {
                throw new InvalidOperationException($"The query has no ? number {extra} - it has {question} (parameters).");
            }

            return (output.ToString(), bindings.Count == 0 ? null : bindings);
        }

        // A result row: each value typed by its column.
        private static Dictionary<string, object?> Row(IReadOnlyList<JsonObject> columns, JsonArray row)
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                record[Words(columns[i]["name"]) ?? $"C{i + 1}"] = Cell(columns[i], i < row.Count ? row[i] : null);
            }

            return record;
        }

        private static object? Cell(JsonObject column, JsonNode? value)
        {
            if (Words(value) is not { } text)
            {
                return value is null ? null : value.ToJsonString(Wire);
            }

            var scale = column["scale"] is JsonValue s && s.TryGetValue<int>(out var digits) ? digits : 0;
            return Words(column["type"])?.ToLowerInvariant() switch
            {
                "fixed" when scale == 0 && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => whole,
                "real" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : text,
                "boolean" => text == "true" ? true : text == "false" ? false : text,
                "date" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days) ? DateTime.UnixEpoch.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text,
                "time" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var since) ? TimeSpan.FromTicks((long)(since * TimeSpan.TicksPerSecond)).ToString("hh\\:mm\\:ss\\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') : text,
                "timestamp_ntz" => Moment(text) is { } ntz ? ntz.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) : text,
                "timestamp_ltz" => Moment(text) is { } ltz ? ltz.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture) : text,
                "timestamp_tz" => text.Split(' ') is [var at, var zone] && Moment(at) is { } utc && int.TryParse(zone, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                    ? new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(minutes - 1440)).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)
                    : text,
                _ => text,
            };
        }

        private static DateTime? Moment(string seconds) =>
            decimal.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var since) ? DateTime.UnixEpoch.AddTicks((long)(since * TimeSpan.TicksPerSecond)) : null;

        // A row's value of a column - Snowflake's unquoted names are the same in any case.
        private static JsonNode? ValueOf(JsonObject row, string column) => row.FirstOrDefault(pair => string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase)).Value;

        // A DML statement's count: its first value.
        private static long Count(JsonObject answer) =>
            ((answer["data"] as JsonArray)?.FirstOrDefault() as JsonArray)?.FirstOrDefault() is { } first && long.TryParse(Words(first), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // A statement sent asynchronously and its status asked of the account's own address until it has a result, no longer than
        // the wait; the result's first part.
        private static async Task<JsonObject> RunAsync(HttpClient client, string token, string api, string sql, JsonObject? bindings, IReadOnlyDictionary<string, string> session, int wait, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["statement"] = sql };
            if (bindings is not null)
            {
                body["bindings"] = bindings;
            }

            foreach (var (name, value) in session)
            {
                body[name] = value;
            }

            var (status, json) = await SendAsync(client, token, HttpMethod.Post, $"{api}/api/v2/statements?requestId={Guid.NewGuid():D}&async=true", body, cancellationToken);
            var clock = Stopwatch.StartNew();
            var pause = 250;
            while (status == (int)HttpStatusCode.Accepted)
            {
                var handle = Words((json as JsonObject)?["statementHandle"]) is { } named && Handle.IsMatch(named) ? named : throw new InvalidOperationException("Snowflake took the statement with no handle to ask after it by.");
                if (clock.Elapsed.TotalSeconds >= wait)
                {
                    throw new InvalidOperationException($"Snowflake had not finished the statement in {wait} seconds - its statement {handle} goes on: give it longer (wait).");
                }

                await Task.Delay(pause, cancellationToken);
                pause = Math.Min(pause * 2, 2000);
                (status, json) = await SendAsync(client, token, HttpMethod.Get, $"{api}/api/v2/statements/{handle}", null, cancellationToken);
            }

            return json as JsonObject ?? throw new InvalidOperationException("Snowflake answered the statement with nothing.");
        }

        // Snowflake's answer as JSON (a gzipped one unzipped), a throttled call waiting; a failure thrown with its status and
        // Snowflake's code, message and SQL state; a success that is not JSON a failure that says so.
        private static async Task<(int Status, JsonNode? Json)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("X-Snowflake-Authorization-Token-Type", "PROGRAMMATIC_ACCESS_TOKEN");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Stomme", "1.0"));
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            string payload;
            if (response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
            {
                await using var zipped = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var unzipped = new GZipStream(zipped, CompressionMode.Decompress);
                using var reader = new StreamReader(unzipped, Encoding.UTF8);
                payload = await reader.ReadToEndAsync(cancellationToken);
            }
            else
            {
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Snowflake answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = json as JsonObject;
                var said = string.Join(": ", new[] { Words(failure?["code"]), Words(failure?["message"]) }.Where(part => !string.IsNullOrEmpty(part)));
                var state = Words(failure?["sqlState"]) is { Length: > 0 } sqlState ? $" (SQL state {sqlState})" : "";
                var text = said + state;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return ((int)response.StatusCode, json);
        }
    }
}
