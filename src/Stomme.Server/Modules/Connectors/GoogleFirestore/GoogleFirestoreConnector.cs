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

namespace Stomme.Modules.Connectors.GoogleFirestore
{
    /// <summary>
    /// Google Cloud Firestore (n8n's Google Cloud Firestore node: a database's collections and documents, and its
    /// structured queries) over Firestore's REST API with an OAuth client's refresh token (the token through the shared
    /// <see cref="OAuth2TokenProvider"/>, as Google Cloud Storage's is), over the host's egress. A test names the project's
    /// database and its collections.
    /// </summary>
    /// <remarks>
    /// <para><c>collections</c> (the database's, or a document's subcollections); <c>documents</c> (a collection's, page by page
    /// by Google's page token up to <c>max</c>); <c>document_get</c> (one or several by id); <c>document_create</c> (an id given
    /// or Firestore's own); <c>document_upsert</c> (its fields set - only those given, the others kept, or the document replaced
    /// whole - made where there is none); <c>document_delete</c>; <c>query</c> (a structured query's JSON, over several lines
    /// from the step's Query box, up to <c>max</c>).</para>
    /// <para><b>Fields</b> are a JSON object, each value Firestore's own type: a whole number an integer, any other number a
    /// double, a string a string - never guessed to be a number, a boolean or a date as n8n guesses - true and false, null, a list
    /// an array, an object a map; a value Firestore types otherwise is given as its typed object - {"timestampValue":
    /// "2026-10-17T08:00:00Z"}, {"geoPointValue": {...}}, {"referenceValue": "..."}, {"bytesValue": "..."}. A document's record is
    /// its id and its fields as plain JSON - a map or an array its JSON - and, unless <c>simple</c>, its name and times. Every
    /// path, id and field name is held to Firestore's shape before anything is sent; Google's status and message on a failure.</para>
    /// </remarks>
    public sealed class GoogleFirestoreConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://firestore.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly string[] Known = { "collections", "documents", "document_get", "document_create", "document_upsert", "document_delete", "query" };

        private static readonly string[] TypedValues = { "timestampValue", "geoPointValue", "referenceValue", "bytesValue" };

        private static readonly Regex Project = new("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex Database = new("^(\\(default\\)|[a-z][a-z0-9-]{2,61}[a-z0-9])$", RegexOptions.CultureInvariant);

        private static readonly Regex SimpleField = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlefirestore";

        public string DisplayName => "Google Cloud Firestore";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted the datastore scope)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://firebase.google.com/products/firestore");

        public Uri ApiDocumentationUrl { get; } = new("https://firebase.google.com/docs/firestore/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: true, Description: "The Google Cloud project's id - kafe-kassa."),
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: false, Description: "The Firestore database's id - (default), its first.") { Default = "(default)" },
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField CollectionField = Field("collection", "Collection", "Its path - orders, or orders/1042/lines for a document's subcollection.", required: true);

        private static readonly ConnectorConfigField SimpleField2 = Field("simple", "Simplify", "false adds each document's name and its create and update times.", ConnectorConfigFieldType.Boolean) with { Default = "true" };

