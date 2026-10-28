#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.HomeAssistant
{
    /// <summary>
    /// Home Assistant (BL-229, n8n's Home Assistant node: a home's configuration, services, states, events, logs, templates and
    /// cameras) over Home Assistant's REST API with a long-lived access token as its Bearer, over the host's egress. A test names
    /// the home and its version.
    /// </summary>
    /// <remarks>
    /// <para><c>config</c> and <c>check_config</c>; <c>services</c> (each domain and its services) and <c>call_service</c> (a
    /// domain's service with its data - each state it changed a record, or the service's own response when asked for one);
    /// <c>states</c>, <c>state</c> and <c>set_state</c> (an entity's state and attributes - its attributes kept when none are
    /// given, as n8n's Create or Update keeps them); <c>events</c> and <c>fire_event</c>; <c>error_log</c> and <c>logbook</c>
    /// (from a time, to a time, for an entity); <c>history</c> (each recorded state of the entities named - n8n's node holds it
    /// commented out); <c>render_template</c> (a template over several lines from the step's Query box) and
    /// <c>camera_snapshot</c> (a camera's picture written into the run's pipeline folder).</para>
    /// <para>Every entity id, domain, service, event type, time and JSON object is held to its shape before anything is sent; a
    /// record is its fields as Home Assistant gives them, a list or an object as its JSON. A failure is thrown with its status and
    /// Home Assistant's words; an answer that is not the API's - a sign-in page, a proxy's - is said as such.</para>
    /// </remarks>
    public sealed class HomeAssistantConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private static readonly string[] Known =
        {
            "config", "check_config", "services", "call_service", "states", "state", "set_state", "events", "fire_event", "error_log", "logbook", "history", "render_template", "camera_snapshot",
        };

        private static readonly Regex Entity = new("^[a-z0-9_]+\\.[a-z0-9_]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Slug = new("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex EventType = new("^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "homeassistant";

        public string DisplayName => "Home Assistant";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => "http://homeassistant.local:8123";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = "Long-lived access token (your profile > Security > Long-lived access tokens)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity, ConnectorCategory.NetworkInfrastructure };

        public Uri WebsiteUrl { get; } = new("https://www.home-assistant.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.home-assistant.io/docs/api/rest/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField EntityField = Field("entity", "Entity", "Its entity id - light.kitchen, sensor.kassa_queue.", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("config", "Get the configuration", "The home's configuration - n8n's Config Get.") { Parameters = Schema() },
            new ConnectorOperation("check_config", "Check the configuration", "Whether its configuration files are valid - n8n's Config Check.") { Parameters = Schema() },
            new ConnectorOperation("services", "Get services", "Each domain and its services - n8n's Service Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation("call_service", "Call a service", "A domain's service with its data - n8n's Service Call.")
            {
                Parameters = Schema(
                    Field("domain", "Domain", "light, input_boolean, notify ...", required: true),
                    Field("service", "Service", "turn_on, toggle, send_message ...", required: true),
                    Field("data", "Data", "A JSON object - {\"entity_id\": \"light.kitchen\", \"brightness_pct\": 60}.", ConnectorConfigFieldType.Multiline),
                    Field("response", "Its response", "true for the service's own response (a service that gives one); else each state it changed.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation("states", "Get states", "Every entity's state - n8n's State Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation("state", "Get a state", "An entity's state - n8n's State Get.") { Parameters = Schema(EntityField) },
            new ConnectorOperation("set_state", "Create or update a state", "An entity's state as Home Assistant shows it - n8n's State Create or Update.")
            {
                Parameters = Schema(
                    EntityField,
                    Field("state", "State", "Its state - on, 3, open.", required: true),
                    Field("attributes", "Attributes", "A JSON object - {\"unit_of_measurement\": \"people\"}. Empty: its attributes kept.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation("events", "Get events", "Each event type and how many listen to it - n8n's Event Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation("fire_event", "Fire an event", "An event with its data - n8n's Event Create.")
            {
                Parameters = Schema(Field("event", "Event type", "kassa_opened.", required: true), Field("data", "Data", "A JSON object - {\"till\": 2}.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation("error_log", "Get the error log", "The server's error log - n8n's Log Get Error Logs.") { Parameters = Schema() },
            new ConnectorOperation("logbook", "Get logbook entries", "What happened - n8n's Log Get Logbook Entries.")
            {
                Parameters = Schema(
                    Field("from", "From", "When from - 2026-10-17T08:00:00+02:00. Empty: a day ago."),
                    Field("to", "To", "When to."),
                    Field("entity", "Entity", "One entity's entries alone."),
                    MaxField),
            },
            new ConnectorOperation("history", "Get history", "Each state the entities had - n8n's History Get Many.")
            {
                Parameters = Schema(
                    Field("entities", "Entities", "Comma-separated entity ids - sensor.kassa_queue, light.kitchen.", required: true),
                    Field("from", "From", "When from. Empty: a day ago."),
                    Field("to", "To", "When to."),
                    Field("minimal", "Minimal", "true: the first and last state with attributes, the rest without.", ConnectorConfigFieldType.Boolean),
                    Field("significant", "Significant changes only", "true: only a change of state, not of an attribute.", ConnectorConfigFieldType.Boolean),
                    MaxField),
            },
            new ConnectorOperation("render_template", "Render a template", "A Jinja template rendered by Home Assistant - n8n's Template Create; over several lines in the step's Query box.")
            {
                Parameters = Schema(Field("template", "Template", "Queue: {{ states('sensor.kassa_queue') }}.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation("camera_snapshot", "Get a camera's picture", "Written into the run's pipeline folder - n8n's Camera Proxy Get Screenshot.")
            {
                Parameters = Schema(Field("entity", "Camera", "Its entity id - camera.kassa.", required: true), Field("file", "File", "Where in the pipeline folder; a name ending in / is a folder, the picture named after the camera.") with { Default = "homeassistant/" }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var config = await session.JsonAsync(HttpMethod.Get, "/config", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Home Assistant {Text(config?["version"]) ?? "(its version unsaid)"}: {Text(config?["location_name"]) ?? "a home"}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "config").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Home Assistant has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string path = string.Empty;
            JsonObject? body = null;
            string? entity = null;
            string? file = null;
            switch (operation)
            {
                case "call_service":
                    {
                        var domain = SlugOf(p, "domain");
                        var service = SlugOf(p, "service");
                        path = $"/services/{domain}/{service}" + (Flag(p, "response") ? "?return_response" : string.Empty);
                        body = ObjectOf(p, "data") ?? new JsonObject();
                        break;
                    }

                case "state":
                case "camera_snapshot":
                    entity = EntityOf(Values.Get(p, "entity"), "entity");
                    if (operation == "camera_snapshot")
                    {
                        file = Values.Get(p, "file") ?? "homeassistant/";
                        if (context.Files is null)
                        {
                            throw new InvalidOperationException("A camera's picture is written into the run's pipeline folder - run it from a pipeline.");
                        }
                    }

                    break;
                case "set_state":
                    entity = EntityOf(Values.Get(p, "entity"), "entity");
                    body = new JsonObject { ["state"] = p.TryGetValue("state", out var state) && !string.IsNullOrWhiteSpace(state) ? state.Length <= 255 ? state : throw new InvalidOperationException("A state is at most 255 characters (state).") : throw new InvalidOperationException("Give its state (state).") };
                    if (ObjectOf(p, "attributes") is { } attributes)
                    {
                        body["attributes"] = attributes;
                    }

                    break;
                case "fire_event":
                    {
                        var type = Values.Get(p, "event") ?? throw new InvalidOperationException("Give the event's type (event).");
                        path = "/events/" + (EventType.IsMatch(type) ? type : throw new InvalidOperationException($"'{type}' is not an event type - kassa_opened (event)."));
                        body = ObjectOf(p, "data") ?? new JsonObject();
                        break;
                    }

                case "logbook":
                    {
                        var query = new List<string>();
                        if (TimeOf(p, "to") is { } to)
                        {
                            query.Add("end_time=" + Uri.EscapeDataString(to));
                        }

                        if (Values.Get(p, "entity") is { } one)
                        {
                            query.Add("entity=" + EntityOf(one, "entity"));
                        }

                        path = "/logbook" + (TimeOf(p, "from") is { } from ? "/" + Uri.EscapeDataString(from) : string.Empty) + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
                        break;
                    }

                case "history":
                    {
                        var entities = (Values.Get(p, "entities") ?? throw new InvalidOperationException("Give the entities - sensor.kassa_queue, light.kitchen (entities)."))
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(one => EntityOf(one, "entities")).ToList();
                        var query = new List<string> { "filter_entity_id=" + string.Join(",", entities) };
                        if (TimeOf(p, "to") is { } to)
                        {
                            query.Add("end_time=" + Uri.EscapeDataString(to));
                        }

                        if (Flag(p, "minimal"))
                        {
                            query.Add("minimal_response");
                        }

                        if (Flag(p, "significant"))
                        {
                            query.Add("significant_changes_only");
                        }

                        path = "/history/period" + (TimeOf(p, "from") is { } from ? "/" + Uri.EscapeDataString(from) : string.Empty) + "?" + string.Join("&", query);
                        break;
                    }

                case "render_template":
                    {
                        var template = Values.Get(p, "template") ?? (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : null)
                            ?? throw new InvalidOperationException("Give the template - or write it in the step's Query box.");
                        body = new JsonObject { ["template"] = template };
                        break;
                    }
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            switch (operation)
            {
                case "config":
                    yield return Record(await session.JsonAsync(HttpMethod.Get, "/config", null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "check_config":
                    yield return Record(await session.JsonAsync(HttpMethod.Post, "/config/core/check_config", null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "services":
                case "states":
                case "events":
                    foreach (var item in Items(await session.JsonAsync(HttpMethod.Get, "/" + operation, null, cancellationToken)).Take(max))
                    {
                        yield return Record(item);
                    }

                    break;
                case "call_service":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, path, body, cancellationToken);
                        if (answer is JsonObject responded)
                        {
                            // Asked for its response: the service's own, with the states it changed.
                            yield return Fields(("response", Json(responded["service_response"])), ("changed", Json(responded["changed_states"] ?? new JsonArray())));
                            break;
                        }

                        var changed = Items(answer).ToList();
                        if (changed.Count == 0)
                        {
                            yield return Fields(("called", true), ("changed", 0L));
                        }

                        foreach (var item in changed)
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "state":
                    yield return Record(await session.JsonAsync(HttpMethod.Get, "/states/" + entity, null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "set_state":
                    {
                        if (body!["attributes"] is null && await session.JsonOrMissingAsync("/states/" + entity, cancellationToken) is JsonObject { } current && current["attributes"] is JsonObject kept)
                        {
                            body["attributes"] = kept.DeepClone(); // none given: the entity's own kept, as n8n keeps them
                        }

                        yield return Record(await session.JsonAsync(HttpMethod.Post, "/states/" + entity, body, cancellationToken) as JsonObject ?? new JsonObject());
                        break;
                    }

                case "fire_event":
                    {
                        var fired = await session.JsonAsync(HttpMethod.Post, path, body, cancellationToken) as JsonObject;
                        yield return Fields(("event", path["/events/".Length..]), ("message", Text(fired?["message"])));
                        break;
                    }

                case "error_log":
                    yield return Fields(("errorLog", await session.TextAsync(HttpMethod.Get, "/error_log", null, cancellationToken)));
                    break;
                case "logbook":
                    foreach (var item in Items(await session.JsonAsync(HttpMethod.Get, path, null, cancellationToken)).Take(max))
                    {
                        yield return Record(item);
                    }

                    break;
                case "history":
                    {
                        // One list per entity, each its states in time: every state a record, the entity's id on each.
                        var lists = await session.JsonAsync(HttpMethod.Get, path, null, cancellationToken) as JsonArray ?? new JsonArray();
                        var states = lists.OfType<JsonArray>().SelectMany(list =>
                        {
                            var id = list.OfType<JsonObject>().Select(state => Text(state["entity_id"])).FirstOrDefault(found => found is not null);
                            return list.OfType<JsonObject>().Select(state =>
                            {
                                var copy = (JsonObject)state.DeepClone();
                                copy["entity_id"] ??= id;
                                return copy;
                            });
                        });
                        foreach (var item in states.Take(max))
                        {
                            yield return Record(item);
                        }

                        break;
                    }

                case "render_template":
                    yield return Fields(("renderedTemplate", await session.TextAsync(HttpMethod.Post, "/template", body, cancellationToken)));
                    break;
                default:
                    yield return await session.SnapshotAsync(entity!, context.Files!, file!, cancellationToken);
                    break;
            }
        }

        public static string EntityOf(string? given, string what) =>
            given is null ? throw new InvalidOperationException($"Give the entity's id - light.kitchen ({what}).")
            : Entity.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an entity id - light.kitchen, sensor.kassa_queue ({what}).");

        private static string SlugOf(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? Slug.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {name} - lowercase letters, digits and _ ({name}).") : throw new InvalidOperationException($"Give the {name} ({name}).");

        private static bool Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => false,
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
                return JsonNode.Parse(given) as JsonObject ?? throw new InvalidOperationException($"The {name} is not a JSON object - {{\"entity_id\": \"light.kitchen\"}} ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} is not a JSON object - {{\"entity_id\": \"light.kitchen\"}} ({name}).");
            }
        }

        // A time as Home Assistant reads one: ISO 8601 with its offset.
        private static string? TimeOf(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given
                ? DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-17T08:00:00+02:00 ({name}).")
                : null;

        private static IEnumerable<JsonObject> Items(JsonNode? answer) => (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>();

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

        private static string? Json(JsonNode? node) => node?.ToJsonString(Wire);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the server's API under /api with the token as its Bearer; a throttled call waiting as the
        // server says; a failure thrown with its status and Home Assistant's words.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient http)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = http;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Home Assistant's address - http://homeassistant.local:8123.");
                }

                var root = Values.BaseUrl(endpoint, string.Empty);
                if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api".Length]; // the API's own address given: its server's
                }

                return new Session(
                    root + "/api",
                    Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Home Assistant connection has no access token."),
                    egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> JsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var (_, answer) = await SendAsync(method, path, body, missingIsNull: false, cancellationToken);
                try
                {
                    return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    // A page that is not the API's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException("Home Assistant answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
            }

            /// <summary>An entity's state, or null where Home Assistant has none.</summary>
            public async Task<JsonNode?> JsonOrMissingAsync(string path, CancellationToken cancellationToken)
            {
                var (found, answer) = await SendAsync(HttpMethod.Get, path, null, missingIsNull: true, cancellationToken);
                try
                {
                    return found && !string.IsNullOrWhiteSpace(answer) ? JsonNode.Parse(answer) : null;
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("Home Assistant answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
            }

            public async Task<string> TextAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken) =>
                (await SendAsync(method, path, body, missingIsNull: false, cancellationToken)).Answer;

            /// <summary>A camera's picture written into the run's folder; its length checked against the folder's cap first.</summary>
            public async Task<ConnectorRecord> SnapshotAsync(string entity, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () => Request(HttpMethod.Get, "/camera_proxy/" + entity, null), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
                }

                if (response.Content.Headers.ContentLength is { } known && known > files.MaxBytes)
                {
                    throw new InvalidOperationException($"The picture is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var type = response.Content.Headers.ContentType?.MediaType;
                var written = file.EndsWith('/') ? file + entity.Replace('.', '_') + (type switch { "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".jpg" }) : file;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var length = await files.WriteAsync(written, stream, cancellationToken);
                return Fields(("entity_id", entity), ("file", written), ("size", length), ("contentType", type));
            }

            private HttpRequestMessage Request(HttpMethod method, string path, string? payload)
            {
                var request = new HttpRequestMessage(method, baseUrl + path);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (payload is not null)
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                return request;
            }

            private async Task<(bool Found, string Answer)> SendAsync(HttpMethod method, string path, JsonNode? body, bool missingIsNull, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () => Request(method, path, payload), cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                if (missingIsNull && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, string.Empty);
                }

                return response.IsSuccessStatusCode ? (true, answer) : throw Failure(response.StatusCode, answer);
            }

            // Home Assistant's words: its JSON's message, or its plain text ("400: Bad Request").
            private static InvalidOperationException Failure(HttpStatusCode status, string answer)
            {
                string? said;
                try
                {
                    said = Text((JsonNode.Parse(answer) as JsonObject)?["message"]);
                }
                catch (JsonException)
                {
                    said = answer.Trim();
                }

                said = string.IsNullOrWhiteSpace(said) ? null : said.Length > 300 ? said[..300] : said;
                return new InvalidOperationException($"Home Assistant answered HTTP {(int)status}{(said is null ? "." : ": " + said)}" + (status == HttpStatusCode.Unauthorized ? " - check the access token." : string.Empty));
            }
        }
    }
}
