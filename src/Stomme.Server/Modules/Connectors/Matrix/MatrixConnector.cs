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

namespace Stomme.Modules.Connectors.Matrix
{
    /// <summary>
    /// Matrix (n8n's Matrix node: messages sent and read, rooms made, joined and left, people invited and kicked): a
    /// homeserver's client-server API with an access token as a bearer token, over the host's egress. A test names whose the
    /// token is.
    /// </summary>
    /// <remarks>
    /// <c>me</c>, <c>rooms</c> (the rooms joined), <c>create_room</c> (its name, topic, preset, alias, invitees, direct),
    /// <c>join_room</c> (by id or alias), <c>leave_room</c>, <c>invite</c> and <c>kick</c> (with a reason);
    /// <c>send_message</c> (a text, a notice or an emote - with HTML and its plain text), <c>messages</c> (a room's,
    /// newest first, with a filter - page by page by the homeserver's token up to <c>max</c>), <c>event</c> and
    /// <c>members</c> (by membership). An event is its fields as the homeserver gives them - its content's body, type,
    /// membership and display name its own too - a list or an object as its JSON. Every room, alias, user, event, preset, type and membership held to
    /// its shape before anything is sent. A throttled call waits as the homeserver says; a failure is thrown with its status,
    /// Matrix's code and its word.
    /// </remarks>
    public sealed class MatrixConnector : IConnector, IConnectorExtractor
    {
        public const string MeOperation = "me";

        public const string RoomsOperation = "rooms";

        public const string CreateRoomOperation = "create_room";

        public const string JoinRoomOperation = "join_room";

        public const string LeaveRoomOperation = "leave_room";

        public const string InviteOperation = "invite";

        public const string KickOperation = "kick";

        public const string SendMessageOperation = "send_message";

        public const string MessagesOperation = "messages";

        public const string EventOperation = "event";

        public const string MembersOperation = "members";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A room version 12 room's id has no server - !abc; an older room's has - !abc:example.org.
        private static readonly Regex RoomId = new("^![^\\s/:]+(:[^\\s/]+)?$", RegexOptions.CultureInvariant);

        private static readonly Regex Alias = new("^#[^\\s:/]+:[^\\s/]+$", RegexOptions.CultureInvariant);

        private static readonly Regex UserId = new("^@[^\\s:/]+:[^\\s/]+$", RegexOptions.CultureInvariant);

