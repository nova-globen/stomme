#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.SeaTable
{
    /// <summary>
    /// SeaTable (BL-229, n8n's SeaTable node) over SeaTable's API by a base's API token - from the base's own settings - which
    /// the connector exchanges for a base access token (<c>/api/v2.1/dtable/app-access-token/</c>), each a <c>Token</c>
    /// header, never in the address. A test names the base the token is for.
    /// </summary>
    /// <remarks>
    /// n8n's resources: rows - listed, read, searched, made, changed, deleted, locked and unlocked; the base - its metadata, its
    /// collaborators and a snapshot; links between rows; an asset's download link. Measured against SeaTable 6.2
    /// (<c>scripts/seatable-test-server.ps1</c>): n8n's search builds its SQL with the term escaped by backslashes, which
    /// SeaTable's SQL does not read as escapes - a term with a quote (O'Brien) finds nothing and one with a double quote is a
    /// parse error - so a search here binds its term as a parameter; a row made or changed with a value its column does not
    /// hold (a word in a number, a date that is no date) is kept with that value EMPTIED and a field the table has not got is
    /// dropped, without a word, and a single-select value it does not offer ADDS the option to the column - so every field is
    /// held to its column, by the table's own metadata, before anything is sent; a change of a row that is not there answers
    /// "base not found in big data storage" and a delete of one answers 200 with nothing deleted, so a change reads the row
    /// first and a delete says whether it deleted; a snapshot refused for being too soon after the last answers 200, which is
    /// said here as a failure. Rows come back by their columns' NAMES, not SeaTable's keys. Every address is under the
    /// connection's own - never the server address SeaTable names in its answer, which is its own idea of its address.
    /// </remarks>
    public sealed class SeaTableConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 1000; // SeaTable's own most

        private const int MostText = 1048576;

        private static readonly Regex RowId = new("^[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant);

        private static readonly string[] ReadOnly = { "formula", "link-formula", "auto-number", "creator", "ctime", "mtime", "last-modifier", "button", "digital-sign" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "seatable";

        public string DisplayName => "SeaTable";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (the base's Advanced - API token)" };

        public string EndpointExample => "https://cloud.seatable.io";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://seatable.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://api.seatable.com/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Text(string name, string label, bool required, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField TableField = Text("table", "Table", true, "The table's name - Orders.");

        private static readonly ConnectorConfigField RowField = Text("row", "Row", true, "The row's id - its _id, 22 letters and digits.");

        private static readonly ConnectorConfigField FieldsField = Text("fields", "Fields", true, "A JSON object of column names and values - {\"Name\": \"Ada\", \"Qty\": 3, \"Kind\": \"Kaffe\"} - from the step's Query box. Over several lines from the step's Query box.", ConnectorConfigFieldType.Multiline);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField LinkColumnField = Text("column", "Link column", true, "The link column's name - Customer.");

        private static ConnectorOperation Op(string name, string label, string description, params ConnectorConfigField[] fields) => new(name, label, description) { Parameters = new(fields) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            Op("rows", "Get rows", "n8n's Row - Get Many: a table's rows, or a view's.", TableField, Text("view", "View", false, "A view's name - its rows in its order and filter."), MaxField),
            Op("row", "Get a row", "n8n's Row - Get.", TableField, RowField),
            Op("rows_search", "Search rows", "n8n's Row - Search: rows whose column holds the term - bound as a parameter, never spliced into the SQL.", TableField,
                Text("column", "Column", true, "The column to search - Name."), Text("term", "Search", true, "What it holds."),
                new ConnectorConfigField("exact", "Exactly", ConnectorConfigFieldType.Boolean, Required: false, Description: "The whole value, not a part of it - false by default."),
                new ConnectorConfigField("ignore_case", "Ignore case", ConnectorConfigFieldType.Boolean, Required: false, Description: "Upper and lower case alike - false by default."), MaxField),
            Op("row_create", "Create a row", "n8n's Row - Create: each field held to its column.", TableField, FieldsField),
            Op("row_update", "Update a row", "n8n's Row - Update: each field held to its column.", TableField, RowField, FieldsField),
            Op("row_delete", "Delete a row", "n8n's Row - Delete: says whether it deleted.", TableField, RowField),
            Op("row_lock", "Lock a row", "n8n's Row - Lock.", TableField, RowField),
            Op("row_unlock", "Unlock a row", "n8n's Row - Unlock.", TableField, RowField),
            Op("base_metadata", "Get the base's metadata", "n8n's Base - Metadata: its tables, columns and views."),
            Op("base_collaborators", "Get the base's collaborators", "n8n's Base - Collaborator: who works on it.", Text("search", "Search", false, "A part of a name or an e-mail.")),
            Op("base_snapshot", "Snapshot the base", "n8n's Base - Snapshot: refused, as SeaTable refuses one soon after another."),
            Op("links", "Get a row's links", "n8n's Link - List.", TableField, LinkColumnField, RowField),
            Op("link_add", "Link rows", "n8n's Link - Add: a row to rows of the linked table.", TableField, LinkColumnField, RowField, Text("other_rows", "Linked rows", true, "The linked table's row ids, by commas.")),
            Op("link_remove", "Unlink rows", "n8n's Link - Remove.", TableField, LinkColumnField, RowField, Text("other_rows", "Linked rows", true, "The linked table's row ids, by commas.")),
            Op("asset_link", "Get an asset's download link", "n8n's Asset - Get Public URL.", Text("path", "Path", true, "The asset's path in the base - /images/2026-10/receipt.png.")),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var name = await session.BaseNameAsync(cancellationToken);
                return ConnectionTestResult.Ok($"SeaTable answers: the token is the base {name}'s.");
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
            var operation = (context.OperationName ?? "rows").Trim().ToLowerInvariant();
            if (Operations.All(known => known.Name != operation))
            {
                throw new InvalidOperationException($"SeaTable has no operation '{context.OperationName}' - {string.Join(", ", Operations.Select(known => known.Name))}.");
            }

            var p = context.Parameters;
            var max = Values.Get(p, "max") is { } most
                ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).")
                : DefaultMax;
            switch (operation)
            {
                case "base_metadata":
                    {
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        foreach (var table in (await session.MetadataAsync(cancellationToken))["tables"] as JsonArray ?? new JsonArray())
                        {
                            if (table is JsonObject item)
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["name"] = Said(item["name"]), ["id"] = Said(item["_id"]),
                                    ["columns"] = new JsonArray((item["columns"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(column => (JsonNode?)new JsonObject { ["name"] = column["name"]?.DeepClone(), ["type"] = column["type"]?.DeepClone() }).ToArray()).ToJsonString(Wire),
                                    ["views"] = new JsonArray((item["views"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(view => view["name"]?.DeepClone()).ToArray()).ToJsonString(Wire),
                                });
                            }
                        }

                        yield break;
                    }

                case "base_collaborators":
                    {
                        var search = Values.Get(p, "search")?.Trim();
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var users = (await session.GatewayAsync(HttpMethod.Get, "related-users/", null, cancellationToken))?["user_list"] as JsonArray ?? new JsonArray();
                        foreach (var user in users.OfType<JsonObject>().Where(user => search is null || $"{Said(user["name"])} {Said(user["contact_email"])} {Said(user["email"])}".Contains(search, StringComparison.OrdinalIgnoreCase)).Take(max))
                        {
                            yield return Record(user);
                        }

                        yield break;
                    }

                case "base_snapshot":
                    {
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var answer = await session.GatewayAsync(HttpMethod.Post, "snapshot/", new JsonObject { ["dtable_name"] = "snapshot" }, cancellationToken);
                        var status = Said(answer?["status"]);
                        // SeaTable answers 200 with time_is_short when a snapshot was made too lately: n8n passed that on as done.
                        yield return status is null or "success" or "ok"
                            ? new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["snapshot"] = true, ["status"] = status })
                            : throw new InvalidOperationException($"SeaTable made no snapshot: {(status == "time_is_short" ? "the last one is too recent (time_is_short)" : status)}.");
                        yield break;
                    }

                case "asset_link":
                    {
                        var path = Values.Get(p, "path")?.Trim() is { Length: > 1 and <= 1024 } given && given.StartsWith('/') && !given.Any(char.IsControl) && !given.Contains("..", StringComparison.Ordinal)
                            ? given
                            : throw new InvalidOperationException("Give the asset's path in the base - /images/2026-10/receipt.png (path).");
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var answer = await session.SendAsync(HttpMethod.Get, "/api/v2.1/dtable/app-download-link/?path=" + Uri.EscapeDataString(path), null, session.ApiToken, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = path, ["download_link"] = Said(answer?["download_link"]) ?? throw new InvalidOperationException("SeaTable answered without the download link - check the address.") });
                        yield break;
                    }
            }

            var tableName = Values.Get(p, "table")?.Trim() is { Length: > 0 and <= 256 } named && !named.Any(char.IsControl) ? named : throw new InvalidOperationException("Give the table - its name, Orders (table).");
            using (var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout))
            {
                var table = await session.TableAsync(tableName, cancellationToken);
                var tableQuery = "table_name=" + Uri.EscapeDataString(table.Name);
                switch (operation)
                {
                    case "rows":
                        {
                            var view = Values.Get(p, "view")?.Trim();
                            if (view is not null && !table.Views.Contains(view))
                            {
                                throw new InvalidOperationException($"The table {table.Name} has no view '{view}' - it has {string.Join(", ", table.Views)} (view).");
                            }

                            var found = 0;
                            for (var start = 0; found < max; start += PageSize)
                            {
                                var rows = (await session.GatewayAsync(HttpMethod.Get, $"rows/?{tableQuery}{(view is null ? string.Empty : "&view_name=" + Uri.EscapeDataString(view))}&start={start}&limit={PageSize}&convert_keys=true", null, cancellationToken))?["rows"] as JsonArray
                                    ?? throw new InvalidOperationException("SeaTable answered with what is not a list of rows - check the address.");
                                foreach (var row in rows.OfType<JsonObject>().Take(max - found))
                                {
                                    found++;
                                    yield return Record(row);
                                }

                                if (rows.Count < PageSize)
                                {
                                    break;
                                }
                            }

                            break;
                        }

                    case "row":
                        {
                            yield return Record(await session.RowAsync(table, Row(p, "row"), cancellationToken));
                            break;
                        }

                    case "rows_search":
                        {
                            var column = table.Column(Values.Get(p, "column") ?? throw new InvalidOperationException("Give the column - its name, Name (column)."), "column");
                            var term = Values.Get(p, "term") is { Length: <= 1000 } given && !given.Any(char.IsControl) ? given : throw new InvalidOperationException("Give the search - one line (term).");
                            var exact = Flag(p, "exact") ?? false;
                            var ignoreCase = Flag(p, "ignore_case") ?? false;

                            // The term BOUND, never spliced: n8n's backslash escaping is no escaping to SeaTable's SQL. The names are
                            // the table's own, from its metadata, so quoting them is all they need.
                            var columnSql = ignoreCase ? $"lower({Quote(column.Name)})" : Quote(column.Name);
                            var bound = ignoreCase ? term.ToLowerInvariant() : term;
                            var sql = $"SELECT * FROM {Quote(table.Name)} WHERE {columnSql} {(exact ? "= ?" : "LIKE ?")} LIMIT {Math.Min(max, 10000)}";
                            var answer = await session.GatewayAsync(HttpMethod.Post, "sql/", new JsonObject { ["sql"] = sql, ["parameters"] = new JsonArray(JsonValue.Create(exact ? bound : "%" + bound + "%")), ["convert_keys"] = true }, cancellationToken);
                            foreach (var row in (answer?["results"] as JsonArray ?? throw new InvalidOperationException("SeaTable answered without its results - check the address.")).OfType<JsonObject>().Take(max))
                            {
                                yield return Record(row);
                            }

                            break;
                        }

                    case "row_create":
                    case "row_update":
                        {
                            var update = operation == "row_update";
                            var row = update ? Row(p, "row") : null;
                            var fields = Check(table, Values.Get(p, "fields") ?? Values.Get(p, "query") ?? throw new InvalidOperationException("Give the fields - a JSON object, {\"Name\": \"Ada\"} (fields)."));
                            if (update)
                            {
                                // A change of a row SeaTable has not got answers "base not found in big data storage": read it first.
                                await session.RowAsync(table, row!, cancellationToken);
                                await session.GatewayAsync(HttpMethod.Put, "rows/", new JsonObject { ["table_name"] = table.Name, ["updates"] = new JsonArray(new JsonObject { ["row_id"] = row, ["row"] = fields }) }, cancellationToken);
                            }
                            else
                            {
                                var made = await session.GatewayAsync(HttpMethod.Post, "rows/", new JsonObject { ["table_name"] = table.Name, ["rows"] = new JsonArray(fields) }, cancellationToken);
                                row = Said(made?["row_ids"]?[0]?["_id"]) ?? throw new InvalidOperationException("SeaTable answered without the row it made - check the address.");
                            }

                            yield return Record(await session.RowAsync(table, row!, cancellationToken)); // by its columns' names
                            break;
                        }

                    case "row_delete":
                        {
                            var row = Row(p, "row");
                            var answer = await session.GatewayAsync(HttpMethod.Delete, "rows/", new JsonObject { ["table_name"] = table.Name, ["row_ids"] = new JsonArray(JsonValue.Create(row)) }, cancellationToken);
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["_id"] = row, ["deleted"] = answer?["deleted_rows"]?.GetValue<int>() > 0 });
                            break;
                        }

                    case "row_lock":
                    case "row_unlock":
                        {
                            var row = Row(p, "row");
                            await session.RowAsync(table, row, cancellationToken);
                            await session.GatewayAsync(HttpMethod.Put, operation == "row_lock" ? "lock-rows/" : "unlock-rows/", new JsonObject { ["table_name"] = table.Name, ["row_ids"] = new JsonArray(JsonValue.Create(row)) }, cancellationToken);
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["_id"] = row, ["locked"] = operation == "row_lock" });
                            break;
                        }

                    default:
                        {
                            // A link column, its link and the linked table read from the metadata: n8n asked for SeaTable's ids.
                            var column = table.Column(Values.Get(p, "column") ?? throw new InvalidOperationException("Give the link column - its name, Customer (column)."), "column");
                            if (column.Type != "link" || column.Data?["link_id"] is null)
                            {
                                throw new InvalidOperationException($"{column.Name} is no link column - it is a {column.Type} (column).");
                            }

                            var row = Row(p, "row");
                            if (operation == "links")
                            {
                                var answer = await session.GatewayAsync(HttpMethod.Post, "query-links/", new JsonObject { ["table_id"] = table.Id, ["link_column_key"] = column.Key, ["rows"] = new JsonArray(new JsonObject { ["row_id"] = row, ["offset"] = 0, ["limit"] = Math.Min(max, 1000) }) }, cancellationToken);
                                foreach (var link in (answer?[row] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                                {
                                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["_id"] = Said(link["row_id"]), ["display_value"] = Said(link["display_value"]) ?? link["display_value"]?.ToJsonString(Wire) });
                                }

                                break;
                            }

                            var others = (Values.Get(p, "other_rows") ?? throw new InvalidOperationException("Give the linked rows - their ids, by commas (other_rows).")).Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).Select(part => RowId.IsMatch(part) ? part : throw new InvalidOperationException($"'{part}' is not a row's id - 22 letters and digits (other_rows).")).Distinct().ToArray();
                            if (others.Length is 0 or > 1000)
                            {
                                throw new InvalidOperationException("Give 1 to 1000 linked rows (other_rows).");
                            }

                            var otherTable = await session.TableByIdAsync(Said(column.Data["other_table_id"]) == table.Id ? Said(column.Data["table_id"]) : Said(column.Data["other_table_id"]), cancellationToken);
                            var body = new JsonObject { ["link_id"] = column.Data["link_id"]!.DeepClone(), ["table_name"] = table.Name, ["other_table_name"] = otherTable, ["other_rows_ids_map"] = new JsonObject { [row] = new JsonArray(others.Select(other => (JsonNode?)JsonValue.Create(other)).ToArray()) } };
                            var done = await session.GatewayAsync(operation == "link_add" ? HttpMethod.Post : HttpMethod.Delete, "links/", body, cancellationToken);
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["_id"] = row, ["other_rows"] = string.Join(",", others), [operation == "link_add" ? "linked" : "unlinked"] = operation == "link_add" ? done?["success"]?.GetValue<bool>() == true : done?["deleted_links_count"]?.GetValue<int>() > 0,
                            });
                            break;
                        }
                }
            }
        }

        // The fields as a JSON object, each a column the table has, its value of the column's kind: SeaTable empties a value its
        // column does not hold, drops a field it has not got and adds a select's option it does not offer, all without a word.
        private static JsonObject Check(Table table, string given)
        {
            JsonObject fields;
            try
            {
                fields = (given.Length <= MostText ? JsonNode.Parse(given) : null) as JsonObject ?? throw new InvalidOperationException("The fields are a JSON object - {\"Name\": \"Ada\"} (fields).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The fields are not JSON - {\"Name\": \"Ada\"} (fields).");
            }

            if (fields.Count == 0)
            {
                throw new InvalidOperationException("Give at least one field (fields).");
            }

            var body = new JsonObject();
            foreach (var (name, value) in fields)
            {
                var column = table.Column(name, "fields");
                if (ReadOnly.Contains(column.Type))
                {
                    throw new InvalidOperationException($"{column.Name} is a {column.Type} column - SeaTable fills it itself (fields).");
                }

                body[column.Name] = value is null ? null : column.Type switch
                {
                    "number" or "rate" or "duration" => value is JsonValue number && (number.TryGetValue<double>(out var amount) || (number.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))) && double.IsFinite(amount)
                        ? JsonValue.Create(amount)
                        : throw new InvalidOperationException($"{column.Name} is a number column - '{value.ToJsonString(Wire)}' is no number (fields)."),
                    "checkbox" => value is JsonValue flag && (flag.TryGetValue<bool>(out var yes) || (flag.TryGetValue<string>(out var said) && bool.TryParse(said, out yes)))
                        ? JsonValue.Create(yes)
                        : throw new InvalidOperationException($"{column.Name} is a checkbox - true or false (fields)."),
                    "date" => value is JsonValue day && day.TryGetValue<string>(out var date) && (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || DateTime.TryParseExact(date, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        ? JsonValue.Create(date)
                        : throw new InvalidOperationException($"{column.Name} is a date column - 2026-10-21, or 2026-10-21 08:00 (fields)."),
                    "single-select" => value is JsonValue choice && choice.TryGetValue<string>(out var option) && column.Options.Contains(option)
                        ? JsonValue.Create(option)
                        : throw new InvalidOperationException($"{column.Name} offers {string.Join(", ", column.Options.Select(item => '"' + item + '"'))} - SeaTable would add another option to the column (fields)."),
                    "multiple-select" => value is JsonArray options && options.All(item => item is JsonValue each && each.TryGetValue<string>(out var picked) && column.Options.Contains(picked))
                        ? options.DeepClone()
                        : throw new InvalidOperationException($"{column.Name} is a list of {string.Join(", ", column.Options.Select(item => '"' + item + '"'))} - SeaTable would add another option to the column (fields)."),
                    "link" => throw new InvalidOperationException($"{column.Name} is a link column - link rows with link_add (fields)."),
                    "file" or "image" => throw new InvalidOperationException($"{column.Name} is a {column.Type} column - an asset is uploaded to the base, not written here (fields)."),
                    "collaborator" => value is JsonArray people && people.All(item => item is JsonValue each && each.TryGetValue<string>(out var person) && person.Contains('@'))
                        ? people.DeepClone()
                        : throw new InvalidOperationException($"{column.Name} is a list of collaborators' e-mail addresses (fields)."),
                    _ => value is JsonValue plain && (!plain.TryGetValue<string>(out var words) || words.Length <= MostText)
                        ? plain.DeepClone()
                        : throw new InvalidOperationException($"{column.Name} is a {column.Type} column - give it a value, not a list or an object (fields)."),
                };
            }

            return body;
        }

        private static string Quote(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

        private static string Row(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name)?.Trim() is { } given ? RowId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a row's id - 22 letters and digits ({name}).") : throw new InvalidOperationException($"Give the row - its id ({name}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Said(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed record Column(string Name, string Key, string Type, JsonObject? Data, string[] Options);

        private sealed record Table(string Name, string Id, Column[] Columns, string[] Views)
        {
            public Column Column(string given, string where)
            {
                var name = given.Trim();
                return Columns.FirstOrDefault(column => column.Name == name)
                    ?? throw new InvalidOperationException($"The table {Name} has no column '{given}'{(Columns.Where(column => column.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(column.Name, StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).Take(5).ToArray() is { Length: > 0 } near ? " - " + string.Join(", ", near) + "?" : string.Empty)} ({where}).");
            }
        }

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string root;
            private JsonObject? access;
            private JsonObject? metadata;

            private Session(HttpClient client, string root, string apiToken)
            {
                this.client = client;
                this.root = root;
                ApiToken = apiToken;
            }

            public string ApiToken { get; }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give SeaTable's address - https://cloud.seatable.io, or your own.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("" or "/"))
                {
                    throw new InvalidOperationException($"'{endpoint}' is not SeaTable's address - https://cloud.seatable.io, its server alone.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The SeaTable connection has no API token.");
                return new Session(egress.CreateHttpClient(timeout), root.TrimEnd('/'), token);
            }

            public void Dispose() => client.Dispose();

            // The base's access token for the API token, once a session; its addresses stay the connection's own.
            private async Task<JsonObject> AccessAsync(CancellationToken cancellationToken) =>
                access ??= await SendAsync(HttpMethod.Get, "/api/v2.1/dtable/app-access-token/", null, ApiToken, cancellationToken) is JsonObject answer && Said(answer["access_token"]) is not null && Said(answer["dtable_uuid"]) is not null
                    ? answer
                    : throw new InvalidOperationException("SeaTable answered without the base's access token - check the address.");

            public async Task<string> BaseNameAsync(CancellationToken cancellationToken) => Said((await AccessAsync(cancellationToken))["dtable_name"]) ?? "it is for";

            public async Task<JsonNode?> GatewayAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var base_ = await AccessAsync(cancellationToken);
                return await SendAsync(method, $"/api-gateway/api/v2/dtables/{Uri.EscapeDataString(Said(base_["dtable_uuid"])!)}/{route}", body, Said(base_["access_token"])!, cancellationToken);
            }

            public async Task<JsonObject> MetadataAsync(CancellationToken cancellationToken) =>
                metadata ??= (await GatewayAsync(HttpMethod.Get, "metadata/", null, cancellationToken))?["metadata"] as JsonObject
                    ?? throw new InvalidOperationException("SeaTable answered without the base's metadata - check the address.");

            public async Task<Table> TableAsync(string name, CancellationToken cancellationToken)
            {
                var tables = ((await MetadataAsync(cancellationToken))["tables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
                var table = tables.FirstOrDefault(item => Said(item["name"]) == name)
                    ?? throw new InvalidOperationException($"The base has no table '{name}' - it has {string.Join(", ", tables.Select(item => Said(item["name"])))} (table).");
                return new Table(name, Said(table["_id"]) ?? string.Empty,
                    (table["columns"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(column => new Column(Said(column["name"]) ?? string.Empty, Said(column["key"]) ?? string.Empty, Said(column["type"]) ?? "text", column["data"] as JsonObject,
                        (column["data"]?["options"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(option => Said(option["name"]) ?? string.Empty).ToArray())).ToArray(),
                    (table["views"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(view => Said(view["name"]) ?? string.Empty).ToArray());
            }

            public async Task<string> TableByIdAsync(string? id, CancellationToken cancellationToken) =>
                ((await MetadataAsync(cancellationToken))["tables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(item => Said(item["_id"]) == id).Select(item => Said(item["name"])).FirstOrDefault()
                    ?? throw new InvalidOperationException("The link column's other table is not in the base's metadata.");

            public async Task<JsonObject> RowAsync(Table table, string row, CancellationToken cancellationToken)
            {
                try
                {
                    return await GatewayAsync(HttpMethod.Get, $"rows/{Uri.EscapeDataString(row)}/?table_name={Uri.EscapeDataString(table.Name)}&convert_keys=true", null, cancellationToken) as JsonObject
                        ?? throw new InvalidOperationException("SeaTable answered without the row - check the address.");
                }
                catch (SeaTableAnswer ex) when (ex.Status == 404)
                {
                    throw new InvalidOperationException($"The table {table.Name} has no row {row} (row).");
                }
            }

            /// <summary>A call with a token as SeaTable's header; its JSON, or its status and message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, string token, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, root + route);
                    request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("SeaTable answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var said = Said(json?["error_message"]) ?? Said(json?["error_msg"]) ?? Said(json?["detail"]) ?? string.Empty;
                said = said.Length > 300 ? said[..300] : said;
                throw new SeaTableAnswer(status, $"SeaTable answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status is 401 or 403 && said.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? " - check the API token." : string.Empty));
            }
        }

        private sealed class SeaTableAnswer(int status, string message) : InvalidOperationException(message)
        {
            public int Status { get; } = status;
        }
    }
}
