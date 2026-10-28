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
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Metabase
{
    /// <summary>
    /// Metabase (BL-229, n8n's Metabase node) over its REST API (<c>{address}/api</c>), by an API key (<c>x-api-key</c>, Metabase
    /// 0.49 on) or a user's e-mail and password exchanged for a session (<c>POST /api/session</c>, its id sent as
    /// <c>X-Metabase-Session</c>, as n8n's credential does). A test names the user the connection signs in as.
    /// </summary>
    /// <remarks>
    /// <c>questions</c> and <c>question</c> (Metabase's saved questions - its cards - their name, kind, display, database and
    /// collection, the whole card if <c>simple</c> is false); <c>question_result</c> (a question's answer: as JSON, each row a
    /// record up to <c>max</c>; as CSV or XLSX, the file saved into the run's pipeline folder); <c>databases</c> (their engine and
    /// connection's details - never a password, which Metabase does not give - as n8n's Simplify did); <c>database_fields</c>;
    /// <c>metrics</c> and <c>metric</c> (Metabase's metrics, cards of their own kind since 0.50); <c>alerts</c> and <c>alert</c>.
    /// n8n's Add Database is not taken: it carries the database's password, which is never a step's parameter here. Every id,
    /// format and number held to its shape before anything is sent; Metabase's own message on a failure.
    /// </remarks>
    public sealed class MetabaseConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private static readonly string[] Known = { "questions", "question", "question_result", "databases", "database_fields", "metrics", "metric", "alerts", "alert" };

        // What n8n's Simplify kept of a question, and of a database (its connection's details flattened).
        private static readonly string[] QuestionFields = { "id", "name", "description", "type", "display", "database_id", "table_id", "collection_id", "query_type", "archived", "creator_id", "created_at", "updated_at" };

        private static readonly string[] DatabaseFields = { "id", "name", "description", "engine", "creator_id", "timezone", "created_at", "updated_at", "is_full_sync" };

        private static readonly string[] DatabaseDetails = { "db", "user", "host", "port", "ssl" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "metabase";

        public string DisplayName => "Metabase";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "E-mail (empty: the password below is an API key)",
            ["password"] = "Password - or an API key (Admin settings > Authentication > API keys)",
        };

        public string EndpointExample => "https://metabase.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.metabase.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.metabase.com/docs/latest/api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField SimpleField = Field("simple", "Simplify", "false: as Metabase gives it.", ConnectorConfigFieldType.Boolean) with { Default = "true" };

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField IdField(string name, string label) => Field(name, label, "Its number - from its address in Metabase.", ConnectorConfigFieldType.Integer, required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("questions", "Get questions", "The saved questions - n8n's Get Many.") { Parameters = new(new[] { SimpleField, MaxField }) },
            new ConnectorOperation("question", "Get a question", "n8n's Get.") { Parameters = new(new[] { IdField("question", "Question"), SimpleField }) },
            new ConnectorOperation("question_result", "Get a question's result", "Its answer - each row a record, or a file - n8n's Result Data.")
            {
                Parameters = new(new[]
                {
                    IdField("question", "Question"),
                    Field("format", "Format", "json (the default: each row a record), csv or xlsx (a file)."),
                    Field("file", "Save to", "For csv or xlsx: a file in the run's pipeline folder - kassa/orders.csv, or a folder ending in /."),
                    MaxField,
                }),
            },
            new ConnectorOperation("databases", "Get databases", "The databases Metabase reads - n8n's Get Many.") { Parameters = new(new[] { SimpleField, MaxField }) },
            new ConnectorOperation("database_fields", "Get a database's fields", "Each table's columns - n8n's Get Fields.") { Parameters = new(new[] { IdField("database", "Database"), MaxField }) },
            new ConnectorOperation("metrics", "Get metrics", "n8n's Get Many.") { Parameters = new(new[] { MaxField }) },
            new ConnectorOperation("metric", "Get a metric", "n8n's Get.") { Parameters = new(new[] { IdField("metric", "Metric") }) },
            new ConnectorOperation("alerts", "Get alerts", "n8n's Get Many.") { Parameters = new(new[] { MaxField }) },
            new ConnectorOperation("alert", "Get an alert", "n8n's Get.") { Parameters = new(new[] { IdField("alert", "Alert") }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var user = await session.JsonAsync(HttpMethod.Get, "/user/current", cancellationToken);
                return ConnectionTestResult.Ok($"Metabase answers: signed in as {Text(user?["common_name"]) ?? Text(user?["email"])}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "questions").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Metabase has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var simple = Flag(p, "simple") ?? true;
            var id = operation switch
            {
                "question" or "question_result" => Id(p, "question"),
                "database_fields" => Id(p, "database"),
                "metric" => Id(p, "metric"),
                "alert" => Id(p, "alert"),
                _ => 0,
            };
            var format = "json";
            string? file = null;
            if (operation == "question_result")
            {
                format = (Values.Get(p, "format") ?? "json").ToLowerInvariant() is ("json" or "csv" or "xlsx") and var chosen ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "format")}' is not json, csv or xlsx (format).");
                if (format != "json")
                {
                    var target = Values.Get(p, "file") ?? throw new InvalidOperationException($"Give the file a {format} result is saved to, in the run's pipeline folder (file).");
                    file = target.EndsWith('/') ? $"{target}question-{id}.{format}" : target;
                    if (context.Files is null)
                    {
                        throw new InvalidOperationException("A result as a file is saved into the run's pipeline folder - run it from a pipeline (file).");
                    }
                }
            }

            using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case "questions":
                    foreach (var card in Items(await session.JsonAsync(HttpMethod.Get, "/card/?f=all", cancellationToken)).Take(max))
                    {
                        yield return Record(simple ? Pick(card, QuestionFields) : card);
                    }

                    break;
                case "question":
                    yield return Record(simple ? Pick(await Single(session, $"/card/{id}", cancellationToken), QuestionFields) : await Single(session, $"/card/{id}", cancellationToken));
                    break;
                case "question_result" when format == "json":
                    foreach (var row in Items(await session.JsonAsync(HttpMethod.Post, $"/card/{id}/query/json", cancellationToken)).Take(max))
                    {
                        yield return Record(row);
                    }

                    break;
                case "question_result":
                    {
                        var written = await session.SaveAsync($"/card/{id}/query/{format}", file!, context.Files!, cancellationToken);
                        yield return Fields(("question", id), ("format", format), ("file", file), ("size", written));
                        break;
                    }

                case "databases":
                    foreach (var database in Items(await session.JsonAsync(HttpMethod.Get, "/database/", cancellationToken)).Take(max))
                    {
                        if (!simple)
                        {
                            yield return Record(database);
                            continue;
                        }

                        var kept = Pick(database, DatabaseFields);
                        foreach (var detail in DatabaseDetails)
                        {
                            kept[detail] = database["details"]?[detail]?.DeepClone();
                        }

                        kept["is_full_sync"] ??= database["details"]?["is_full_sync"]?.DeepClone();
                        yield return Record(kept);
                    }

                    break;
                case "database_fields":
                    foreach (var field in Items(await session.JsonAsync(HttpMethod.Get, $"/database/{id}/fields", cancellationToken)).Take(max))
                    {
                        yield return Record(field);
                    }

                    break;
                case "metrics":
                    foreach (var metric in Items(await session.JsonAsync(HttpMethod.Get, "/metric/", cancellationToken)).Take(max))
                    {
                        yield return Record(Pick(metric, QuestionFields));
                    }

                    break;
                case "metric":
                    yield return Record(Pick(await Single(session, $"/metric/{id}", cancellationToken), QuestionFields));
                    break;
                case "alerts":
                    foreach (var alert in Items(await session.JsonAsync(HttpMethod.Get, "/alert/", cancellationToken)).Take(max))
                    {
                        yield return Record(alert);
                    }

                    break;
                default:
                    yield return Record(await Single(session, $"/alert/{id}", cancellationToken));
                    break;
            }
        }

        private static async Task<JsonObject> Single(Session session, string route, CancellationToken cancellationToken) =>
            await session.JsonAsync(HttpMethod.Get, route, cancellationToken) as JsonObject ?? new JsonObject();

        // A list as Metabase answers it: an array, or a page of one ({ data: [...] }) as its databases and metrics are.
        private static IEnumerable<JsonObject> Items(JsonNode? answer) =>
            (answer as JsonArray ?? answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        private static JsonObject Pick(JsonObject item, string[] names)
        {
            var picked = new JsonObject();
            foreach (var name in names)
            {
                if (item.ContainsKey(name))
                {
                    picked[name] = item[name]?.DeepClone();
                }
            }

            return picked;
        }

        private static int Id(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{given}' is not a {name}'s number - from its address in Metabase ({name}).")
                : throw new InvalidOperationException($"Give the {name}'s number ({name}).");

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly (string Name, string Value) auth;

            private Session(HttpClient client, string api, (string Name, string Value) auth)
            {
                this.client = client;
                this.api = api;
                this.auth = auth;
            }

            public static async Task<Session> OpenAsync(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, string.Empty);
                if (root.Length == 0)
                {
                    throw new InvalidOperationException("Give Metabase's address - https://metabase.kafe.example.");
                }

                if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api".Length]; // the API's own address given: Metabase's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Metabase's address - https://metabase.kafe.example.");
                }

                var secret = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Metabase connection has no password - or API key.");
                var client = egress.CreateHttpClient(timeout);
                if (Values.Get(credentials, "username") is not { } user)
                {
                    return new Session(client, root + "/api", ("x-api-key", secret));
                }

                try
                {
                    // n8n's sign-in: the user's e-mail and password for a session, its id the header every call carries.
                    var body = new JsonObject { ["username"] = user, ["password"] = secret }.ToJsonString(Wire);
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, root + "/api/session") { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
                    var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Metabase refused the sign-in (HTTP {(int)response.StatusCode}){Said(answer)} - check the e-mail and the password.");
                    }

                    var id = Text(Parse(answer)?["id"]) ?? throw new InvalidOperationException("Metabase signed in without a session - check the address.");
                    return new Session(client, root + "/api", ("X-Metabase-Session", id));
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            public void Dispose() => client.Dispose();

            private HttpRequestMessage Request(HttpMethod method, string route)
            {
                var request = new HttpRequestMessage(method, api + route);
                request.Headers.TryAddWithoutValidation(auth.Name, auth.Value);
                if (method == HttpMethod.Post)
                {
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                }

                return request;
            }

            /// <summary>A call's JSON, or Metabase's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(method, route), cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Metabase answered HTTP {(int)response.StatusCode}{Said(answer)}"
                        + ((int)response.StatusCode is 401 or 403 ? " - check the API key, or the e-mail and password, and what they may see." : string.Empty));
                }

                try
                {
                    return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("Metabase answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
            }

            /// <summary>A result as a file, saved into the run's folder as it streams - held to the folder's size.</summary>
            public async Task<long> SaveAsync(string route, string file, IConnectorFiles files, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(HttpMethod.Post, route), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Metabase answered HTTP {(int)response.StatusCode}{Said(await response.Content.ReadAsStringAsync(cancellationToken))}");
                }

                if (response.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
                {
                    throw new InvalidOperationException($"The result is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await files.WriteAsync(file, content, cancellationToken);
            }

            private static JsonNode? Parse(string answer)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            // Metabase's message: its "message", its first field error, a JSON text of its own ("Not found."), or a short plain answer.
            private static string Said(string answer)
            {
                var json = Parse(answer);
                var said = json switch
                {
                    JsonObject error => Text(error["message"]) ?? (error["errors"] as JsonObject)?.Select(pair => pair.Key + ": " + (Text(pair.Value) ?? pair.Value?.ToJsonString())).FirstOrDefault(),
                    JsonValue text => Text(text),
                    null when answer.Length is > 0 and <= 200 && !answer.TrimStart().StartsWith('<') => answer.Trim(),
                    _ => null,
                };
                return said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said);
            }
        }
    }
}
