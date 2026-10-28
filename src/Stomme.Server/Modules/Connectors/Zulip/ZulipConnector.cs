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

namespace Stomme.Modules.Connectors.Zulip
{
    /// <summary>
    /// Zulip (BL-229, n8n's Zulip node: messages sent, got, updated and deleted, streams and users kept): an organization's REST
    /// API with a bot's - or a user's - e-mail and API key, over the host's egress. A test names whose the key is.
    /// </summary>
    /// <remarks>
    /// <c>send_stream</c> (to a stream's topic), <c>send_direct</c> (to people), <c>message</c>, <c>messages</c> (a stream's,
    /// a topic's, a sender's, searched - newest first, page by page up to <c>max</c>), <c>update_message</c>,
    /// <c>delete_message</c>; <c>streams</c>, <c>subscriptions</c>, <c>create_stream</c>, <c>update_stream</c>,
    /// <c>delete_stream</c>; <c>users</c>, <c>user</c>, <c>create_user</c>, <c>update_user</c> and <c>deactivate_user</c>. A
    /// message, a stream or a user is its fields as Zulip gives them - a message's subject its topic too - a list or an object
    /// as its JSON. Every id, address, stream, role and mode held to its shape before anything is sent. A throttled call waits
    /// as Zulip says; a failure is thrown with its status, Zulip's code and its word.
    /// </remarks>
    public sealed class ZulipConnector : IConnector, IConnectorExtractor
    {
        public const string SendStreamOperation = "send_stream";

        public const string SendDirectOperation = "send_direct";

        public const string MessageOperation = "message";

        public const string MessagesOperation = "messages";

        public const string UpdateMessageOperation = "update_message";

        public const string DeleteMessageOperation = "delete_message";

        public const string StreamsOperation = "streams";

        public const string SubscriptionsOperation = "subscriptions";

        public const string CreateStreamOperation = "create_stream";

        public const string UpdateStreamOperation = "update_stream";

        public const string DeleteStreamOperation = "delete_stream";

        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string CreateUserOperation = "create_user";

        public const string UpdateUserOperation = "update_user";

        public const string DeactivateUserOperation = "deactivate_user";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SendStreamOperation, SendDirectOperation, MessageOperation, MessagesOperation, UpdateMessageOperation, DeleteMessageOperation, StreamsOperation, SubscriptionsOperation, CreateStreamOperation, UpdateStreamOperation, DeleteStreamOperation, UsersOperation, UserOperation, CreateUserOperation, UpdateUserOperation, DeactivateUserOperation };

        // Zulip's roles by name: owner, administrator, moderator, member, guest.
        private static readonly Dictionary<string, int> Roles = new(StringComparer.OrdinalIgnoreCase) { ["owner"] = 100, ["administrator"] = 200, ["moderator"] = 300, ["member"] = 400, ["guest"] = 600 };

        public string ConnectorType => "zulip";

        public string DisplayName => "Zulip";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://chat.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://zulip.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://zulip.com/api/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MessageField = Field("id", "Message", "The message's id.", required: true);

        private static readonly ConnectorConfigField StreamIdField = Field("id", "Stream", "The stream's id.", required: true);

