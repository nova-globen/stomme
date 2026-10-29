#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Supabase
{
    /// <summary>
    /// Supabase (n8n's Supabase node: a table's rows read, got, created, updated and deleted): a project's REST API
    /// (<c>https://your-project.supabase.co/rest/v1</c>, PostgREST), its secret key in Supabase's <c>apikey</c> header - and as a
    /// bearer too when it is one of the older JWT keys (<c>service_role</c>), the newer <c>sb_secret_</c> keys going in
    /// <c>apikey</c> alone. A test reads the project's API description and counts its tables.
    /// </summary>
    /// <remarks>
    /// A table and a column are named as Postgres names them (letters, digits and _), each held to that shape before it reaches the
    /// address. <c>filters</c>, one per line, are <c>column operator value</c> - eq, neq, gt, gte, lt, lte, like, ilike, is (null,
    /// true, false), in (comma-separated) and the full-text fts, plfts, phfts and wfts - every one of them, or any one when
    /// <c>match</c> says so; <c>query</c> takes PostgREST's own syntax as written beside them. <c>rows</c>: a table's rows the filters
    /// keep, the <c>columns</c> named (or every one), in the <c>order</c> asked (<c>created_at.desc</c>), page by page by offset up to
    /// <c>max</c> (1,000); one record each, a number, a yes/no and a text as themselves, a list or an object as JSON. <c>get_row</c>
    /// the first. <c>create_rows</c>: a row or a list of rows as JSON - the rows Postgres stored, with their generated columns.
    /// <c>update_rows</c> sets the <c>fields</c> given on the rows the filters keep, and <c>delete_rows</c> removes them - each
    /// answering the rows it changed, and each refused without a filter, so a mistake cannot touch a whole table. A failure is
    /// thrown with its status, Postgres's code and its message, its details and hint beside it.
    /// </remarks>
    public sealed class SupabaseConnector : IConnector, IConnectorExtractor
    {
        public const string RowsOperation = "rows";

        public const string GetRowOperation = "get_row";

        public const string CreateRowsOperation = "create_rows";

        public const string UpdateRowsOperation = "update_rows";

        public const string DeleteRowsOperation = "delete_rows";

        public const int DefaultMax = 1000;

        private const int PageSize = 1000;

        private static readonly Regex Name = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "eq", "neq", "gt", "gte", "lt", "lte", "like", "ilike", "is", "in", "fts", "plfts", "phfts", "wfts" };

        public string ConnectorType => "supabase";

        public string DisplayName => "Supabase";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The key as Supabase names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Secret key (sb_secret_... or the service_role key)" };

        public string EndpointExample => "https://your-project.supabase.co";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://supabase.com");

        public Uri ApiDocumentationUrl { get; } = new("https://supabase.com/docs/guides/api");

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - orders.");

        private static ConnectorConfigField FiltersField(bool required) => new("filters", "Filters", ConnectorConfigFieldType.Multiline, Required: required,
            Description: "One per line: column operator value - status eq paid, total gte 100, email ilike %@acme.com, id in 1,2,3, deleted_at is null.");

        private static readonly ConnectorConfigField MatchField = new("match", "Match", ConnectorConfigFieldType.Text, Required: false, Description: "all (every filter) or any (one of them). Empty: all.");

        private static readonly ConnectorConfigField QueryField = new("query", "PostgREST query", ConnectorConfigFieldType.Text, Required: false, Description: "PostgREST's own filters, as written - status=eq.paid&total=gte.100.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RowsOperation, "Get rows", "A table's rows the filters keep - one record each - n8n's row Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField, FiltersField(required: false), MatchField, QueryField,
                    new ConnectorConfigField("columns", "Columns", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated. Empty: every one."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "column.asc or column.desc, comma-separated - created_at.desc."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetRowOperation, "Get a row", "The first row the filters keep - n8n's row Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, FiltersField(required: true), MatchField, QueryField }),
            },
            new ConnectorOperation(CreateRowsOperation, "Create rows", "Adds a row or rows - the rows stored - n8n's row Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField,
                    new ConnectorConfigField("rows", "Rows", ConnectorConfigFieldType.Multiline, Required: true, Description: "JSON - {\"email\": \"ada@example.com\"}, or a list of them."),
                }),
            },
            new ConnectorOperation(UpdateRowsOperation, "Update rows", "Sets fields on the rows the filters keep - the rows changed - n8n's row Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField, FiltersField(required: true), MatchField, QueryField,
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: "JSON - {\"status\": \"shipped\"}."),
                }),
            },
            new ConnectorOperation(DeleteRowsOperation, "Delete rows", "Removes the rows the filters keep - the rows removed - n8n's row Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, FiltersField(required: true), MatchField, QueryField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (description, _) = await SendAsync(client, Key(context.Credentials), HttpMethod.Get, api + "/", null, cancellationToken);
                var tables = (description?["paths"] as JsonObject)?.Count(path => path.Key != "/" && !path.Key.StartsWith("/rpc/", StringComparison.Ordinal)) ?? 0;
                return ConnectionTestResult.Ok($"Reached {new Uri(api).Host}: {tables} table{(tables == 1 ? "" : "s")} and view{(tables == 1 ? "" : "s")}.");
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
            var api = Api(context.Endpoint);
            var key = Key(context.Credentials);
            var operation = context.OperationName ?? RowsOperation;
            var parameters = context.Parameters;

            // What could be refused is refused before anything is sent.
            var table = Column(Values.Get(parameters, "table"), "table");
            var address = $"{api}/{table}";
            if (Is(operation, CreateRowsOperation))
            {
                using var creating = context.Egress.CreateHttpClient(context.Timeout);
                var (made, _) = await SendAsync(creating, key, HttpMethod.Post, address, Rows(Values.Get(parameters, "rows")), cancellationToken);
                foreach (var row in (made as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return Record(row);
                }

                yield break;
            }

            if (!Is(operation, RowsOperation) && !Is(operation, GetRowOperation) && !Is(operation, UpdateRowsOperation) && !Is(operation, DeleteRowsOperation))
            {
                throw new InvalidOperationException($"Supabase has no operation '{operation}' - rows, get_row, create_rows, update_rows or delete_rows.");
            }

            var filters = Filters(parameters);
            if (filters.Count == 0 && !Is(operation, RowsOperation))
            {
                throw new InvalidOperationException("Give at least one filter - a get, an update or a delete names the rows it touches.");
            }

            var query = new List<string>(filters);
            if (Is(operation, RowsOperation))
            {
                if (Values.Get(parameters, "columns") is { } columns)
                {
                    query.Add("select=" + string.Join(",", columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(column => Column(column, "column"))));
                }

                if (Values.Get(parameters, "order") is { } order)
                {
                    query.Add("order=" + string.Join(",", order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Order)));
                }
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, UpdateRowsOperation) || Is(operation, DeleteRowsOperation))
            {
                var update = Is(operation, UpdateRowsOperation);
                var (changed, _) = await SendAsync(client, key, update ? HttpMethod.Patch : HttpMethod.Delete, address + "?" + string.Join("&", query), update ? Fields(Values.Get(parameters, "fields")) : null, cancellationToken);
                foreach (var row in (changed as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return Record(row);
                }

                yield break;
            }

            var max = Is(operation, GetRowOperation) ? 1 : int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            var read = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = Math.Min(PageSize, max - read);
                var page = new List<string>(query) { "limit=" + size.ToString(CultureInfo.InvariantCulture), "offset=" + read.ToString(CultureInfo.InvariantCulture) };
                var (rows, _) = await SendAsync(client, key, HttpMethod.Get, address + "?" + string.Join("&", page), null, cancellationToken);
                var found = (rows as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var row in found)
                {
                    read++;
                    yield return Record(row);
                    if (read >= max)
                    {
                        yield break;
                    }
                }

                if (found.Count < size)
                {
                    yield break;
                }
            }
        }

        // The filters as PostgREST takes them: each line column=operator.value, or all of them inside or=(...) when any one is
        // enough; PostgREST's own query as written beside them.
        private static List<string> Filters(IReadOnlyDictionary<string, string> parameters)
        {
            var any = Values.Get(parameters, "match")?.Trim().ToLowerInvariant() switch
            {
                null or "" or "all" => false,
                "any" => true,
                var other => throw new InvalidOperationException($"'{other}' is not a match - all or any."),
            };
            var conditions = new List<(string Column, string Operator, string Value)>();
            foreach (var line in (Values.Get(parameters, "filters") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    throw new InvalidOperationException($"'{line}' is not a filter - column operator value (status eq paid).");
                }

                var op = parts[1].ToLowerInvariant();
                if (!Operators.Contains(op))
                {
                    throw new InvalidOperationException($"'{parts[1]}' is not an operator - {string.Join(", ", Operators)}.");
                }

                var value = parts[2].Trim();
                if (op == "is" && value.ToLowerInvariant() is not ("null" or "true" or "false" or "unknown"))
                {
                    throw new InvalidOperationException($"'{value}' is not what is compares with - null, true, false or unknown.");
                }

                conditions.Add((Column(parts[0], "column"), op, op == "is" ? value.ToLowerInvariant() : value));
            }

            var query = new List<string>();
            if (any && conditions.Count > 1)
            {
                query.Add("or=" + Uri.EscapeDataString("(" + string.Join(",", conditions.Select(condition => $"{condition.Column}.{condition.Operator}.{Quoted(condition)}")) + ")"));
            }
            else
            {
                query.AddRange(conditions.Select(condition => condition.Column + "=" + Uri.EscapeDataString(condition.Operator + "." + Listed(condition))));
            }

            if (Values.Get(parameters, "query") is { } written)
            {
                var raw = written.Trim().TrimStart('?', '&');
                if (raw.Any(c => char.IsWhiteSpace(c) || c == '#'))
                {
                    throw new InvalidOperationException("The PostgREST query has a space or a # - write it as PostgREST takes it, status=eq.paid&total=gte.100.");
                }

                query.Add(raw);
            }

            return query;
        }

        // A value inside PostgREST's in=(...) or or=(...): a list's items each quoted, any other value quoted when it holds what
        // PostgREST reads as syntax.
        private static string Listed((string Column, string Operator, string Value) condition) =>
            condition.Operator == "in" ? "(" + string.Join(",", condition.Value.Split(',', StringSplitOptions.TrimEntries).Select(Quote)) + ")" : condition.Value;

        private static string Quoted((string Column, string Operator, string Value) condition) =>
            condition.Operator == "in" ? Listed(condition) : condition.Value.IndexOfAny(new[] { ',', '(', ')', '"', '\\', '.', ':' }) >= 0 ? Quote(condition.Value) : condition.Value;

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        private static string Order(string given)
        {
            var parts = given.Split('.');
            return parts.Length switch
            {
                1 => Column(parts[0], "column"),
                2 when parts[1] is "asc" or "desc" => Column(parts[0], "column") + "." + parts[1],
                3 when parts[1] is "asc" or "desc" && parts[2] is "nullsfirst" or "nullslast" => Column(parts[0], "column") + "." + parts[1] + "." + parts[2],
                _ => throw new InvalidOperationException($"'{given}' is not an order - column.asc or column.desc."),
            };
        }

        // A row, or a list of rows, as JSON objects whose keys are columns.
        private static JsonNode Rows(string? given)
        {
            var parsed = Parse(given, "rows", "{\"email\": \"ada@example.com\"}");
            var rows = parsed switch
            {
                JsonObject single => new List<JsonObject> { single },
                JsonArray list when list.Count > 0 && list.All(item => item is JsonObject) => list.OfType<JsonObject>().ToList(),
                _ => throw new InvalidOperationException("The rows are not a JSON object or a list of them."),
            };
            foreach (var row in rows)
            {
                Keys(row);
            }

            return parsed;
        }

        private static JsonObject Fields(string? given)
        {
            var fields = Parse(given, "fields", "{\"status\": \"shipped\"}") as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object.");
            if (fields.Count == 0)
            {
                throw new InvalidOperationException("Give at least one field.");
            }

            Keys(fields);
            return fields;
        }

        private static void Keys(JsonObject row)
        {
            foreach (var (name, _) in row)
            {
                Column(name, "column");
            }
        }

        private static JsonNode Parse(string? given, string what, string example)
        {
            try
            {
                return JsonNode.Parse(given ?? throw new InvalidOperationException($"Give the {what} - JSON, {example}.")) ?? throw new InvalidOperationException($"Give the {what} - JSON, {example}.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {what} are not JSON - {example}.");
            }
        }

        // A row as Postgres stored it: each column by its name, a number, a yes/no and a text as themselves, a list or an object as
        // JSON.
        private static ConnectorRecord Record(JsonObject row)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in row)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var words) => words,
                    JsonValue whole when whole.TryGetValue<long>(out var number) => number,
                    JsonValue real when real.TryGetValue<double>(out var fraction) => fraction,
                    JsonValue flag when flag.TryGetValue<bool>(out var yes) => yes,
                    _ => value.ToJsonString(),
                };
            }

            return new ConnectorRecord(fields);
        }

        // The project's REST API: its own address, /rest/v1.
        private static string Api(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var project) || (project.Scheme != Uri.UriSchemeHttps && project.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Supabase project's address - https://your-project.supabase.co.");
            }

            return project.GetLeftPart(UriPartial.Authority) + "/rest/v1";
        }

        private static string Column(string? given, string what) =>
            given is not null && Name.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s name (letters, digits and _).");

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var key) && !string.IsNullOrWhiteSpace(key) ? key.Trim() : throw new InvalidOperationException("The Supabase connection has no key - a secret key.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Supabase's answer as JSON, the key in its apikey header (and as a bearer when it is a JWT), the rows asked back; a failure
        // thrown with its status, Postgres's code and message, its details and hint beside it.
        private static async Task<(JsonNode? Json, int Status)> SendAsync(HttpClient client, string key, HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Add("apikey", key);
                if (key.StartsWith("eyJ", StringComparison.Ordinal))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                if (method != HttpMethod.Get)
                {
                    request.Headers.Add("Prefer", "return=representation");
                }

                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Supabase answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["code"]);
                var words = string.Join(" - ", new[] { Text(json?["message"]) ?? Text(json?["msg"]), Text(json?["details"]), Text(json?["hint"]) }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return (json, (int)response.StatusCode);
        }
    }
}
