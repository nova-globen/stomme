#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace Stomme.Modules.Connectors.GoogleBigQuery
{
    /// <summary>
    /// Google BigQuery (n8n's Google BigQuery node: a query run, rows inserted): the BigQuery API v2 with a Google Cloud
    /// OAuth client's id and secret and a refresh token granted the BigQuery scope, exchanged at Google's token endpoint through
    /// the family's token provider, over the host's egress. A test signs in and names what the connection can see.
    /// </summary>
    /// <remarks>
    /// <c>query</c> - GoogleSQL (or legacy SQL when asked) in a project, a default dataset and a location when given, named
    /// parameters as text; the job waited for - BigQuery holding each call up to ten seconds - no longer than <c>wait</c> (60
    /// seconds; a job still running then a failure naming it); its rows read page by page by BigQuery's page token up to
    /// <c>max</c> (1,000), each value typed by the result's schema: an integer a number when it fits, a float a number, a
    /// boolean a flag, a timestamp its UTC moment, a numeric kept as text (it has more digits than a number holds), a record or
    /// a repeated field its JSON. <c>insert_rows</c> - a JSON object or a list of them streamed into a table in batches of 500,
    /// each row with an insert id of its own so a retried request is not written twice; a row BigQuery refuses a failure
    /// naming the row and why, and how many were written before it - unless invalid rows are to be skipped, then counted.
    /// Every project, dataset, table, flag and number held to its shape before a token is asked for. A throttled call waits as
    /// Google says; a failure is thrown with its status and Google's words.
    /// </remarks>
    public sealed class GoogleBigQueryConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://bigquery.googleapis.com/bigquery/v2";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string QueryOperation = "query";

        public const string InsertRowsOperation = "insert_rows";

        public const int DefaultMax = 1000;

        public const int DefaultWait = 60;

        private const int PageSize = 1000;

        private const int Batch = 500;

        private const int MostRows = 10000;

        private static readonly string[] Known = { QueryOperation, InsertRowsOperation };

        private static readonly Regex Project = new("^[a-z0-9][a-z0-9.:\\-]{0,98}[a-z0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex Dataset = new("^[A-Za-z0-9_]{1,1024}$", RegexOptions.CultureInvariant);

        private static readonly Regex Table = new("^[\\p{L}\\p{M}\\p{N}_\\- ]{1,1024}$", RegexOptions.CultureInvariant);

        private static readonly Regex Location = new("^[A-Za-z0-9\\-]{2,40}$", RegexOptions.CultureInvariant);

        private static readonly Regex Parameter = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlebigquery";

        public string DisplayName => "Google BigQuery";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "Its client secret",
            ["refresh-token"] = "Refresh token granted the BigQuery scope (https://www.googleapis.com/auth/bigquery)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://cloud.google.com/bigquery");

        public Uri ApiDocumentationUrl { get; } = new("https://cloud.google.com/bigquery/docs/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project a step names none of - its id, kafe-analytics."),
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ProjectField = Field("project", "Project", "Its id - the connection's when left empty.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(QueryOperation, "Run a query", "GoogleSQL, its rows as records - n8n's Google BigQuery Execute Query.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    Field("query", "Query", "GoogleSQL in the step's Query box - SELECT name, total FROM `sales.orders` WHERE day = @day; each value written @name and given as an '@name = value' parameter: bound, never spliced in.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("dataset", "Default dataset", "The dataset a table the query names alone is in."),
                    Field("location", "Location", "Where the data is - EU, US, europe-north1."),
                    Field("legacy_sql", "Legacy SQL", "true: the query is legacy SQL.", ConnectorConfigFieldType.Boolean),
                    Field("max", "At most", "How many rows, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    Field("wait", "Wait", "How many seconds to wait for the query, at most 600.", ConnectorConfigFieldType.Integer) with { Default = DefaultWait.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(InsertRowsOperation, "Insert rows", "Streamed into a table - n8n's Google BigQuery Insert.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ProjectField,
                    Field("dataset", "Dataset", "Its id - sales.", required: true),
                    Field("table", "Table", "Its id - orders.", required: true),
                    Field("rows", "Rows", "A JSON object or a list of them, by column - {\"name\": \"Kafé AB\", \"total\": 129.5}.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("skip_invalid", "Skip invalid rows", "true: the valid rows are written and the invalid ones counted; otherwise one invalid row is a failure.", ConnectorConfigFieldType.Boolean),
                    Field("ignore_unknown", "Ignore unknown columns", "true: a column the table does not have is left out.", ConnectorConfigFieldType.Boolean),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var project = Values.Get(context.Settings, "project") is { } named ? ProjectOf(named) : null;
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                if (project is null)
                {
                    var projects = (await SendAsync(client, token, HttpMethod.Get, api + "/projects?maxResults=1", null, cancellationToken))?["projects"] as JsonArray;
                    return ConnectionTestResult.Ok(projects is { Count: > 0 } ? $"Google BigQuery: signed in - {Words(projects[0]?["id"]) ?? "a project"} among the projects it can see." : "Google BigQuery: signed in - no project it can see yet.");
                }

                var datasets = (await SendAsync(client, token, HttpMethod.Get, $"{api}/projects/{Uri.EscapeDataString(project)}/datasets?maxResults=1", null, cancellationToken))?["datasets"] as JsonArray;
                return ConnectionTestResult.Ok(datasets is { Count: > 0 } ? $"Google BigQuery: signed in to {project} - {Words(datasets[0]?["datasetReference"]?["datasetId"]) ?? "a dataset"} among its datasets." : $"Google BigQuery: signed in to {project} - no dataset yet.");
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
                ?? throw new InvalidOperationException($"Google BigQuery has no operation '{context.OperationName}' - {QueryOperation} or {InsertRowsOperation}.");
            var p = context.Parameters;
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);

            // Everything held to its shape before a token is asked for.
            var project = ProjectOf(Values.Get(p, "project") ?? Values.Get(context.Settings, "project") ?? throw new InvalidOperationException("Give the project - its id (project), or the connection's."));
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            int NumberOf(string name, int fallback, int most) => Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= most ? n : throw new InvalidOperationException($"'{given}' is not a number from 1 to {most.ToString("N0", CultureInfo.InvariantCulture)} ({name}).")
                : fallback;
            string DatasetOf(bool required) => Values.Get(p, "dataset") is { } given ? Dataset.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a dataset's id - letters, digits and _ (dataset).") : required ? throw new InvalidOperationException("Give the dataset - its id (dataset).") : "";

            if (operation == InsertRowsOperation)
            {
                var dataset = DatasetOf(required: true);
                var table = Values.Get(p, "table") is { } named ? Table.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's id - letters, digits, _, - and spaces (table).") : throw new InvalidOperationException("Give the table - its id (table).");
                var rows = RowsOf(Values.Get(p, "rows"));
                var skip = FlagOf("skip_invalid") ?? false;
                var ignore = FlagOf("ignore_unknown") ?? false;

                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var address = $"{api}/projects/{Uri.EscapeDataString(project)}/datasets/{Uri.EscapeDataString(dataset)}/tables/{Uri.EscapeDataString(table)}/insertAll";
                var written = 0;
                var skipped = 0;
                for (var at = 0; at < rows.Count; at += Batch)
                {
                    var batch = rows.Skip(at).Take(Batch).ToList();
                    var body = new JsonObject
                    {
                        ["skipInvalidRows"] = skip,
                        ["ignoreUnknownValues"] = ignore,
                        ["rows"] = new JsonArray(batch.Select(row => (JsonNode)new JsonObject { ["insertId"] = Guid.NewGuid().ToString("N"), ["json"] = row.DeepClone() }).ToArray()),
                    };
                    var answer = await SendAsync(client, token, HttpMethod.Post, address, body, cancellationToken);
                    var refused = (answer?["insertErrors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    if (refused.Count > 0 && !skip)
                    {
                        var reasons = refused.Where(error => (error["errors"] as JsonArray)?.OfType<JsonObject>().Any(reason => Words(reason["reason"]) != "stopped") ?? false)
                            .Select(error => $"row {(error["index"] is JsonValue index && index.TryGetValue<int>(out var i) ? (at + i + 1).ToString(CultureInfo.InvariantCulture) : "?")} ({string.Join("; ", (error["errors"] as JsonArray)!.OfType<JsonObject>().Where(reason => Words(reason["reason"]) != "stopped").Select(reason => string.Join(": ", new[] { Words(reason["location"]), Words(reason["message"]) ?? Words(reason["reason"]) }.Where(part => !string.IsNullOrEmpty(part)))))})").ToList();
                        var said = string.Join(", ", reasons);
                        throw new InvalidOperationException($"BigQuery refused {said[..Math.Min(said.Length, 300)]} - none of rows {at + 1} to {at + batch.Count} was written{(written > 0 ? $", {written} before them were" : "")}.");
                    }

                    skipped += refused.Count;
                    written += batch.Count - refused.Count;
                }

                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["inserted"] = (long)written, ["skipped"] = (long)skipped, ["table"] = $"{project}.{dataset}.{table}" });
                yield break;
            }

            var sql = Values.Get(p, "query") ?? throw new InvalidOperationException("Give the query (query).");
            var defaultDataset = DatasetOf(required: false);
            var location = Values.Get(p, "location") is { } where ? Location.IsMatch(where) ? where : throw new InvalidOperationException($"'{where}' is not a location - EU, US, europe-north1 (location).") : null;
            var legacy = FlagOf("legacy_sql") ?? false;
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var wait = NumberOf("wait", DefaultWait, 600);
            var parameters = ParametersOf(p);
            if (legacy && parameters.Count > 0)
            {
                throw new InvalidOperationException("Legacy SQL takes no named parameters - leave legacy_sql out, or the @ parameters.");
            }

            var query = new JsonObject { ["query"] = sql, ["useLegacySql"] = legacy, ["maxResults"] = Math.Min(PageSize, max), ["timeoutMs"] = 10000 };
            if (defaultDataset.Length > 0)
            {
                query["defaultDataset"] = new JsonObject { ["projectId"] = project, ["datasetId"] = defaultDataset };
            }

            if (location is not null)
            {
                query["location"] = location;
            }

            if (parameters.Count > 0)
            {
                query["parameterMode"] = "NAMED";
                query["queryParameters"] = new JsonArray(parameters.Select(parameter => (JsonNode)new JsonObject
                {
                    ["name"] = parameter.Key,
                    ["parameterType"] = new JsonObject { ["type"] = "STRING" },
                    ["parameterValue"] = new JsonObject { ["value"] = parameter.Value },
                }).ToArray());
            }

            {
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var projectPath = $"{api}/projects/{Uri.EscapeDataString(project)}";
                var answer = await SendAsync(client, token, HttpMethod.Post, projectPath + "/queries", query, cancellationToken) as JsonObject ?? throw new InvalidOperationException("BigQuery answered the query with nothing.");
                var job = Words(answer["jobReference"]?["jobId"]) ?? throw new InvalidOperationException("BigQuery answered the query with no job.");
                var jobLocation = Words(answer["jobReference"]?["location"]) ?? location;
                string Results(string? pageToken) => $"{projectPath}/queries/{Uri.EscapeDataString(job)}?timeoutMs=10000&maxResults={Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture)}"
                    + (jobLocation is null ? "" : "&location=" + Uri.EscapeDataString(jobLocation)) + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));

                // The job waited for: BigQuery holds each call up to ten seconds; no longer than the step's wait in all.
                var clock = Stopwatch.StartNew();
                while (answer["jobComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var done) && !done)
                {
                    if (clock.Elapsed.TotalSeconds >= wait)
                    {
                        throw new InvalidOperationException($"BigQuery had not finished the query in {wait} seconds - its job {job} goes on: give it longer (wait).");
                    }

                    answer = await SendAsync(client, token, HttpMethod.Get, Results(null), null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("BigQuery answered with nothing.");
                }

                var fields = answer["schema"]?["fields"] as JsonArray ?? new JsonArray();
                var read = 0;
                while (true)
                {
                    foreach (var row in (answer["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return new ConnectorRecord(Row(fields, row));
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    if (Words(answer["pageToken"]) is not { } next)
                    {
                        yield break;
                    }

                    answer = await SendAsync(client, token, HttpMethod.Get, Results(next), null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("BigQuery answered with nothing.");
                }
            }
        }

        private static string ProjectOf(string given) =>
            Project.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a project's id - kafe-analytics (project).");

        // The rows to insert: a JSON object or a list of them, 10,000 at most.
        private static List<JsonObject> RowsOf(string? given)
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
                _ => throw new InvalidOperationException("The rows are not a JSON object or a list of them - {\"name\": \"Kafé AB\"} (rows)."),
            };
            return rows.Count is > 0 and <= MostRows ? rows : throw new InvalidOperationException($"Give from 1 to {MostRows.ToString("N0", CultureInfo.InvariantCulture)} rows (rows).");
        }

        // Named parameters: the step's '@name = value' lines, each value bound as text - never spliced into the query.
        private static Dictionary<string, string> ParametersOf(IReadOnlyDictionary<string, string> given)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in given.Where(pair => pair.Key.StartsWith('@')))
            {
                parameters[Parameter.IsMatch(key[1..]) ? key[1..] : throw new InvalidOperationException($"'{key}' is not a parameter's name - @ and letters, digits and _ (parameters).")] = value;
            }

            return parameters;
        }

        // A result row: each cell typed by its field.
        private static Dictionary<string, object?> Row(JsonArray fields, JsonObject row)
        {
            var cells = row["f"] as JsonArray ?? new JsonArray();
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i] as JsonObject;
                record[Words(field?["name"]) ?? $"f{i}"] = Cell(field, i < cells.Count ? cells[i]?["v"] : null, nested: false) is var value && value is JsonNode node ? node.ToJsonString(Wire) : value;
            }

            return record;
        }

        // A cell's value as its field's type has it; a record or a repeated field (nested) as JSON.
        private static object? Cell(JsonObject? field, JsonNode? value, bool nested)
        {
            if (value is null)
            {
                return null;
            }

            if (Words(field?["mode"]) == "REPEATED")
            {
                var single = field!.DeepClone().AsObject();
                single.Remove("mode");
                return new JsonArray((value as JsonArray ?? new JsonArray()).Select(item => Node(Cell(single, item?["v"], nested: true))).ToArray());
            }

            if (Words(field?["type"]) is "RECORD" or "STRUCT")
            {
                var children = field?["fields"] as JsonArray ?? new JsonArray();
                var cells = value["f"] as JsonArray ?? new JsonArray();
                var record = new JsonObject();
                for (var i = 0; i < children.Count; i++)
                {
                    record[Words(children[i]?["name"]) ?? $"f{i}"] = Node(Cell(children[i] as JsonObject, i < cells.Count ? cells[i]?["v"] : null, nested: true));
                }

                return record;
            }

            var text = Words(value);
            if (text is null)
            {
                return nested ? value.DeepClone() : value.ToJsonString(Wire);
            }

            return Words(field?["type"]) switch
            {
                "INTEGER" or "INT64" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole : text,
                "FLOAT" or "FLOAT64" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : text,
                "BOOLEAN" or "BOOL" => text == "true" ? true : text == "false" ? false : text,
                "TIMESTAMP" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
                    : text,
                _ => text,
            };
        }

        private static JsonNode? Node(object? value) => value switch
        {
            null => null,
            JsonNode node => node,
            string s => JsonValue.Create(s),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            bool b => JsonValue.Create(b),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON, a throttled call waiting as Google says; a failure thrown with its status and Google's status and
        // message; a success that is not JSON a failure that says so.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                throw new InvalidOperationException($"Google BigQuery answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Words(json?["error"]?["status"]);
                var said = Words(json?["error"]?["message"]);
                var text = string.Join(": ", new[] { status, said }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }
    }
}
