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

namespace Stomme.Modules.Connectors.FacebookGraph
{
    /// <summary>
    /// The Facebook Graph API (n8n's Facebook Graph API node: a node or its edge read, posted to and deleted): Meta's
    /// Graph API - a Page's, an Instagram account's, an ad account's - with an access token as a bearer token, never in an
    /// address, over the host's egress. A test names whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>get</c> (a node - me, a Page's or a post's id - or its edge, with the fields to read and more of the Graph's parameters;
    /// an edge's list page by page by the Graph's cursor up to <c>max</c>), <c>post</c> (to a node or an edge, its fields as
    /// JSON) and <c>delete</c>, in the connection's Graph version or the step's. A record is the object's fields - an object or a
    /// list as its JSON - or, raw, the answer as the Graph gives it. Every node, edge, version, field list and parameter held to
    /// its shape before anything is sent. A throttled call waits as the Graph says; a failure is thrown with its status, the
    /// Graph's error type, code and message.
    /// </remarks>
    public sealed class FacebookGraphConnector : IConnector, IConnectorExtractor
    {
        public const string GetOperation = "get";

        public const string PostOperation = "post";

        public const string DeleteOperation = "delete";

        public const string DefaultEndpoint = "https://graph.facebook.com";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A node: me, an id (a post's carries its Page's - 123_456), an ad account (act_123), a path of up to four - never a dot
        // segment.
        private static readonly Regex Node = new("^[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}(/[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}){0,3}$", RegexOptions.CultureInvariant);

