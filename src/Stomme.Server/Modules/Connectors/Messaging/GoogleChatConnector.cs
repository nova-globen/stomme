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

namespace Stomme.Modules.Connectors.Messaging
{
    /// <summary>
    /// Google Chat (n8n's Google Chat node: messages sent, read, edited and deleted, and a workspace's spaces and their
    /// members) over the Chat API (<c>https://chat.googleapis.com/v1</c>) as a person signed in - an OAuth client's refresh token
    /// granted the chat.messages, chat.spaces.readonly and chat.memberships.readonly scopes, exchanged at Google's token endpoint
    /// through the shared <see cref="OAuth2TokenProvider"/>, as Gmail's is. A test lists the spaces the person is in.
    /// </summary>
    /// <remarks>
    /// <para><c>message</c> - the chat connectors' one operation, which Send chat message drives - sends a text <c>to</c> a space
    /// (<c>spaces/AAAA...</c>, or its id) and, <c>reply_to</c> a thread (its name, or a thread key of the author's own), answers in
    /// it - a reply that cannot find its thread starting a new one, as Chat's REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD does; Chat's
    /// refusal - a space not found, no permission - comes back as the record's <c>refused</c>, in Google's words.
    /// <c>message_create</c> sends a whole message's JSON (n8n's JSON parameters: cards and all) with a thread key and a request id;
    /// <c>message_get</c>, <c>message_update</c> (its text, or its cards from JSON, by an update mask of what is given) and
    /// <c>message_delete</c>; <c>spaces</c> and <c>space_get</c>; <c>members</c> and <c>member_get</c>. A list page by page by
    /// Google's page token, up to <c>max</c>.</para>
    /// <para>Every space, message, thread and member name is held to Chat's shape - and so can name nothing outside it - before a
    /// token is asked for. A record is the resource's fields as Chat gives them, a list or an object as its JSON.</para>
    /// </remarks>
    public sealed class GoogleChatConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://chat.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly string[] Known =
        {
            ChatMessages.Operation, "message_create", "message_get", "message_update", "message_delete", "spaces", "space_get", "members", "member_get",
        };

        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Space = new("^spaces/[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Message = new("^spaces/[A-Za-z0-9_-]{1,64}/messages/[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Thread = new("^spaces/[A-Za-z0-9_-]{1,64}/threads/[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Member = new("^spaces/[A-Za-z0-9_-]{1,64}/members/[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Key = new("^[A-Za-z0-9_.:-]{1,256}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlechat";

        public string DisplayName => "Google Chat";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted chat.messages, chat.spaces.readonly and chat.memberships.readonly)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://workspace.google.com/products/chat/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/workspace/chat/api/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField SpaceField = Field("space", "Space", "spaces/AAAA..., or its id.", required: true);

        private static readonly ConnectorConfigField MessageField = Field("message", "Message", "Its name - spaces/AAAA.../messages/BBBB....", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A space - spaces/AAAA..., or its id.", "A thread - its name (spaces/.../threads/...) or a thread key of your own - to answer in it."),
            new ConnectorOperation("message_create", "Send a message's JSON", "A whole message - cards and all - n8n's Message Create with JSON parameters.")
            {
                Parameters = Schema(
                    SpaceField,
                    Field("json", "Message", "The message's JSON - {\"text\": \"...\", \"cardsV2\": [...]}.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("thread_key", "Thread key", "A thread of your own to send it in."),
                    Field("request_id", "Request id", "A unique id so a repeated send is one message.")),
            },
            new ConnectorOperation("message_get", "Get a message", "n8n's Message Get.") { Parameters = Schema(MessageField) },
            new ConnectorOperation("message_update", "Update a message", "Its text, or its cards - n8n's Message Update.")
            {
                Parameters = Schema(
                    MessageField,
                    Field("text", "Text", "Its new text - or, over several lines, in the step's Query box.", ConnectorConfigFieldType.Multiline),
                    Field("json", "Fields", "Its fields as JSON - {\"text\": \"...\", \"cardsV2\": [...]}; only those given change.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation("message_delete", "Delete a message", "n8n's Message Delete.") { Parameters = Schema(MessageField) },
            new ConnectorOperation("spaces", "Get spaces", "The spaces you are in - n8n's Space Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation("space_get", "Get a space", "n8n's Space Get.") { Parameters = Schema(SpaceField) },
            new ConnectorOperation("members", "Get members", "A space's members - n8n's Member Get Many.") { Parameters = Schema(SpaceField, MaxField) },
            new ConnectorOperation("member_get", "Get a member", "n8n's Member Get.") { Parameters = Schema(Field("member", "Member", "Its name - spaces/AAAA.../members/1234....", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var page = await session.JsonAsync(HttpMethod.Get, "/spaces?pageSize=10", null, cancellationToken) as JsonObject;
                var names = (page?["spaces"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(space => Text(space["displayName"]) ?? Text(space["name"])).OfType<string>().ToList();
                return ConnectionTestResult.Ok($"Google Chat: {(names.Count == 0 ? "in no spaces yet" : "in " + string.Join(", ", names) + (page?["nextPageToken"] is not null ? " ..." : string.Empty))}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ChatMessages.Operation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Chat has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string route;
            JsonObject? body = null;
            HttpMethod method = HttpMethod.Get;
            (string To, string Text, string? ReplyTo)? message = null;
            switch (operation)
            {
                case ChatMessages.Operation:
                    {
                        message = ChatMessages.Read(p, out var problem) ?? throw new InvalidOperationException(problem);
                        var space = SpaceOf(message.Value.To);
                        if (space is null)
                        {
                            yield return ChatMessages.Refused(DisplayName, $"'{message.Value.To}' is not a space - spaces/AAAA..., or its id.");
                            yield break;
                        }

                        body = new JsonObject { ["text"] = message.Value.Text };
                        route = "/" + space + "/messages";
                        if (message.Value.ReplyTo is { } reply)
                        {
                            if (Thread.IsMatch(reply) && reply.StartsWith(space + "/", StringComparison.Ordinal))
                            {
                                body["thread"] = new JsonObject { ["name"] = reply };
                            }
                            else if (Key.IsMatch(reply) && !reply.StartsWith("spaces/", StringComparison.Ordinal))
                            {
                                body["thread"] = new JsonObject { ["threadKey"] = reply };
                            }
                            else
                            {
                                yield return ChatMessages.Refused(DisplayName, $"'{reply}' is not one of this space's threads (its name) or a thread key.");
                                yield break;
                            }

                            route += "?messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD";
                        }

                        method = HttpMethod.Post;
                        break;
                    }

                case "message_create":
                    {
                        var space = SpaceOf(Values.Get(p, "space")) ?? throw new InvalidOperationException($"'{Values.Get(p, "space")}' is not a space - spaces/AAAA..., or its id (space).");
                        body = ObjectOf(p, "json") ?? throw new InvalidOperationException("Give the message's JSON - {\"text\": \"...\"} (json).");
                        var query = new List<string>();
                        if (Values.Get(p, "thread_key") is { } key)
                        {
                            query.Add("threadKey=" + Uri.EscapeDataString(Key.IsMatch(key) ? key : throw new InvalidOperationException($"'{key}' is not a thread key - letters, digits, _ . : - (thread_key).")));
                            query.Add("messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD");
                        }

                        if (Values.Get(p, "request_id") is { } requestId)
                        {
                            query.Add("requestId=" + Uri.EscapeDataString(Key.IsMatch(requestId) ? requestId : throw new InvalidOperationException($"'{requestId}' is not a request id - letters, digits, _ . : - (request_id).")));
                        }

                        route = "/" + space + "/messages" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
                        method = HttpMethod.Post;
                        break;
                    }

                case "message_get":
                case "message_delete":
                    route = "/" + NameOf(Values.Get(p, "message"), Message, "message", "spaces/AAAA.../messages/BBBB...");
                    method = operation == "message_delete" ? HttpMethod.Delete : HttpMethod.Get;
                    break;
                case "message_update":
                    {
                        route = "/" + NameOf(Values.Get(p, "message"), Message, "message", "spaces/AAAA.../messages/BBBB...");
                        body = ObjectOf(p, "json") ?? new JsonObject();
                        if ((p.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text) ? text : p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : null) is { } given)
                        {
                            body["text"] = given; // its text, or over several lines in the step's Query box
                        }

                        var mask = body.Select(field => field.Key).Where(field => field is "text" or "cardsV2" or "attachment" or "accessoryWidgets").ToList();
                        if (mask.Count == 0 || mask.Count != body.Count)
                        {
                            throw new InvalidOperationException("Give what changes - its text, or its fields as JSON: text, cardsV2, attachment or accessoryWidgets.");
                        }

                        route += "?updateMask=" + string.Join(",", mask);
                        method = HttpMethod.Patch;
                        break;
                    }

                case "space_get":
                    route = "/" + (SpaceOf(Values.Get(p, "space")) ?? throw new InvalidOperationException($"'{Values.Get(p, "space")}' is not a space - spaces/AAAA..., or its id (space)."));
                    break;
                case "members":
                    route = "/" + (SpaceOf(Values.Get(p, "space")) ?? throw new InvalidOperationException($"'{Values.Get(p, "space")}' is not a space - spaces/AAAA..., or its id (space).")) + "/members";
                    break;
                case "member_get":
                    route = "/" + NameOf(Values.Get(p, "member"), Member, "member", "spaces/AAAA.../members/1234...");
                    break;
                default: // spaces
                    route = "/spaces";
                    break;
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case ChatMessages.Operation:
                    {
                        var (status, json) = await session.SendAsync(method, route, body, cancellationToken);
                        if (status is >= 200 and < 300 && Text(json?["name"]) is { Length: > 0 } name)
                        {
                            yield return ChatMessages.Sent(name, SpaceOf(message!.Value.To)!);
                        }
                        else if (status is >= 400 and < 500 && json?["error"]?["message"] is { } reason)
                        {
                            yield return ChatMessages.Refused(DisplayName, ChatMessages.Text(reason));
                        }
                        else
                        {
                            throw new InvalidOperationException($"HTTP {status}: not Google Chat's answer.");
                        }

                        break;
                    }

                case "spaces":
                case "members":
                    {
                        // Page by page by Google's page token, a page no larger than what is still wanted.
                        var list = operation == "spaces" ? "spaces" : "memberships";
                        var read = 0;
                        string? next = null;
                        do
                        {
                            var page = await session.JsonAsync(HttpMethod.Get, route + "?pageSize=" + Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture) + (next is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(next)), null, cancellationToken) as JsonObject;
                            foreach (var item in (page?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(item);
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

                case "message_delete":
                    await session.JsonAsync(method, route, null, cancellationToken);
                    yield return Fields(("name", route[1..]), ("deleted", true));
                    break;
                default:
                    yield return Record(await session.JsonAsync(method, route, body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
            }
        }

        /// <summary>A space's name from its name or its bare id; null when it is neither.</summary>
        public static string? SpaceOf(string? given) =>
            given?.Trim() is not { Length: > 0 } value ? null : Space.IsMatch(value) ? value : Id.IsMatch(value) ? "spaces/" + value : null;

        private static string NameOf(string? given, Regex shape, string what, string example) =>
            given?.Trim() is { Length: > 0 } value && shape.IsMatch(value) ? value
            : throw new InvalidOperationException(given is null ? $"Give the {what}'s name - {example} ({what})." : $"'{given}' is not a {what}'s name - {example} ({what}).");

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

        /// <summary>One signed-in person's Chat: the API's address and the access token.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;

            private Session(HttpClient client, string token, string api)
            {
                this.client = client;
                this.token = token;
                this.api = api;
            }

            public static async Task<Session> OpenAsync(GoogleChatConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/v1", StringComparison.Ordinal))
                {
                    root = root[..^"/v1".Length]; // the API's versioned address given: its own
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Google Chat's address - its own, {DefaultEndpoint}.");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, root + "/v1");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's status and its JSON, whatever the status - for a send whose refusal is a record.</summary>
            public async Task<(int Status, JsonNode? Json)> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    return ((int)response.StatusCode, string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer));
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Google Chat answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    return ((int)response.StatusCode, null);
                }
            }

            /// <summary>A call's JSON, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var (status, json) = await SendAsync(method, route, body, cancellationToken);
                if (status is >= 200 and < 300)
                {
                    return json;
                }

                var error = json?["error"];
                var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                throw new InvalidOperationException($"Google Chat answered HTTP {status}{(Text(error?["status"]) is { } kind ? " " + kind : string.Empty)}{(message is null ? "." : ": " + message)}"
                    + (status is 401 or 403 ? " - check the refresh token and its scopes." : string.Empty));
            }
        }
    }
}