        private static readonly ConnectorConfigField FieldsField = Field("fields", "Fields", "A JSON object - {\"till\": \"2\", \"total\": 35.5, \"paid\": true}; a timestamp {\"timestampValue\": \"2026-10-17T08:00:00Z\"}.", ConnectorConfigFieldType.Multiline);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("collections", "Get collections", "The database's collections, or a document's - n8n's Collection Get Many.")
            {
                Parameters = Schema(Field("document", "Of a document", "A document's path for its subcollections - orders/1042. Empty: the database's."), MaxField),
            },
            new ConnectorOperation("documents", "Get documents", "A collection's documents - n8n's Document Get Many.") { Parameters = Schema(CollectionField, MaxField, SimpleField2) },
            new ConnectorOperation("document_get", "Get documents by id", "n8n's Document Get.") { Parameters = Schema(CollectionField, Field("id", "Id", "A document's id, or several separated by commas - 1042, 1043.", required: true), SimpleField2) },
            new ConnectorOperation("document_create", "Create a document", "n8n's Document Create.")
            {
                Parameters = Schema(CollectionField, Field("id", "Id", "Its id. Empty: Firestore's own."), FieldsField with { Required = true }, SimpleField2),
            },
            new ConnectorOperation("document_upsert", "Create or update a document", "Its fields set, the document made where there is none - n8n's Document Create or Update.")
            {
                Parameters = Schema(
                    CollectionField,
                    Field("id", "Id", "Its id - 1042.", required: true),
                    FieldsField with { Required = true },
                    Field("replace", "Replace whole", "true: the document becomes what is given; else only the fields given change.", ConnectorConfigFieldType.Boolean),
                    SimpleField2),
            },
            new ConnectorOperation("document_delete", "Delete a document", "n8n's Document Delete.") { Parameters = Schema(CollectionField, Field("id", "Id", "Its id - 1042.", required: true)) },
            new ConnectorOperation("query", "Run a query", "A structured query - n8n's Document Query; over several lines in the step's Query box.")
            {
                Parameters = Schema(
                    Field("structured_query", "Structured query", "Its JSON - {\"from\": [{\"collectionId\": \"orders\"}], \"where\": {...}}, with or without its structuredQuery wrapper.", ConnectorConfigFieldType.Multiline),
                    Field("document", "Under a document", "A document's path to query its subcollections - orders/1042. Empty: the database's."),
                    MaxField,
                    SimpleField2),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var page = await session.JsonAsync(HttpMethod.Post, ":listCollectionIds", new JsonObject { ["pageSize"] = 10 }, cancellationToken) as JsonObject;
                var ids = (page?["collectionIds"] as JsonArray ?? new JsonArray()).Select(id => Text(id)).OfType<string>().ToList();
                return ConnectionTestResult.Ok($"Google Cloud Firestore: {session.Name} - {(ids.Count == 0 ? "no collections yet" : string.Join(", ", ids) + (page?["nextPageToken"] is not null ? " ..." : string.Empty))}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "documents").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Cloud Firestore has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var simple = Flag(p, "simple") ?? true;
            var collection = operation is "collections" or "query" ? string.Empty : PathOf(Values.Get(p, "collection"), "collection", collection: true);
            var document = operation is "collections" or "query" && Values.Get(p, "document") is { } under ? PathOf(under, "document", collection: false) : null;
            var ids = new List<string>();
            JsonObject? body = null;
            var route = string.Empty;
            switch (operation)
            {
                case "document_get":
                    ids = (Values.Get(p, "id") ?? throw new InvalidOperationException("Give the document's id (id).")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IdOf).ToList();
                    break;
                case "document_create":
                case "document_upsert":
                    {
                        var given = Values.Get(p, "id");
                        if (given is not null || operation == "document_upsert")
                        {
                            ids.Add(IdOf(given ?? throw new InvalidOperationException("Give the document's id (id).")));
                        }

                        var fields = ObjectOf(p, "fields") ?? throw new InvalidOperationException("Give its fields - a JSON object (fields).");
                        body = new JsonObject { ["fields"] = FieldsOf(fields) };
                        if (operation == "document_upsert" && Flag(p, "replace") != true)
                        {
                            route = string.Concat(fields.Select(field => "&updateMask.fieldPaths=" + Uri.EscapeDataString(FieldPath(field.Key))));
                        }

                        break;
                    }

                case "document_delete":
                    ids.Add(IdOf(Values.Get(p, "id")));
                    break;
                case "query":
                    {
                        var text = Values.Get(p, "structured_query") ?? (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : null)
                            ?? throw new InvalidOperationException("Give the structured query - or write it in the step's Query box.");
                        JsonObject? parsed;
                        try
                        {
                            parsed = JsonNode.Parse(text) as JsonObject;
                        }
                        catch (JsonException)
                        {
                            parsed = null;
                        }

                        var structured = (parsed?["structuredQuery"] as JsonObject ?? parsed) ?? throw new InvalidOperationException("The structured query is not a JSON object - {\"from\": [{\"collectionId\": \"orders\"}]}.");
                        if (structured["from"] is not JsonArray { Count: > 0 })
                        {
                            throw new InvalidOperationException("The structured query names no collection - {\"from\": [{\"collectionId\": \"orders\"}], ...}.");
                        }

                        body = new JsonObject { ["structuredQuery"] = structured.DeepClone() };
                        break;
                    }
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case "collections":
                    {
                        // Page by page by Google's page token, a page no larger than what is still wanted.
                        var read = 0;
                        string? next = null;
                        do
                        {
                            var asked = new JsonObject { ["pageSize"] = Math.Min(PageSize, max - read) };
                            if (next is not null)
                            {
                                asked["pageToken"] = next;
                            }

                            var page = await session.JsonAsync(HttpMethod.Post, (document is null ? string.Empty : "/" + Escaped(document)) + ":listCollectionIds", asked, cancellationToken) as JsonObject;
                            foreach (var id in (page?["collectionIds"] as JsonArray ?? new JsonArray()).Select(id => Text(id)).OfType<string>())
                            {
                                yield return Fields(("name", id), ("path", document is null ? id : document + "/" + id));
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["nextPageToken"]) is { Length: > 0 } token ? token : null;
                        }
                        while (next is not null);

                        break;
                    }

                case "documents":
                    {
                        var read = 0;
                        string? next = null;
                        do
                        {
                            var page = await session.JsonAsync(HttpMethod.Get, "/" + Escaped(collection) + "?pageSize=" + Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture) + (next is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(next)), null, cancellationToken) as JsonObject;
                            foreach (var found in (page?["documents"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(found, simple);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["nextPageToken"]) is { Length: > 0 } token ? token : null;
                        }
                        while (next is not null);

                        break;
                    }

                case "document_get":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, ":batchGet", new JsonObject { ["documents"] = new JsonArray(ids.Select(id => (JsonNode)session.NameOf(collection + "/" + id)).ToArray()) }, cancellationToken) as JsonArray;
                        foreach (var found in (answer ?? new JsonArray()).OfType<JsonObject>().Select(entry => entry["found"]).OfType<JsonObject>())
                        {
                            yield return Record(found, simple); // one that is missing is no record, as n8n's
                        }

                        break;
                    }

                case "document_create":
                    yield return Record(await session.JsonAsync(HttpMethod.Post, "/" + Escaped(collection) + (ids.Count == 1 ? "?documentId=" + Uri.EscapeDataString(ids[0]) : string.Empty), body, cancellationToken) as JsonObject ?? new JsonObject(), simple);
                    break;
                case "document_upsert":
                    yield return Record(await session.JsonAsync(HttpMethod.Patch, "/" + Escaped(collection + "/" + ids[0]) + (route.Length == 0 ? string.Empty : "?" + route[1..]), body, cancellationToken) as JsonObject ?? new JsonObject(), simple);
                    break;
                case "document_delete":
                    await session.JsonAsync(HttpMethod.Delete, "/" + Escaped(collection + "/" + ids[0]), null, cancellationToken);
                    yield return Fields(("id", ids[0]), ("deleted", true));
                    break;
                default:
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, (document is null ? string.Empty : "/" + Escaped(document)) + ":runQuery", body, cancellationToken) as JsonArray;
                        foreach (var found in (answer ?? new JsonArray()).OfType<JsonObject>().Select(entry => entry["document"]).OfType<JsonObject>().Take(max))
                        {
                            yield return Record(found, simple); // an entry with only its read time is no document
                        }

                        break;
                    }
            }
        }

        /// <summary>A JSON value as Firestore's typed value: no guessing - a string stays a string - and a typed object passed as it is.</summary>
        public static JsonObject ValueOf(JsonNode? node) => node switch
        {
            null => new JsonObject { ["nullValue"] = null },
            JsonValue flag when flag.TryGetValue<bool>(out var b) => new JsonObject { ["booleanValue"] = b },
            JsonValue text when text.TryGetValue<string>(out var s) => new JsonObject { ["stringValue"] = s },
            JsonValue number when number.GetValueKind() == JsonValueKind.Number => number.ToJsonString() is var raw && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? new JsonObject { ["integerValue"] = whole.ToString(CultureInfo.InvariantCulture) }
                : new JsonObject { ["doubleValue"] = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) },
            JsonArray array => new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray(array.Select(ValueOf).Select(value => (JsonNode)value).ToArray()) } },
            JsonObject typed when typed.Count == 1 && TypedValues.Contains(typed.First().Key, StringComparer.Ordinal) => (JsonObject)typed.DeepClone(),
            JsonObject map => new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = FieldsOf(map) } },
            _ => throw new InvalidOperationException($"'{node.ToJsonString()}' is no value Firestore keeps."),
        };

        /// <summary>A Firestore typed value as plain JSON: an integer a number, a timestamp, a reference and bytes their text, a geopoint its latitude and longitude.</summary>
        public static JsonNode? PlainOf(JsonObject value)
        {
            var (kind, inner) = value.FirstOrDefault();
            return kind switch
            {
                "nullValue" => null,
                "integerValue" => long.TryParse(Text(inner) ?? inner?.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole) : inner?.DeepClone(),
                "arrayValue" => new JsonArray(((inner as JsonObject)?["values"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(PlainOf).ToArray()),
                "mapValue" => new JsonObject(((inner as JsonObject)?["fields"] as JsonObject ?? new JsonObject()).Select(field => KeyValuePair.Create(field.Key, field.Value is JsonObject typed ? PlainOf(typed) : null))),
                _ => inner?.DeepClone(),
            };
        }

        private static JsonObject FieldsOf(JsonObject fields)
        {
            var typed = new JsonObject();
            foreach (var (name, value) in fields)
            {
                if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 1500 || Regex.IsMatch(name, "^__.*__$"))
                {
                    throw new InvalidOperationException($"'{name}' is not a field's name Firestore keeps - at most 1,500 bytes, not __like this__ (fields).");
                }

                typed[name] = ValueOf(value);
            }

            return typed;
        }

        // A field's path for an update mask: as it is where it is plain, in backquotes - its own escaped - where it is not.
        private static string FieldPath(string name) => SimpleField.IsMatch(name) ? name : "`" + name.Replace("\\", "\\\\").Replace("`", "\\`") + "`";

        // A collection's path (an odd number of segments) or a document's (an even one), each segment Firestore's shape.
        private static string PathOf(string? given, string what, bool collection)
        {
            var path = (given ?? throw new InvalidOperationException($"Give the {what}'s path ({what}).")).Trim('/');
            var segments = path.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || Regex.IsMatch(segment, "^__.*__$") || Encoding.UTF8.GetByteCount(segment) > 1500)
                || segments.Length % 2 != (collection ? 1 : 0))
            {
                throw new InvalidOperationException($"'{given}' is not a {what}'s path - {(collection ? "orders, or orders/1042/lines" : "orders/1042")} ({what}).");
            }

            return path;
        }

        private static string IdOf(string? given) =>
            given is null ? throw new InvalidOperationException("Give the document's id (id).")
            : given.Length > 0 && !given.Contains('/') && given is not ("." or "..") && !Regex.IsMatch(given, "^__.*__$") && Encoding.UTF8.GetByteCount(given) <= 1500 ? given
            : throw new InvalidOperationException($"'{given}' is not a document's id - no /, not . or .., not __like this__ (id).");

        private static string Escaped(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static JsonObject? ObjectOf(IReadOnlyDictionary<string, string> p, string name)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(given) as JsonObject ?? throw new InvalidOperationException($"The {name} is not a JSON object ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} is not a JSON object ({name}).");
            }
        }

        // A document as a record: its id and its fields as plain JSON - a map or an array its JSON - and its name and times unless simple.
        private static ConnectorRecord Record(JsonObject document, bool simple)
        {
            var name = Text(document["name"]) ?? string.Empty;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = name[(name.LastIndexOf('/') + 1)..] };
            if (!simple)
            {
                fields["name"] = name;
                fields["createTime"] = Text(document["createTime"]);
                fields["updateTime"] = Text(document["updateTime"]);
            }

            foreach (var (field, value) in document["fields"] as JsonObject ?? new JsonObject())
            {
                fields[field] = Value(value is JsonObject typed ? PlainOf(typed) : null);
            }

            return new ConnectorRecord(fields);
        }

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

        /// <summary>One signed-in Firestore database: its documents' address and its token.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string documents;

            private Session(HttpClient client, string token, string api, string project, string database)
            {
                this.client = client;
                this.token = token;
                Root = $"projects/{project}/databases/{database}/documents";
                documents = api + "/v1/" + $"projects/{Uri.EscapeDataString(project)}/databases/{Uri.EscapeDataString(database)}/documents";
                Name = $"the project {project}'s database {database}";
            }

            public string Root { get; }

            public string Name { get; }

            public static async Task<Session> OpenAsync(GoogleFirestoreConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Firestore's address - its own, {DefaultEndpoint}.");
                }

                var project = Values.Get(settings, "project") ?? throw new InvalidOperationException("Give the connection its project (project).");
                var database = Values.Get(settings, "database") ?? "(default)";
                if (!Project.IsMatch(project))
                {
                    throw new InvalidOperationException($"'{project}' is not a project's id - kafe-kassa (project).");
                }

                if (!Database.IsMatch(database))
                {
                    throw new InvalidOperationException($"'{database}' is not a database's id - (default), or kafe-orders (database).");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, api, project, database);
            }

            /// <summary>A document's full name, as batchGet names it.</summary>
            public string NameOf(string path) => Root + "/" + path;

            public void Dispose() => client.Dispose();

            /// <summary>A JSON call under the database's documents: its answer, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, documents + route);
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
                    throw new InvalidOperationException("Firestore answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = (json is JsonArray list ? list.FirstOrDefault() : json)?["error"];
                    var status = Text(error?["status"]);
                    var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                    throw new InvalidOperationException($"Firestore answered HTTP {(int)response.StatusCode}{(status is null ? string.Empty : " " + status)}{(message is null ? "." : ": " + message)}"
                        + ((int)response.StatusCode is 401 or 403 ? " - check the refresh token and its scope." : string.Empty));
                }

                return json;
            }
        }
    }
}
