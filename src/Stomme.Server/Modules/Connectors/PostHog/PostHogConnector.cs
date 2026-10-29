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

namespace Stomme.Modules.Connectors.PostHog
{
    /// <summary>
    /// PostHog (BL-229, n8n's PostHog node) over its capture API (<c>POST /batch/</c> on the project's ingestion host -
    /// <c>https://eu.i.posthog.com</c>, <c>https://us.i.posthog.com</c> or a self-hosted one), its project key in each body's
    /// <c>api_key</c> as PostHog and n8n send it. A test asks PostHog for a user's feature flags, which it answers only for a key
    /// it knows.
    /// </summary>
    /// <remarks>
    /// <c>event</c> (an event for a person, with its properties and time - n8n's Event Create); <c>events</c> (a JSON array of
    /// events in one call, as n8n's node sent its items); <c>identify</c> (a person's properties set, and set once - n8n's Identity
    /// Create); <c>alias</c> (n8n's Alias Create); <c>page</c> (a $pageview, PostHog's own, at an address with a title - n8n's
    /// Track Page sent $page); <c>screen</c> (n8n's Track Screen); and <c>flags</c> (the feature flags PostHog evaluates for a
    /// person - which n8n's node has not). Every name, id, time and JSON held to PostHog's shape before anything is sent; PostHog's
    /// answer said on a failure.
    /// </remarks>
    public sealed class PostHogConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://eu.i.posthog.com";

        private static readonly string[] Known = { "event", "events", "identify", "alias", "page", "screen", "flags" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "posthog";

        public string DisplayName => "PostHog";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-key"] = "Project API key (Project settings > Project API key - phc_...)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing, ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://posthog.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://posthog.com/docs/api/capture");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField PersonField = Field("distinct_id", "Person", "Their distinct id - user-42, or their e-mail.", required: true);

        private static readonly ConnectorConfigField PropertiesField = Field("properties", "Properties", "A JSON object - {\"plan\": \"pro\", \"till\": 4}.");

        private static readonly ConnectorConfigField TimeField = Field("timestamp", "When", "Its time, ISO 8601 - 2026-10-18T09:30:00Z. Empty: now.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("event", "Capture an event", "An event for a person - n8n's Event Create.")
            {
                Parameters = new(new[] { Field("event", "Event", "Its name - order placed.", required: true), PersonField, PropertiesField, TimeField }),
            },
            new ConnectorOperation("events", "Capture events", "Several in one call.")
            {
                Parameters = new(new[] { Field("events", "Events", "A JSON array - [{\"event\": \"order placed\", \"distinct_id\": \"user-42\", \"properties\": {...}, \"timestamp\": \"...\"}].", required: true) }),
            },
            new ConnectorOperation("identify", "Identify a person", "Their properties set - n8n's Identity Create.")
            {
                Parameters = new(new[] { PersonField, Field("properties", "Set", "A JSON object of their properties to set - {\"email\": \"ada@kafe.example\"}."), Field("set_once", "Set once", "A JSON object of properties set only if not yet set."), TimeField }),
            },
            new ConnectorOperation("alias", "Alias a person", "Another id for them - n8n's Alias Create.")
            {
                Parameters = new(new[] { PersonField, Field("alias", "Alias", "The other id, merged with theirs.", required: true), TimeField }),
            },
            new ConnectorOperation("page", "Track a page", "A page they viewed - n8n's Track Page.")
            {
                Parameters = new(new[] { PersonField, Field("url", "Address", "The page's address - https://kafe.example/meny."), Field("name", "Title", "The page's name."), PropertiesField, TimeField }),
            },
            new ConnectorOperation("screen", "Track a screen", "A screen they viewed in an app - n8n's Track Screen.")
            {
                Parameters = new(new[] { PersonField, Field("name", "Screen", "The screen's name.", required: true), PropertiesField, TimeField }),
            },
            new ConnectorOperation("flags", "Get feature flags", "The flags PostHog turns on for a person.") { Parameters = new(new[] { PersonField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.SendAsync("/flags?v=2", new JsonObject { ["distinct_id"] = "stomme-connection-test" }, cancellationToken);
                return ConnectionTestResult.Ok($"PostHog answers: the key's project has {(answer?["flags"] as JsonObject)?.Count ?? 0} feature flags.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "event").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"PostHog has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var batch = new JsonArray();
            string person = string.Empty;
            if (operation == "events")
            {
                JsonArray? given;
                try
                {
                    given = Values.Get(p, "events") is { } text ? JsonNode.Parse(text) as JsonArray : null;
                }
                catch (JsonException)
                {
                    given = null;
                }

                if (given is null || given.Count == 0 || given.Any(item => item is not JsonObject))
                {
                    throw new InvalidOperationException("The events are a JSON array of objects - [{\"event\": \"order placed\", \"distinct_id\": \"user-42\"}] (events).");
                }

                foreach (var item in given.OfType<JsonObject>())
                {
                    batch.Add(Event(Text(item["event"]) ?? string.Empty, Text(item["distinct_id"]) ?? string.Empty, item["properties"] is JsonObject properties ? (JsonObject)properties.DeepClone() : item["properties"] is null ? new JsonObject() : throw new InvalidOperationException("An event's properties are a JSON object (events)."), Text(item["timestamp"]), "events"));
                }
            }
            else
            {
                person = Values.Get(p, "distinct_id") ?? throw new InvalidOperationException("Give the person's distinct id (distinct_id).");
                var when = Values.Get(p, "timestamp");
                switch (operation)
                {
                    case "event":
                        batch.Add(Event(Values.Get(p, "event") ?? throw new InvalidOperationException("Give the event's name (event)."), person, Object(p, "properties"), when, "event"));
                        break;
                    case "identify":
                        {
                            var properties = new JsonObject { ["$set"] = Object(p, "properties") };
                            if (Values.Get(p, "set_once") is not null)
                            {
                                properties["$set_once"] = Object(p, "set_once");
                            }

                            batch.Add(Event("$identify", person, properties, when, "distinct_id"));
                            break;
                        }

                    case "alias":
                        batch.Add(Event("$create_alias", person, new JsonObject { ["alias"] = Id(Values.Get(p, "alias") ?? throw new InvalidOperationException("Give the other id (alias)."), "alias") }, when, "distinct_id"));
                        break;
                    case "page":
                        {
                            var properties = Object(p, "properties");
                            if (Values.Get(p, "url") is { } url)
                            {
                                properties["$current_url"] = Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var page) && (page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp) ? url : throw new InvalidOperationException($"'{url}' is not a page's address - https://kafe.example/meny (url).");
                            }

                            if (Values.Get(p, "name") is { } title)
                            {
                                properties["title"] = title;
                            }

                            batch.Add(Event("$pageview", person, properties, when, "distinct_id"));
                            break;
                        }

                    case "screen":
                        {
                            var properties = Object(p, "properties");
                            properties["$screen_name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the screen's name (name).");
                            batch.Add(Event("$screen", person, properties, when, "distinct_id"));
                            break;
                        }

                    default:
                        Id(person, "distinct_id");
                        break;
                }
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            if (operation == "flags")
            {
                var answer = await session.SendAsync("/flags?v=2", new JsonObject { ["distinct_id"] = person }, cancellationToken);
                foreach (var (key, flag) in answer?["flags"] as JsonObject ?? new JsonObject())
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["distinct_id"] = person,
                        ["flag"] = key,
                        ["enabled"] = flag?["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && on,
                        ["variant"] = Text(flag?["variant"]),
                        ["reason"] = Text(flag?["reason"]?["code"]) ?? Text(flag?["reason"]?["description"]),
                    });
                }

                yield break;
            }

            await session.SendAsync("/batch/", new JsonObject { ["batch"] = batch }, cancellationToken);
            foreach (var sent in batch.OfType<JsonObject>())
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["event"] = Text(sent["event"]), ["distinct_id"] = Text(sent["distinct_id"]), ["captured"] = true });
            }
        }