        private static readonly ConnectorConfigField UserField = Field("id", "User", "The user's id - or, to get one, an e-mail.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendStreamOperation, "Send to a stream", "n8n's Zulip Message Send to Stream.") { Parameters = Schema(Field("stream", "Stream", "The stream's name or id.", required: true), Field("topic", "Topic", "The topic.", required: true), Field("content", "Message", "The message, in Zulip's Markdown.", ConnectorConfigFieldType.Multiline, required: true)) },
            new ConnectorOperation(SendDirectOperation, "Send directly", "To one or more people - n8n's Zulip Message Send Private.") { Parameters = Schema(Field("to", "To", "Comma-separated e-mails or user ids.", required: true), Field("content", "Message", "The message, in Zulip's Markdown.", ConnectorConfigFieldType.Multiline, required: true)) },
            new ConnectorOperation(MessageOperation, "Get a message", "Its fields and its Markdown - n8n's Zulip Message Get.") { Parameters = Schema(MessageField) },
            new ConnectorOperation(MessagesOperation, "Get messages", "Newest first - a stream's, a topic's, a sender's, searched.")
            {
                Parameters = Schema(Field("stream", "Stream", "The stream's name."), Field("topic", "Topic", "The topic."), Field("sender", "Sender", "The sender's e-mail."), Field("search", "Search", "Words in a message."), MaxField),
            },
            new ConnectorOperation(UpdateMessageOperation, "Update a message", "Its text or topic - n8n's Zulip Message Update.")
            {
                Parameters = Schema(MessageField, Field("content", "Message", "Its new text.", ConnectorConfigFieldType.Multiline), Field("topic", "Topic", "Its new topic."), Field("propagate_mode", "Move", "change_one (the default), change_later or change_all - which of the topic's messages move with it.")),
            },
            new ConnectorOperation(DeleteMessageOperation, "Delete a message", "n8n's Zulip Message Delete.") { Parameters = Schema(MessageField) },
            new ConnectorOperation(StreamsOperation, "Get streams", "The streams the key can see - n8n's Zulip Stream Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(SubscriptionsOperation, "Get subscribed streams", "n8n's Zulip Stream Get Subscribed.") { Parameters = Schema(Field("subscribers", "Subscribers", "true: each stream's subscribers too.", ConnectorConfigFieldType.Boolean), MaxField) },
            new ConnectorOperation(CreateStreamOperation, "Create a stream", "Subscribes to it, making it when it is new - n8n's Zulip Stream Create.")
            {
                Parameters = Schema(Field("name", "Name", "The stream's name.", required: true), Field("description", "Description", "What it is for."), Field("private", "Private", "true: invite only.", ConnectorConfigFieldType.Boolean), Field("principals", "Subscribers", "Comma-separated e-mails or user ids to subscribe - the key's own when left empty."), Field("announce", "Announce", "true: say so in the announcements stream.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(UpdateStreamOperation, "Update a stream", "n8n's Zulip Stream Update.")
            {
                Parameters = Schema(StreamIdField, Field("new_name", "New name", "Its new name."), Field("description", "Description", "Its new description."), Field("private", "Private", "true or false.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(DeleteStreamOperation, "Delete a stream", "Archives it - n8n's Zulip Stream Delete.") { Parameters = Schema(StreamIdField) },
            new ConnectorOperation(UsersOperation, "Get users", "The organization's users - n8n's Zulip User Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's Zulip User Get.") { Parameters = Schema(UserField) },
            new ConnectorOperation(CreateUserOperation, "Create a user", "n8n's Zulip User Create.")
            {
                Parameters = Schema(Field("email", "E-mail", "Their e-mail.", required: true), Field("full_name", "Name", "Their full name.", required: true), Field("password", "Password", "Their first password.", ConnectorConfigFieldType.Secret, required: true)),
            },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "n8n's Zulip User Update.") { Parameters = Schema(UserField, Field("full_name", "Name", "Their new full name."), Field("role", "Role", "owner, administrator, moderator, member or guest.")) },
            new ConnectorOperation(DeactivateUserOperation, "Deactivate a user", "n8n's Zulip User Deactivate.") { Parameters = Schema(UserField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/users/me", null, cancellationToken);
                return ConnectionTestResult.Ok($"Zulip: the key is {Text(me?["full_name"]) ?? "a user"}'s{(Text(me?["email"]) is { } email ? $" ({email})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendStreamOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Zulip has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? id = null;
            if (operation is MessageOperation or UpdateMessageOperation or DeleteMessageOperation or UpdateStreamOperation or DeleteStreamOperation or UserOperation or UpdateUserOperation or DeactivateUserOperation)
            {
                id = Values.Get(p, "id") is { } given
                    ? Id.IsMatch(given) || (operation == UserOperation && Email.IsMatch(given)) ? given : throw new InvalidOperationException($"'{given}' is not an id{(operation == UserOperation ? " or an e-mail" : "")}.")
                    : throw new InvalidOperationException("Give the id.");
            }

            string Required(string name, string what) => Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what}.");
            string People(string name, string value) => JsonSerializer.Serialize(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(person => Id.IsMatch(person) ? (object)long.Parse(person, CultureInfo.InvariantCulture) : Email.IsMatch(person) ? person : throw new InvalidOperationException($"'{person}' is not an e-mail or a user id ({name}).")).ToArray(), Wire);
            string? Flag(string name) => Values.Get(p, name) is { } flag ? flag.ToLowerInvariant() is "true" or "false" ? flag.ToLowerInvariant() : throw new InvalidOperationException($"'{flag}' is not true or false ({name}).") : null;

            var form = new List<KeyValuePair<string, string>>();
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    form.Add(new(name, value));
                }
            }

            switch (operation)
            {
                case SendStreamOperation:
                    Add("type", "stream");
                    Add("to", Required("stream", "the stream - its name or id"));
                    Add("topic", Required("topic", "the topic"));
                    Add("content", Required("content", "the message"));
                    break;
                case SendDirectOperation:
                    Add("type", "direct");
                    Add("to", People("to", Required("to", "who it is to - e-mails or user ids")));
                    Add("content", Required("content", "the message"));
                    break;
                case UpdateMessageOperation:
                    Add("content", Values.Get(p, "content"));
                    Add("topic", Values.Get(p, "topic"));
                    Add("propagate_mode", Values.Get(p, "propagate_mode") is { } mode ? mode.ToLowerInvariant() is "change_one" or "change_later" or "change_all" ? mode.ToLowerInvariant() : throw new InvalidOperationException($"'{mode}' is not change_one, change_later or change_all (propagate_mode).") : null);
                    if (form.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - the message or its topic.");
                    }

                    break;
                case SubscriptionsOperation:
                    Add("include_subscribers", Flag("subscribers"));
                    break;
                case CreateStreamOperation:
                    var stream = new JsonObject { ["name"] = Required("name", "the stream's name") };
                    if (Values.Get(p, "description") is { } description)
                    {
                        stream["description"] = description;
                    }

                    Add("subscriptions", new JsonArray(stream).ToJsonString(Wire));
                    Add("invite_only", Flag("private"));
                    Add("principals", Values.Get(p, "principals") is { } principals ? People("principals", principals) : null);
                    Add("announce", Flag("announce"));
                    break;
                case UpdateStreamOperation:
                    Add("new_name", Values.Get(p, "new_name"));
                    Add("description", Values.Get(p, "description"));
                    Add("is_private", Flag("private"));
                    if (form.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - its name, description or privacy.");
                    }

                    break;
                case CreateUserOperation:
                    Add("email", Required("email", "their e-mail") is var address && Email.IsMatch(address) ? address : throw new InvalidOperationException($"'{address}' is not an e-mail (email)."));
                    Add("full_name", Required("full_name", "their name"));
                    Add("password", Required("password", "their first password"));
                    break;
                case UpdateUserOperation:
                    Add("full_name", Values.Get(p, "full_name"));
                    Add("role", Values.Get(p, "role") is { } role ? Roles.TryGetValue(role, out var level) ? level.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{role}' is not owner, administrator, moderator, member or guest (role).") : null);
                    if (form.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - their name or role.");
                    }

                    break;
            }

            using var client = session.Client;
            switch (operation)
            {
                case SendStreamOperation:
                case SendDirectOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, "/messages", form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Number(answer?["id"]), ["sent"] = true });
                        yield break;
                    }

                case MessageOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/messages/{id}", null, cancellationToken);
                        var fields = Fields(answer?["message"] as JsonObject ?? new JsonObject());
                        fields.TryAdd("raw_content", Text(answer?["raw_content"]));
                        yield return new ConnectorRecord(fields);
                        yield break;
                    }

                case MessagesOperation:
                    {
                        var narrow = new JsonArray();
                        foreach (var (name, operator_) in new[] { ("stream", "stream"), ("topic", "topic"), ("sender", "sender"), ("search", "search") })
                        {
                            if (Values.Get(p, name) is { } operand)
                            {
                                narrow.Add(new JsonObject { ["operator"] = operator_, ["operand"] = operand });
                            }
                        }

                        // Zulip pages by an anchor, newest first: the next page before the oldest read, Zulip's found oldest the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        var anchor = "newest";
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/messages?anchor={anchor}&include_anchor={(anchor == "newest" ? "true" : "false")}&num_before={size}&num_after=0&apply_markdown=false&narrow={Uri.EscapeDataString(narrow.ToJsonString(Wire))}", null, cancellationToken);
                            var messages = (answer?["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().OrderByDescending(message => Number(message["id"]) ?? 0).ToList();
                            foreach (var message in messages)
                            {
                                yield return Record(message);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (messages.Count < size || answer?["found_oldest"] is JsonValue oldest && oldest.TryGetValue<bool>(out var found) && found || Number(messages[^1]["id"]) is not { } last)
                            {
                                yield break;
                            }

                            anchor = last.ToString(CultureInfo.InvariantCulture);
                        }
                    }

                case UpdateMessageOperation:
                case UpdateStreamOperation:
                case UpdateUserOperation:
                    {
                        var path = operation switch { UpdateMessageOperation => "/messages/", UpdateStreamOperation => "/streams/", _ => "/users/" } + id;
                        await session.SendAsync(HttpMethod.Patch, path, form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["updated"] = true });
                        yield break;
                    }

                case DeleteMessageOperation:
                case DeleteStreamOperation:
                case DeactivateUserOperation:
                    {
                        var path = operation switch { DeleteMessageOperation => "/messages/", DeleteStreamOperation => "/streams/", _ => "/users/" } + id;
                        await session.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), [operation == DeactivateUserOperation ? "deactivated" : "deleted"] = true });
                        yield break;
                    }

                case CreateStreamOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, "/users/me/subscriptions", form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = Values.Get(p, "name"),
                            ["subscribed"] = answer?["subscribed"]?.ToJsonString(Wire),
                            ["already_subscribed"] = answer?["already_subscribed"]?.ToJsonString(Wire),
                        });
                        yield break;
                    }

