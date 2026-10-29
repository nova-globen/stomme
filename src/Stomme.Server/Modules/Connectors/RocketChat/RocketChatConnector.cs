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

namespace Stomme.Modules.Connectors.RocketChat
{
    /// <summary>
    /// Rocket.Chat (n8n's Rocket.Chat node: a message posted, the subscriptions, a direct conversation's messages): a
    /// server's REST API with a user's id and personal access token, over the host's egress. A test names whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>post_message</c> (to a channel, a user or a room - with an alias, an emoji or an avatar and attachments),
    /// <c>update_message</c> and <c>delete_message</c>; <c>messages</c> (a channel's, a private group's or a direct
    /// conversation's, page by page by Rocket.Chat's total up to <c>max</c>); <c>channels</c> (every public one, or those
    /// joined), <c>users</c>, <c>subscriptions</c> and <c>mark_read</c>. A record is its fields as Rocket.Chat gives them, a
    /// list or an object as its JSON. Every room, message, channel, kind and attachment held to its shape before anything is
    /// sent; an answer Rocket.Chat does not say succeeded a failure. A throttled call waits as Rocket.Chat says; a failure is
    /// thrown with its status and Rocket.Chat's word.
    /// </remarks>
    public sealed class RocketChatConnector : IConnector, IConnectorExtractor
    {
        public const string PostMessageOperation = "post_message";

        public const string UpdateMessageOperation = "update_message";

        public const string DeleteMessageOperation = "delete_message";

        public const string MessagesOperation = "messages";

        public const string ChannelsOperation = "channels";

        public const string UsersOperation = "users";

        public const string SubscriptionsOperation = "subscriptions";

        public const string MarkReadOperation = "mark_read";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A room's or a message's id as Rocket.Chat makes them - GENERAL, a 17-character id, two of them joined for a direct one.
        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        // Where a message goes: #channel, @user or a room's id.
        private static readonly Regex Channel = new("^([#@][^\\s#@]{1,100}|[A-Za-z0-9_-]{1,64})$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { PostMessageOperation, UpdateMessageOperation, DeleteMessageOperation, MessagesOperation, ChannelsOperation, UsersOperation, SubscriptionsOperation, MarkReadOperation };

        // A room's kind as the API names its messages: a channel's, a private group's, a direct conversation's.
        private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal) { ["channel"] = "channels", ["group"] = "groups", ["dm"] = "dm" };

        public string ConnectorType => "rocketchat";

        public string DisplayName => "Rocket.Chat";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://chat.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "User ID", ["password"] = "Personal access token" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.rocket.chat/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.rocket.chat/apidocs");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField RoomField = Field("room", "Room", "The room's id.", required: true);