        private static readonly Regex Edge = new("^[a-z_]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex GraphVersion = new("^v[0-9]{1,2}\\.[0-9]$", RegexOptions.CultureInvariant);

        // The Graph's field expansion - id,name,posts.limit(5){message,created_time}.
        private static readonly Regex FieldList = new("^[A-Za-z0-9_.{}(),:]{1,2000}$", RegexOptions.CultureInvariant);

        private static readonly Regex ParameterName = new("^[a-z_]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { GetOperation, PostOperation, DeleteOperation };

        // The token travels in its header, and the Graph's paging is this connector's own.
        private static readonly string[] Reserved = { "access_token", "appsecret_proof", "after", "before", "limit" };

        public string ConnectorType => "facebookgraph";

        public string DisplayName => "Facebook Graph API";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://developers.facebook.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.facebook.com/docs/graph-api/reference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("version", "Graph version", ConnectorConfigFieldType.Text, Required: false, Description: "v21.0 - the app's own when left empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField NodeField = Field("node", "Node", "me, a Page's, a post's or an ad account's id - 1234567890, act_123.", required: true);

        private static readonly ConnectorConfigField EdgeField = Field("edge", "Edge", "A node's list - feed, posts, comments, photos.");

        private static readonly ConnectorConfigField VersionField = Field("version", "Graph version", "v21.0 - the connection's when left empty.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(GetOperation, "Read", "A node, or its edge's list - n8n's Facebook Graph API GET.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    NodeField,
                    EdgeField,
                    Field("fields", "Fields", "Comma-separated fields to read - id,name,posts.limit(5){message}."),
                    Field("query", "Parameters", "More of the Graph's parameters as JSON - {\"since\": \"2026-10-01\", \"filter\": \"stream\"}.", ConnectorConfigFieldType.Multiline),
                    Field("raw", "Raw", "true: the answer as the Graph gives it, one record.", ConnectorConfigFieldType.Boolean),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many of an edge's, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    VersionField,
                }),
            },
            new ConnectorOperation(PostOperation, "Post", "To a node or its edge - n8n's Facebook Graph API POST.")
            {
                Parameters = new ConnectorConfigSchema(new[] { NodeField, EdgeField, Field("fields", "Fields", "A JSON object of what to post - {\"message\": \"Hello\", \"link\": \"https://example.com\"}.", ConnectorConfigFieldType.Multiline), VersionField }),
            },
            new ConnectorOperation(DeleteOperation, "Delete", "A node, or from its edge - n8n's Facebook Graph API DELETE.") { Parameters = new ConnectorConfigSchema(new[] { NodeField, EdgeField, VersionField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, null, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/me?fields=id,name", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Facebook Graph API{(session.Version is { } version ? " " + version : "")}: the token is {Text(me?["name"]) ?? "a user"}'s{(Text(me?["id"]) is { } id ? $" ({id})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? GetOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The Facebook Graph API has no operation '{context.OperationName}' - get, post or delete.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, Values.Get(p, "version"), context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var node = Values.Get(p, "node") is { } named ? Node.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a node - me, 1234567890, act_123.") : throw new InvalidOperationException("Give the node - me, or an id.");
            var path = "/" + node + (Values.Get(p, "edge") is { } edge ? "/" + (Edge.IsMatch(edge) ? edge : throw new InvalidOperationException($"'{edge}' is not an edge - feed, posts, comments.")) : "");

            var query = new List<string>();
            JsonObject? body = null;
            if (operation == GetOperation)
            {
                if (Values.Get(p, "fields") is { } fields)
                {
                    var clean = fields.Replace(" ", "", StringComparison.Ordinal);
                    query.Add("fields=" + Uri.EscapeDataString(FieldList.IsMatch(clean) ? clean : throw new InvalidOperationException($"'{fields}' is not a list of fields - id,name,posts.limit(5){{message}}.")));
                }

                if (Values.Get(p, "query") is { } json)
                {
                    foreach (var (name, value) in Parse(json) as JsonObject ?? throw new InvalidOperationException("The parameters are not a JSON object - {\"since\": \"2026-10-01\"}."))
                    {
                        if (!ParameterName.IsMatch(name) || Reserved.Contains(name))
                        {
                            throw new InvalidOperationException($"'{name}' is not a parameter this step sends - the token travels in its header, and {string.Join(", ", Reserved[2..])} are its paging's.");
                        }

                        query.Add(name + "=" + Uri.EscapeDataString(value switch
                        {
                            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
                            JsonValue scalar => scalar.ToJsonString(),
                            null => throw new InvalidOperationException($"'{name}' in the parameters has no value."),
                            _ => value.ToJsonString(Wire),
                        }));
                    }
                }
            }

            if (operation == PostOperation)
            {
                body = Values.Get(p, "fields") is { } json
                    ? Parse(json) is JsonObject { Count: > 0 } posted ? posted : throw new InvalidOperationException("The fields are not a JSON object with a field or more - {\"message\": \"Hello\"}.")
                    : new JsonObject();
            }

            var raw = Values.GetBool(p, "raw", false);
            using var client = session.Client;
            if (operation != GetOperation)
            {
                var answer = await session.SendAsync(operation == PostOperation ? HttpMethod.Post : HttpMethod.Delete, path, body, cancellationToken);
                yield return Record(answer as JsonObject ?? new JsonObject { ["success"] = answer is JsonValue done && done.TryGetValue<bool>(out var ok) && ok });
                yield break;
            }

            // An edge's list pages by the Graph's cursor: the limit is fixed at the start, an empty page or no next the end. A
            // node read whole takes no limit.
            var listing = path.Count(character => character == '/') > 1;
            var size = Math.Min(PageSize, max);
            var read = 0;
            string? after = null;
            while (true)
            {
                var paging = query.Concat(listing ? new[] { $"limit={size}" } : Array.Empty<string>()).Concat(after is null ? Array.Empty<string>() : new[] { "after=" + Uri.EscapeDataString(after) }).ToList();
                var answer = await session.SendAsync(HttpMethod.Get, path + (paging.Count == 0 ? "" : "?" + string.Join("&", paging)), null, cancellationToken) as JsonObject ?? new JsonObject();
                if (raw || answer["data"] is not JsonArray list)
                {
                    yield return Record(answer);
                    yield break;
                }

                var items = list.OfType<JsonObject>().ToList();
                foreach (var item in items)
                {
                    yield return Record(item);
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                after = Text(answer["paging"]?["cursors"]?["after"]);
                if (items.Count == 0 || after is not { Length: > 0 } || Text(answer["paging"]?["next"]) is not { Length: > 0 })
                {
                    yield break;
                }
            }
        }

        private static JsonNode? Parse(string json)
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

        // A record: the object's fields - text, number and flag as themselves, an object or a list as its JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
            var other => other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the Graph at its version, the token as a bearer token; a throttled call waiting as the Graph
        // says; a failure thrown with its status, the Graph's error type, code and message.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, string? version, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Version = version;
                Client = client;
            }

            public HttpClient Client { get; }

            public string? Version { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, string? stepVersion, IConnectorEgress egress, TimeSpan timeout)
            {
                var version = (stepVersion ?? Values.Get(settings, "version")) is { } given ? GraphVersion.IsMatch(given.ToLowerInvariant()) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a Graph version - v21.0.") : null;
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Facebook Graph API connection has no access token.");
                return new Session(Values.BaseUrl(endpoint, DefaultEndpoint) + (version is null ? "" : "/" + version), token, version, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
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
                    // A page that is not the Graph's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"The Facebook Graph API answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = (json as JsonObject)?["error"] as JsonObject;
                    var type = Text(error?["type"]);
                    var code = error?["code"] is JsonValue number && number.TryGetValue<long>(out var c) ? c.ToString(CultureInfo.InvariantCulture) : null;
                    var kind = type is null ? code : code is null ? type : type + " " + code;
                    var said = Text(error?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(kind is null ? "" : $" ({kind})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