        private static JsonObject Event(string name, string person, JsonObject properties, string? when, string field)
        {
            if (name.Length is 0 or > 200)
            {
                throw new InvalidOperationException($"An event's name is 1 to 200 characters ({(field == "events" ? "events" : "event")}).");
            }

            var item = new JsonObject { ["event"] = name, ["distinct_id"] = Id(person, field == "events" ? "events" : "distinct_id"), ["properties"] = properties };
            if (when is not null)
            {
                item["timestamp"] = DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{when}' is not a time - 2026-10-18T09:30:00Z ({(field == "events" ? "events" : "timestamp")}).");
            }

            return item;
        }

        // PostHog's own bound on a distinct id: present, at most 200 characters.
        private static string Id(string given, string name) =>
            given.Trim() is { Length: > 0 and <= 200 } id ? id : throw new InvalidOperationException($"A distinct id is 1 to 200 characters ({name}).");

        private static JsonObject Object(IReadOnlyDictionary<string, string> p, string name)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(given) as JsonObject ?? throw new InvalidOperationException($"The {name} are a JSON object - {{\"plan\": \"pro\"}} ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} are a JSON object - {{\"plan\": \"pro\"}} ({name}).");
            }
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string root;
            private readonly string key;

            private Session(HttpClient client, string root, string key)
            {
                this.client = client;
                this.root = root;
                this.key = key;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not PostHog's address - its ingestion host, {DefaultEndpoint}.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The PostHog connection has no project API key.");
                return new Session(egress.CreateHttpClient(timeout), root, key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the project's key in its body, as PostHog takes it; its JSON, or PostHog's answer said.</summary>
            public async Task<JsonNode?> SendAsync(string route, JsonObject body, CancellationToken cancellationToken)
            {
                body["api_key"] = key;
                var payload = body.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, root + route) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("PostHog answered with what is not JSON - check the address: its ingestion host, a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json;
                }

                var said = json is JsonObject error ? Text(error["detail"]) ?? Text(error["error"]) ?? Text(error["message"]) : null;
                var code = json is JsonObject coded ? Text(coded["code"]) : null;
                throw new InvalidOperationException($"PostHog answered HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}"
                    + ((int)response.StatusCode is 401 or 403 ? " - check the project API key, and that the address is its project's region." : string.Empty));
            }
        }
    }
}