        private static readonly ConnectorConfigField MessageField = Field("message", "Message", "The message's id.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PostMessageOperation, "Post a message", "n8n's Rocket.Chat Chat Post Message.")
            {
                Parameters = Schema(
                    Field("channel", "Channel", "#channel, @user or a room's id.", required: true),
                    Field("text", "Text", "What to say.", ConnectorConfigFieldType.Multiline),
                    Field("alias", "Alias", "The name it is shown from."),
                    Field("emoji", "Emoji", "An emoji for its avatar - :coffee:."),
                    Field("avatar", "Avatar", "An image's address for its avatar."),
                    Field("attachments", "Attachments", "A JSON list of Rocket.Chat's attachments - [{\"title\": \"Fika\", \"text\": \"At three\", \"color\": \"#ff0000\"}].", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateMessageOperation, "Update a message", "A message's new text.") { Parameters = Schema(RoomField, MessageField, Field("text", "Text", "Its new text.", ConnectorConfigFieldType.Multiline, required: true)) },
            new ConnectorOperation(DeleteMessageOperation, "Delete a message", "A message deleted.") { Parameters = Schema(RoomField, MessageField) },
            new ConnectorOperation(MessagesOperation, "Get messages", "A room's messages, newest first - n8n's Rocket.Chat Direct Message Get Messages.")
            {
                Parameters = Schema(RoomField, Field("kind", "Kind", "channel (the default), group - a private one - or dm."), MaxField),
            },
            new ConnectorOperation(ChannelsOperation, "Get channels", "Every public channel, or those the user joined.") { Parameters = Schema(Field("joined", "Joined", "true: only those the user joined.", ConnectorConfigFieldType.Boolean), MaxField) },
            new ConnectorOperation(UsersOperation, "Get users", "The server's users.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(SubscriptionsOperation, "Get subscriptions", "The rooms the user is in - n8n's Rocket.Chat Subscription Get Many.") { Parameters = Schema() },
            new ConnectorOperation(MarkReadOperation, "Mark as read", "A room read - n8n's Rocket.Chat Subscription Mark As Read.") { Parameters = Schema(RoomField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/me", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Rocket.Chat: the token is {(Text(me?["name"]) is { Length: > 0 } name ? name : "a user")}'s{(Text(me?["username"]) is { } username ? $" (@{username})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PostMessageOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Rocket.Chat has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string name) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).") : throw new InvalidOperationException($"Give the {name}'s id.");
            var room = operation is UpdateMessageOperation or DeleteMessageOperation or MessagesOperation or MarkReadOperation ? IdOf("room") : null;
            var message = operation is UpdateMessageOperation or DeleteMessageOperation ? IdOf("message") : null;
            var kind = Values.Get(p, "kind")?.ToLowerInvariant() is { } named ? Kinds.TryGetValue(named, out var path) ? path : throw new InvalidOperationException($"'{Values.Get(p, "kind")}' is not channel, group or dm (kind).") : "channels";
            var joined = Values.Get(p, "joined") is { } flag ? flag.ToLowerInvariant() is "true" or "false" ? flag.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{flag}' is not true or false (joined).") : false;

            JsonObject? body = null;
            if (operation == PostMessageOperation)
            {
                var channel = Values.Get(p, "channel") is { } to ? Channel.IsMatch(to) ? to : throw new InvalidOperationException($"'{to}' is not #channel, @user or a room's id (channel).") : throw new InvalidOperationException("Give the channel - #channel, @user or a room's id.");
                body = new JsonObject { ["channel"] = channel };
                foreach (var name in new[] { "text", "alias", "emoji", "avatar" })
                {
                    if (Values.Get(p, name) is { } value)
                    {
                        body[name] = value;
                    }
                }

                if (Values.Get(p, "attachments") is { } attachments)
                {
                    body["attachments"] = Parse(attachments) is JsonArray { Count: > 0 } list && list.All(item => item is JsonObject) ? list : throw new InvalidOperationException("The attachments are not a JSON list of objects - [{\"title\": \"Fika\", \"text\": \"At three\"}].");
                }

                if (!body.ContainsKey("text") && !body.ContainsKey("attachments"))
                {
                    throw new InvalidOperationException("Give the text or attachments.");
                }
            }

            if (operation == UpdateMessageOperation)
            {
                body = new JsonObject { ["roomId"] = room, ["msgId"] = message, ["text"] = Values.Get(p, "text") ?? throw new InvalidOperationException("Give its new text.") };
            }

            using var client = session.Client;
            switch (operation)
            {
                case PostMessageOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, "/chat.postMessage", body, cancellationToken);
                        var posted = (answer?["message"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
                        posted["channel"] = Text(answer?["channel"]) ?? (string?)body!["channel"];
                        yield return Record(posted);
                        yield break;
                    }

                case UpdateMessageOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, "/chat.update", body, cancellationToken))?["message"] as JsonObject ?? new JsonObject());
                    yield break;
                case DeleteMessageOperation:
                    await session.SendAsync(HttpMethod.Post, "/chat.delete", new JsonObject { ["roomId"] = room, ["msgId"] = message }, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["_id"] = message, ["rid"] = room, ["deleted"] = true });
                    yield break;
                case MarkReadOperation:
                    await session.SendAsync(HttpMethod.Post, "/subscriptions.read", new JsonObject { ["rid"] = room }, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["rid"] = room, ["read"] = true });
                    yield break;
                case SubscriptionsOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, "/subscriptions.get", null, cancellationToken))?["update"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;
                default:
                    {
                        // Rocket.Chat pages by an offset and says how many there are: the size fixed at the start, the total or
                        // an empty page the end.
                        var (resource, key, query) = operation switch
                        {
                            MessagesOperation => ($"/{kind}.messages", "messages", "roomId=" + room + "&sort=" + Uri.EscapeDataString("{\"ts\":-1}") + "&"),
                            ChannelsOperation => (joined ? "/channels.list.joined" : "/channels.list", "channels", ""),
                            _ => ("/users.list", "users", ""),
                        };
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"{resource}?{query}offset={read.ToString(CultureInfo.InvariantCulture)}&count={size}", null, cancellationToken);
                            var items = (answer?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count == 0 || answer?["total"] is not JsonValue total || !total.TryGetValue<long>(out var all) || read >= all)
                            {
                                yield break;
                            }
                        }
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

        // A record: its fields as Rocket.Chat gives them, a list or an object as its JSON.
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

        // One connection's calls: the server's API v1 with the user's id and token; an answer Rocket.Chat does not say
        // succeeded a failure; a throttled call waiting as Rocket.Chat says; a failure thrown with its status and Rocket.Chat's
        // word.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string user;
            private readonly string token;

            private Session(string baseUrl, string user, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.user = user;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the server's address - https://chat.example.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^7] : baseUrl;
                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Rocket.Chat connection has no user id.");
                var token = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Rocket.Chat connection has no access token.");
                return new Session(baseUrl + "/api/v1", user, token, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Add("X-User-Id", user);
                    request.Headers.Add("X-Auth-Token", token);
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
                    // A page that is not Rocket.Chat's - a sign-in page, a proxy's - read as no messages would hide that it
                    // never was.
                    throw new InvalidOperationException($"Rocket.Chat answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                var error = json as JsonObject;
                if (!response.IsSuccessStatusCode || error?["success"] is not JsonValue success || !success.TryGetValue<bool>(out var succeeded) || !succeeded)
                {
                    var said = Text(error?["error"]) ?? Text(error?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? (response.IsSuccessStatusCode ? ": Rocket.Chat did not say it succeeded." : ".") : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
