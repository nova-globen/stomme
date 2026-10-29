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

namespace Stomme.Modules.Connectors.Gotify
{
    /// <summary>
    /// Gotify (n8n's Gotify node: its messages sent, read and deleted): a self-hosted Gotify server's API with an
    /// application's token to send and a client's token to read and delete, each in X-Gotify-Key, over the host's egress. A
    /// test names whose the client token is.
    /// </summary>
    /// <remarks>
    /// <c>create_message</c> sends a message - a title, a priority from 0 to 10, plain text or Markdown, an address a click
    /// opens, a big image and an Android intent; <c>messages</c> reads them newest first, page by page by Gotify's since up to
    /// <c>max</c> as long as Gotify names a next page - its since, never its address; <c>delete_message</c> deletes one;
    /// <c>applications</c> lists the server's applications. A record is its fields as Gotify gives them, a list or an object as
    /// its JSON. Every id, priority, address and content type held to its shape before anything is sent. A throttled call
    /// waits as Gotify says; a failure is thrown with its status and Gotify's words.
    /// </remarks>
    public sealed class GotifyConnector : IConnector, IConnectorExtractor
    {
        public const string CreateMessageOperation = "create_message";

        public const string MessagesOperation = "messages";

        public const string DeleteMessageOperation = "delete_message";

        public const string ApplicationsOperation = "applications";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { CreateMessageOperation, MessagesOperation, DeleteMessageOperation, ApplicationsOperation };

        public string ConnectorType => "gotify";

        public string DisplayName => "Gotify";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://gotify.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Application token (Apps > an app - to send)",
            ["password"] = "Client token (Clients > a client - to read and delete)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://gotify.net/");

        public Uri ApiDocumentationUrl { get; } = new("https://gotify.net/api-docs");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CreateMessageOperation, "Send a message", "n8n's Gotify Message Create.")
            {
                Parameters = Schema(
                    Field("message", "Message", "What it says.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("title", "Title", "Its title."),
                    Field("priority", "Priority", "0 to 10.", ConnectorConfigFieldType.Integer),
                    Field("content_type", "Content type", "text/plain (the default) or text/markdown."),
                    Field("click_url", "Click address", "The address a click opens."),
                    Field("big_image_url", "Big image", "An image's address shown with it."),
                    Field("intent_url", "Android intent", "An address Android opens on receiving it.")),
            },
            new ConnectorOperation(MessagesOperation, "Get messages", "Newest first - n8n's Gotify Message Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(DeleteMessageOperation, "Delete a message", "n8n's Gotify Message Delete.") { Parameters = Schema(Field("id", "Id", "The message's id.", required: true)) },
            new ConnectorOperation(ApplicationsOperation, "Get applications", "The server's applications.") { Parameters = Schema() },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var user = await session.SendAsync(HttpMethod.Get, "/current/user", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Gotify: the client token is {(Text(user?["name"]) is { Length: > 0 } name ? name : "a user")}'s{(user?["admin"] is JsonValue admin && admin.TryGetValue<bool>(out var isAdmin) && isAdmin ? ", an admin" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? CreateMessageOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Gotify has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? AddressOf(string name) => Values.Get(p, name) is { } given ? Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out _) ? given : throw new InvalidOperationException($"'{given}' is not an address ({name}).") : null;
            JsonObject? body = null;
            string? id = null;
            switch (operation)
            {
                case CreateMessageOperation:
                    {
                        body = new JsonObject { ["message"] = Values.Get(p, "message") ?? throw new InvalidOperationException("Give the message.") };
                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = title;
                        }

                        if (Values.Get(p, "priority") is { } priority)
                        {
                            body["priority"] = int.TryParse(priority, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level <= 10 ? level : throw new InvalidOperationException($"'{priority}' is not a whole number from 0 to 10 (priority).");
                        }

                        var extras = new JsonObject();
                        if (Values.Get(p, "content_type") is { } type)
                        {
                            extras["client::display"] = new JsonObject { ["contentType"] = type.ToLowerInvariant() is "text/plain" or "text/markdown" ? type.ToLowerInvariant() : throw new InvalidOperationException($"'{type}' is not text/plain or text/markdown (content_type).") };
                        }

                        var notification = new JsonObject();
                        if (AddressOf("click_url") is { } click)
                        {
                            notification["click"] = new JsonObject { ["url"] = click };
                        }

                        if (AddressOf("big_image_url") is { } image)
                        {
                            notification["bigImageUrl"] = image;
                        }

                        if (notification.Count > 0)
                        {
                            extras["client::notification"] = notification;
                        }

                        if (AddressOf("intent_url") is { } intent)
                        {
                            extras["android::action"] = new JsonObject { ["onReceive"] = new JsonObject { ["intentUrl"] = intent } };
                        }

                        if (extras.Count > 0)
                        {
                            body["extras"] = extras;
                        }

                        break;
                    }

                case DeleteMessageOperation:
                    id = Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id (id).") : throw new InvalidOperationException("Give the message's id.");
                    break;
            }

            using var client = session.Client;
            switch (operation)
            {
                case CreateMessageOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Post, "/message", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteMessageOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/message/{id}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
                    yield break;
                case ApplicationsOperation:
                    foreach (var item in (await session.SendAsync(HttpMethod.Get, "/application", null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;
                default:
                    {
                        // Newest first: each page the messages before Gotify's since - its since read, never the address beside it -
                        // the size fixed at the start, an empty page or no next the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        long? since = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/message?limit={size}" + (since is null ? "" : "&since=" + since.Value.ToString(CultureInfo.InvariantCulture)), null, cancellationToken);
                            var items = (answer?["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            var paging = answer?["paging"] as JsonObject;
                            if (items.Count == 0 || paging?["next"] is null || paging["since"] is not JsonValue next || !next.TryGetValue<long>(out var value) || value <= 0)
                            {
                                yield break;
                            }

                            since = value;
                        }
                    }
            }
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        // One connection's calls: the server's API with the application's token in X-Gotify-Key to send and the client's to
        // read and delete; a throttled call waiting as Gotify says; a failure thrown with its status and Gotify's words.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string application;
            private readonly string client;

            private Session(string baseUrl, string application, string client, HttpClient http)
            {
                this.baseUrl = baseUrl;
                this.application = application;
                this.client = client;
                Client = http;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the Gotify server's address - https://gotify.example.com.");
                }

                return new Session(
                    Values.BaseUrl(endpoint, ""),
                    Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Gotify connection has no application token."),
                    Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Gotify connection has no client token."),
                    egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Add("X-Gotify-Key", method == HttpMethod.Post ? application : client);
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
                    // A page that is not the server's - a sign-in page, a proxy's - read as no messages would hide that it never was.
                    throw new InvalidOperationException($"Gotify answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var said = string.Join(": ", new[] { Text(error?["error"]), Text(error?["errorDescription"]) }.Where(part => !string.IsNullOrEmpty(part)));
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