                case CreateUserOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, "/users", form, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["user_id"] = Number(answer?["user_id"]), ["email"] = Values.Get(p, "email") });
                        yield break;
                    }

                case UserOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(id!)}", null, cancellationToken))?["user"] as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    {
                        var (path, key) = operation switch
                        {
                            StreamsOperation => ("/streams", "streams"),
                            SubscriptionsOperation => ("/users/me/subscriptions" + (form.Count == 0 ? "" : "?" + string.Join("&", form.Select(pair => pair.Key + "=" + pair.Value))), "subscriptions"),
                            _ => ("/users", "members"),
                        };
                        foreach (var item in ((await session.SendAsync(HttpMethod.Get, path, null, cancellationToken))?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }
            }
        }

        private static ConnectorRecord Record(JsonObject item) => new(Fields(item));

        // A record's fields as Zulip gives them - a message's subject its topic too - a list or an object as its JSON.
        private static Dictionary<string, object?> Fields(JsonObject item)
        {
            var record = item.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue text when text.TryGetValue<string>(out var s) => s,
                JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
                var other => other.ToJsonString(Wire),
            }, StringComparer.Ordinal);
            if (record.TryGetValue("subject", out var subject))
            {
                record.TryAdd("topic", subject);
            }

            return record;
        }

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the organization's API v1 with the e-mail and key; a body form-encoded, as Zulip reads it; a
        // throttled call waiting as Zulip says; a failure thrown with its status, Zulip's code and its word.
        private sealed class Session
        {
            private readonly string api;
            private readonly AuthenticationHeaderValue authorization;

            private Session(string api, AuthenticationHeaderValue authorization, HttpClient client)
            {
                this.api = api;
                this.authorization = authorization;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the organization's address - https://chat.example.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^7] : baseUrl;
                var email = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Zulip connection has no e-mail - the bot's or the user's.");
                var key = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Zulip connection has no API key.");
                return new Session(baseUrl + "/api/v1", new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + key))), egress.CreateHttpClient(timeout));
            }

            public async Task<JsonObject?> SendAsync(HttpMethod method, string path, List<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = authorization;
                    if (form is not null)
                    {
                        request.Content = new FormUrlEncodedContent(form);
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonObject? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer) as JsonObject;
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Zulip's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"Zulip answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode || Text(json?["result"]) == "error")
                {
                    var code = Text(json?["code"]);
                    var said = Text(json?["msg"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