        private static readonly Regex EventId = new("^\\$[^\\s/]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { MeOperation, RoomsOperation, CreateRoomOperation, JoinRoomOperation, LeaveRoomOperation, InviteOperation, KickOperation, SendMessageOperation, MessagesOperation, EventOperation, MembersOperation };

        private static readonly string[] Presets = { "public_chat", "private_chat", "trusted_private_chat" };

        private static readonly string[] Types = { "m.text", "m.notice", "m.emote" };

        private static readonly string[] Memberships = { "join", "invite", "leave", "ban", "knock" };

        public string ConnectorType => "matrix";

        public string DisplayName => "Matrix";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => "https://matrix.example.org";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Access token (Element: Settings > Help & About)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://matrix.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://spec.matrix.org/latest/client-server-api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField RoomField = Field("room", "Room", "The room's id - !abc:example.org.", required: true);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's id - @ada:example.org.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(MeOperation, "Who am I", "The token's user - n8n's Matrix Account Me.") { Parameters = Schema() },
            new ConnectorOperation(RoomsOperation, "Get rooms", "The rooms the token's user is in.") { Parameters = Schema() },
            new ConnectorOperation(CreateRoomOperation, "Create a room", "n8n's Matrix Room Create.")
            {
                Parameters = Schema(
                    Field("name", "Name", "Its name."),
                    Field("topic", "Topic", "Its topic."),
                    Field("preset", "Preset", "public_chat, private_chat or trusted_private_chat."),
                    Field("alias", "Alias", "Its local alias - fika, for #fika:example.org."),
                    Field("invite", "Invite", "Comma-separated user ids - @ada:example.org."),
                    Field("direct", "Direct", "true: a direct conversation.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(JoinRoomOperation, "Join a room", "By its id or alias - n8n's Matrix Room Join.") { Parameters = Schema(Field("room", "Room", "The room's id or alias - #fika:example.org.", required: true)) },
            new ConnectorOperation(LeaveRoomOperation, "Leave a room", "n8n's Matrix Room Leave.") { Parameters = Schema(RoomField) },
            new ConnectorOperation(InviteOperation, "Invite to a room", "n8n's Matrix Room Invite.") { Parameters = Schema(RoomField, UserField, Field("reason", "Reason", "Why.")) },
            new ConnectorOperation(KickOperation, "Kick from a room", "n8n's Matrix Room Kick.") { Parameters = Schema(RoomField, UserField, Field("reason", "Reason", "Why.")) },
            new ConnectorOperation(SendMessageOperation, "Send a message", "n8n's Matrix Message Create.")
            {
                Parameters = Schema(
                    RoomField,
                    Field("text", "Text", "What to say - the plain text when HTML is given.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("html", "HTML", "The message as HTML - <b>Fika</b>.", ConnectorConfigFieldType.Multiline),
                    Field("type", "Type", "m.text (the default), m.notice or m.emote.")),
            },
            new ConnectorOperation(MessagesOperation, "Get messages", "A room's events, newest first - n8n's Matrix Message Get Many.")
            {
                Parameters = Schema(RoomField, Field("filter", "Filter", "A JSON room event filter - {\"types\": [\"m.room.message\"]}.", ConnectorConfigFieldType.Multiline), MaxField),
            },
            new ConnectorOperation(EventOperation, "Get an event", "n8n's Matrix Event Get.") { Parameters = Schema(RoomField, Field("event", "Event", "The event's id - $abc.", required: true)) },
            new ConnectorOperation(MembersOperation, "Get members", "A room's members - n8n's Matrix Room Member Get Many.")
            {
                Parameters = Schema(RoomField, Field("membership", "Membership", "join, invite, leave, ban or knock."), Field("not_membership", "Not membership", "join, invite, leave, ban or knock - those left out.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/account/whoami", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Matrix: the token is {Text(me?["user_id"]) ?? "a user"}'s{(Text(me?["device_id"]) is { } device ? $" (device {device})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendMessageOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Matrix has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string Of(string name, Regex shape, string what, string example) => Values.Get(p, name) is { } given ? shape.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} - {example} ({name}).") : throw new InvalidOperationException($"Give the {name} - {example}.");
            var room = operation switch
            {
                JoinRoomOperation => Values.Get(p, "room") is { } given ? RoomId.IsMatch(given) || Alias.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a room's id or alias - #fika:example.org (room).") : throw new InvalidOperationException("Give the room - !abc:example.org or #fika:example.org."),
                LeaveRoomOperation or InviteOperation or KickOperation or SendMessageOperation or MessagesOperation or EventOperation or MembersOperation => Of("room", RoomId, "a room's id", "!abc:example.org"),
                _ => null,
            };
            var user = operation is InviteOperation or KickOperation ? Of("user", UserId, "a user's id", "@ada:example.org") : null;
            var eventId = operation == EventOperation ? Of("event", EventId, "an event's id", "$abc") : null;

            JsonObject? body = null;
            if (operation == CreateRoomOperation)
            {
                body = new JsonObject();
                foreach (var name in new[] { "name", "topic" })
                {
                    if (Values.Get(p, name) is { } value)
                    {
                        body[name] = value;
                    }
                }

                if (Values.Get(p, "preset") is { } preset)
                {
                    body["preset"] = Presets.Contains(preset.ToLowerInvariant()) ? preset.ToLowerInvariant() : throw new InvalidOperationException($"'{preset}' is not public_chat, private_chat or trusted_private_chat (preset).");
                }

                if (Values.Get(p, "alias") is { } alias)
                {
                    body["room_alias_name"] = Regex.IsMatch(alias, "^[^\\s:#]{1,255}$") ? alias : throw new InvalidOperationException($"'{alias}' is not a room's local alias - fika (alias).");
                }

                if (Values.Get(p, "invite") is { } invite)
                {
                    body["invite"] = new JsonArray(invite.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(id => (JsonNode?)JsonValue.Create(UserId.IsMatch(id) ? id : throw new InvalidOperationException($"'{id}' is not a user's id - @ada:example.org (invite)."))).ToArray());
                }

                if (Values.Get(p, "direct") is { } direct)
                {
                    body["is_direct"] = direct.ToLowerInvariant() is "true" or "false" ? direct.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{direct}' is not true or false (direct).");
                }
            }

            if (operation is InviteOperation or KickOperation)
            {
                body = new JsonObject { ["user_id"] = user };
                if (Values.Get(p, "reason") is { } reason)
                {
                    body["reason"] = reason;
                }
            }

            if (operation == SendMessageOperation)
            {
                var type = Values.Get(p, "type") is { } given ? Types.Contains(given.ToLowerInvariant()) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not m.text, m.notice or m.emote (type).") : "m.text";
                body = new JsonObject { ["msgtype"] = type, ["body"] = Values.Get(p, "text") ?? throw new InvalidOperationException("Give the text - the plain text when HTML is given too.") };
                if (Values.Get(p, "html") is { } html)
                {
                    body["format"] = "org.matrix.custom.html";
                    body["formatted_body"] = html;
                }
            }

            var query = new List<string>();
            if (operation == MessagesOperation && Values.Get(p, "filter") is { } filter)
            {
                query.Add("filter=" + Uri.EscapeDataString(Parse(filter) is JsonObject parsed ? parsed.ToJsonString(Wire) : throw new InvalidOperationException("The filter is not a JSON object - {\"types\": [\"m.room.message\"]}.")));
            }

            if (operation == MembersOperation)
            {
                foreach (var name in new[] { "membership", "not_membership" })
                {
                    if (Values.Get(p, name) is { } membership)
                    {
                        query.Add(name + "=" + (Memberships.Contains(membership.ToLowerInvariant()) ? membership.ToLowerInvariant() : throw new InvalidOperationException($"'{membership}' is not join, invite, leave, ban or knock ({name}).")));
                    }
                }
            }

            var roomPath = room is null ? null : Uri.EscapeDataString(room);
            using var client = session.Client;
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case MeOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, "/account/whoami", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case RoomsOperation:
                    foreach (var id in ((await session.SendAsync(HttpMethod.Get, "/joined_rooms", null, cancellationToken))?["joined_rooms"] as JsonArray ?? new JsonArray()).Select(Text).OfType<string>().Take(max))
                    {
                        yield return Done(("room_id", id));
                    }

                    yield break;
                case CreateRoomOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Post, "/createRoom", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case JoinRoomOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Post, $"/join/{roomPath}", new JsonObject(), cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case LeaveRoomOperation:
                    await session.SendAsync(HttpMethod.Post, $"/rooms/{roomPath}/leave", new JsonObject(), cancellationToken);
                    yield return Done(("room_id", room), ("left", true));
                    yield break;
                case InviteOperation:
                    await session.SendAsync(HttpMethod.Post, $"/rooms/{roomPath}/invite", body, cancellationToken);
                    yield return Done(("room_id", room), ("user_id", user), ("invited", true));
                    yield break;
                case KickOperation:
                    await session.SendAsync(HttpMethod.Post, $"/rooms/{roomPath}/kick", body, cancellationToken);
                    yield return Done(("room_id", room), ("user_id", user), ("kicked", true));
                    yield break;
                case SendMessageOperation:
                    {
                        // A transaction id of its own, so a retried send is the same message, never a second.
                        var sent = await session.SendAsync(HttpMethod.Put, $"/rooms/{roomPath}/send/m.room.message/stomme{Guid.NewGuid():N}", body, cancellationToken) as JsonObject;
                        yield return Done(("room_id", room), ("event_id", Text(sent?["event_id"])));
                        yield break;
                    }

                case EventOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, $"/rooms/{roomPath}/event/{Uri.EscapeDataString(eventId!)}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case MembersOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, $"/rooms/{roomPath}/members" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, cancellationToken))?["chunk"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;
                default:
                    {
                        // The homeserver pages back from the newest by a token: the size fixed at the start, an empty chunk or
                        // no next token the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? from = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/rooms/{roomPath}/messages?" + string.Join("&", new[] { "dir=b", $"limit={size}" }.Concat(from is null ? Array.Empty<string>() : new[] { "from=" + Uri.EscapeDataString(from) }).Concat(query)), null, cancellationToken);
                            var items = (answer?["chunk"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            from = Text(answer?["end"]);
                            if (items.Count == 0 || from is not { Length: > 0 })
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

        // A record: its fields as the homeserver gives them - an event's content's body, type, membership and display name its
        // own too - a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);
            if (item["content"] is JsonObject content)
            {
                foreach (var name in new[] { "body", "msgtype", "membership", "displayname" })
                {
                    if (Text(content[name]) is { } value)
                    {
                        fields.TryAdd(name, value);
                    }
                }
            }

            return new ConnectorRecord(fields);
        }

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

        // One connection's calls: the homeserver's client-server API v3 with the token as a bearer token; a throttled call
        // waiting as it says; a failure thrown with its status, Matrix's code and its word.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the homeserver's address - https://matrix.example.org.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/_matrix/client/v3", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^18] : baseUrl;
                return new Session(baseUrl + "/_matrix/client/v3", Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Matrix connection has no access token."), egress.CreateHttpClient(timeout));
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
                    // A page that is not the homeserver's - a sign-in page, a proxy's - read as no messages would hide that it
                    // never was.
                    throw new InvalidOperationException($"Matrix answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var code = Text(error?["errcode"]);
                    var said = Text(error?["error"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
