#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Messaging
{
    /// <summary>
    /// What the chat connectors share (BL-229, for n8n's Slack, Telegram, Discord, Mattermost and Twilio nodes): one operation, <c>message</c> - a
    /// text sent <c>to</c> a channel or chat, optionally <c>reply_to</c> a message - answered by ONE record: the message's
    /// <c>id</c> and where it went, or <c>refused</c> with the service's own reason (one line, at most
    /// <see cref="MaxReason"/> characters) when the service answered and said no. The reason travels as a record, not an
    /// exception, because the extraction port answers a thrown failure with its fixed words alone - and "channel_not_found"
    /// is the one thing an author needs to put a message right. A failure that is not the service's answer (no answer, not
    /// JSON, a policy refusal) still throws, and is the port's category.
    /// </summary>
    public static class ChatMessages
    {
        public const string Operation = "message";

        public const string ToParameter = "to";

        public const string TextParameter = "text";

        public const string ReplyToParameter = "reply_to";

        public const string IdField = "id";

        public const string ToField = "to";

        public const string RefusedField = "refused";

        public const int MaxReason = 300;

        /// <summary>The <c>message</c> operation, its <c>to</c> described in the service's own terms.</summary>
        public static ConnectorOperation Describe(string toDescription, string replyToDescription) =>
            new(Operation, "Send a message", "Sends a text message and answers one record: the message's id and where it went, or why the service refused it.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField(ToParameter, "To", ConnectorConfigFieldType.Text, Required: true, Description: toDescription),
                    new ConnectorConfigField(TextParameter, "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "The message."),
                    new ConnectorConfigField(ReplyToParameter, "In reply to", ConnectorConfigFieldType.Text, Required: false, Description: replyToDescription),
                }),
            };

        /// <summary>The endpoint with a path under it - one slash between, whatever either ends or starts with.</summary>
        public static bool TryUnder(string endpoint, string path, out Uri target)
        {
            target = null!;
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var root) || (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            {
                return false;
            }

            target = new Uri(root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path.TrimStart('/'));
            return true;
        }

        /// <summary>A message's parameters: <c>to</c> and <c>text</c> required, <c>reply_to</c> optional; null and why when one is missing.</summary>
        public static (string To, string Text, string? ReplyTo)? Read(IReadOnlyDictionary<string, string> parameters, out string problem)
        {
            problem = string.Empty;
            var to = parameters.TryGetValue(ToParameter, out var target) ? target.Trim() : string.Empty;
            var text = parameters.TryGetValue(TextParameter, out var body) ? body : string.Empty;
            if (to.Length == 0)
            {
                problem = "A message needs somewhere to go (to).";
                return null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                problem = "A message needs a text.";
                return null;
            }

            return (to, text, parameters.TryGetValue(ReplyToParameter, out var reply) && !string.IsNullOrWhiteSpace(reply) ? reply.Trim() : null);
        }

        /// <summary>A JSON body POSTed; the answer's status and its JSON (null when it is not JSON).</summary>
        public static async Task<(int Status, JsonNode? Json)> PostAsync(HttpClient client, HttpRequestMessage request, JsonObject body, CancellationToken cancellationToken)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return await ReadAsync(client, request, cancellationToken);
        }

        public static async Task<(int Status, JsonNode? Json)> ReadAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The chat service answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            return ((int)response.StatusCode, json);
        }

        public static ConnectorRecord Sent(string id, string to) =>
            new(new Dictionary<string, object?>(StringComparer.Ordinal) { [IdField] = id, [ToField] = to });

        /// <summary>The service's refusal as a record - its reason on one line, cut to <see cref="MaxReason"/> characters.</summary>
        public static ConnectorRecord Refused(string service, string? reason)
        {
            var line = string.Join(' ', (reason ?? string.Empty).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (line.Length == 0)
            {
                line = "no reason given";
            }

            if (line.Length > MaxReason)
            {
                line = line[..MaxReason] + "...";
            }

            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [RefusedField] = service + ": " + line });
        }

        /// <summary>A JSON value as text - a string as it is, a number in invariant form.</summary>
        public static string? Text(JsonNode? node) => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>
    /// Slack (n8n's Slack node, its message): a bot token (xoxb-...) as the Bearer token, the endpoint Slack's Web API,
    /// <c>https://slack.com/api</c>. A test calls <c>auth.test</c>; <c>message</c> calls <c>chat.postMessage</c> - <c>to</c> a
    /// channel (#general, or its id) or a person's id, <c>reply_to</c> a message's ts to answer it in its thread. Slack answers
    /// a refusal with HTTP 200 and <c>ok: false</c>, its <c>error</c> a short code (channel_not_found, not_in_channel).
    /// </summary>
    public sealed class SlackConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "slack";

        public string DisplayName => "Slack";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Slack names it: a bot's token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Bot token (xoxb-...)" };

        public string EndpointExample => "https://slack.com/api";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://slack.com");

        public Uri ApiDocumentationUrl { get; } = new("https://api.slack.com/methods/chat.postMessage");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A channel - #general, or its id (C0123...) - or a person's id (U0123...).", "A message's ts, to answer it in its thread."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!ChatMessages.TryUnder(context.Endpoint, "auth.test", out var target))
            {
                return ConnectionTestResult.Fail("Endpoint must be an absolute http(s) URL - Slack's is https://slack.com/api.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            Authorize(request, context.Credentials);
            try
            {
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return json?["ok"]?.GetValueKind() == JsonValueKind.True
                    ? ConnectionTestResult.Ok($"Signed in as {ChatMessages.Text(json["user"])} on {ChatMessages.Text(json["team"])}.")
                    : ConnectionTestResult.Fail(json?["error"] is { } error ? "Slack: " + ChatMessages.Text(error) : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            if (!ChatMessages.TryUnder(context.Endpoint, "chat.postMessage", out var target))
            {
                throw new InvalidOperationException("Endpoint must be an absolute http(s) URL.");
            }

            var body = new JsonObject { ["channel"] = message.To, ["text"] = message.Text };
            if (message.ReplyTo is { } thread)
            {
                body["thread_ts"] = thread;
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            Authorize(request, context.Credentials);
            var (status, json) = await ChatMessages.PostAsync(client, request, body, cancellationToken);
            if (json?["ok"]?.GetValueKind() == JsonValueKind.True && ChatMessages.Text(json["ts"]) is { Length: > 0 } ts)
            {
                yield return ChatMessages.Sent(ts, ChatMessages.Text(json["channel"]) ?? message.To);
            }
            else if (json?["ok"]?.GetValueKind() == JsonValueKind.False)
            {
                yield return ChatMessages.Refused("Slack", ChatMessages.Text(json["error"]));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Slack's answer.");
            }
        }

        private static void Authorize(HttpRequestMessage request, IReadOnlyDictionary<string, string> credentials)
        {
            if (credentials.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    /// <summary>
    /// Telegram (n8n's Telegram node, its message): a bot's token (from @BotFather) as the Bearer token, the endpoint the Bot
    /// API, <c>https://api.telegram.org</c>. The token is part of each request's PATH (<c>/bot{token}/sendMessage</c>), as
    /// Telegram has it, so no message this connector writes names the address. A test calls <c>getMe</c>; <c>message</c> calls
    /// <c>sendMessage</c> - <c>to</c> a chat's id (or @channelname), <c>reply_to</c> a message's id. A refusal is
    /// <c>ok: false</c> with its <c>description</c>.
    /// </summary>
    public sealed class TelegramConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "telegram";

        public string DisplayName => "Telegram";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Telegram names it: the bot's token from @BotFather.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Bot token (from @BotFather)" };

        public string EndpointExample => "https://api.telegram.org";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://telegram.org");

        public Uri ApiDocumentationUrl { get; } = new("https://core.telegram.org/bots/api#sendmessage");

        /// <summary>The updates waiting for the bot (BL-229, for the "Telegram message received" trigger).</summary>
        public const string UpdatesOperation = "updates";

        /// <summary>The newest update's id - every earlier one forgotten, as Telegram does for a negative offset.</summary>
        public const string LatestOperation = "latest";

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A chat's id (a number, negative for a group) or a channel's @name.", "A message's id, to answer it."),
            new ConnectorOperation(UpdatesOperation, "Read updates", "The updates waiting for the bot, oldest first, one record each: its update_id and kind, and for a message or a channel post its chat, its sender and its text. Without offset nothing is forgotten; with one, Telegram forgets every update before it (getUpdates).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("offset", "Offset", ConnectorConfigFieldType.Integer, Required: false, Description: "The first update_id to answer; Telegram then forgets the earlier ones. Empty: every update waiting, none forgotten."),
                    new ConnectorConfigField("limit", "Limit", ConnectorConfigFieldType.Integer, Required: false, Description: "The most updates answered, 1 to 100.") { Default = "100" },
                }),
            },
            new ConnectorOperation(LatestOperation, "Latest update", "One record: the update_id of the newest update waiting (0 when none). Telegram FORGETS every earlier one - what a trigger starts watching from, the messages already there starting nothing."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryMethod(context.Endpoint, context.Credentials, "getMe", out var target, out var problem))
            {
                return ConnectionTestResult.Fail(problem);
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            try
            {
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return json?["ok"]?.GetValueKind() == JsonValueKind.True
                    ? ConnectionTestResult.Ok($"Signed in as @{ChatMessages.Text(json["result"]?["username"])}.")
                    : ConnectionTestResult.Fail(json?["description"] is { } description ? "Telegram: " + ChatMessages.Text(description) : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(Unaddressed(ex));
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.Equals(context.OperationName, UpdatesOperation, StringComparison.OrdinalIgnoreCase) || string.Equals(context.OperationName, LatestOperation, StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var update in UpdatesAsync(context, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            if (!TryMethod(context.Endpoint, context.Credentials, "sendMessage", out var target, out problem))
            {
                throw new InvalidOperationException(problem);
            }

            var body = new JsonObject { ["chat_id"] = message.To, ["text"] = message.Text };
            if (message.ReplyTo is { } reply)
            {
                if (!long.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var replyId))
                {
                    yield return ChatMessages.Refused("Telegram", $"'{reply}' is not a message id.");
                    yield break;
                }

                body["reply_parameters"] = new JsonObject { ["message_id"] = replyId };
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            (int Status, JsonNode? Json) answer;
            try
            {
                answer = await ChatMessages.PostAsync(client, request, body, cancellationToken);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                throw new HttpRequestException(Unaddressed(ex), null, ex.StatusCode);
            }

            var (status, json) = answer;
            if (json?["ok"]?.GetValueKind() == JsonValueKind.True && ChatMessages.Text(json["result"]?["message_id"]) is { Length: > 0 } id)
            {
                yield return ChatMessages.Sent(id, ChatMessages.Text(json["result"]?["chat"]?["id"]) ?? message.To);
            }
            else if (json?["ok"]?.GetValueKind() == JsonValueKind.False)
            {
                yield return ChatMessages.Refused("Telegram", ChatMessages.Text(json["description"]));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Telegram's answer.");
            }
        }

        // getUpdates, never waiting (timeout 0): the updates from an offset, or the newest one with every earlier one forgotten. A
        // message or a channel post is read into its fields; any other kind of update is its id and kind only, so a reader moves
        // past it. A refusal - a webhook set on the bot, a token Telegram does not know - is a record in Telegram's words.
        private async IAsyncEnumerable<ConnectorRecord> UpdatesAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var latest = string.Equals(context.OperationName, LatestOperation, StringComparison.OrdinalIgnoreCase);
            var query = new List<string> { "timeout=0" };
            if (latest)
            {
                query.Add("offset=-1");
                query.Add("limit=1");
            }
            else
            {
                if (context.Parameters.TryGetValue("offset", out var offsetText) && !string.IsNullOrWhiteSpace(offsetText))
                {
                    if (!long.TryParse(offsetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                    {
                        throw new InvalidOperationException($"'{offsetText}' is not an update id.");
                    }

                    query.Add("offset=" + offset.ToString(CultureInfo.InvariantCulture));
                }

                var limit = context.Parameters.TryGetValue("limit", out var limitText) && int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) ? Math.Clamp(most, 1, 100) : 100;
                query.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
            }

            if (!TryMethod(context.Endpoint, context.Credentials, "getUpdates?" + string.Join("&", query), out var target, out var problem))
            {
                throw new InvalidOperationException(problem);
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            (int Status, JsonNode? Json) answer;
            try
            {
                answer = await ChatMessages.ReadAsync(client, request, cancellationToken);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                throw new HttpRequestException(Unaddressed(ex), null, ex.StatusCode);
            }

            var (status, json) = answer;
            if (json?["ok"]?.GetValueKind() == JsonValueKind.False)
            {
                yield return ChatMessages.Refused("Telegram", ChatMessages.Text(json["description"]));
                yield break;
            }

            if (json?["ok"]?.GetValueKind() != JsonValueKind.True || json["result"] is not JsonArray updates)
            {
                throw new InvalidOperationException($"HTTP {status}: not Telegram's answer.");
            }

            if (latest)
            {
                var newest = updates.Select(update => Number(update?["update_id"]) ?? 0).DefaultIfEmpty(0).Max();
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [UpdateIdField] = newest });
                yield break;
            }

            foreach (var update in updates.OfType<JsonObject>().OrderBy(update => Number(update["update_id"]) ?? 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Update(update);
            }
        }

        public const string UpdateIdField = "update_id";

        // One update as a record: its id and kind; for a message or a channel post, its fields.
        private static ConnectorRecord Update(JsonObject update)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { [UpdateIdField] = Number(update["update_id"]) ?? 0 };
            var kind = update.Select(property => property.Key).FirstOrDefault(key => key != "update_id") ?? "unknown";
            fields["kind"] = kind;
            if (kind is not ("message" or "channel_post") || update[kind] is not JsonObject message)
            {
                return new ConnectorRecord(fields);
            }

            fields["message_id"] = Number(message["message_id"]) ?? 0;
            fields["date"] = Number(message["date"]) ?? 0;
            fields["text"] = ChatMessages.Text(message["text"]) ?? ChatMessages.Text(message["caption"]) ?? "";
            fields["chat_id"] = Number(message["chat"]?["id"]) ?? 0;
            fields["chat_type"] = ChatMessages.Text(message["chat"]?["type"]) ?? "";
            fields["chat_title"] = ChatMessages.Text(message["chat"]?["title"]);
            fields["chat_username"] = ChatMessages.Text(message["chat"]?["username"]);
            if (message["from"] is JsonObject from)
            {
                fields["from_id"] = Number(from["id"]);
                fields["from_username"] = ChatMessages.Text(from["username"]);
                fields["from_first_name"] = ChatMessages.Text(from["first_name"]);
                fields["from_last_name"] = ChatMessages.Text(from["last_name"]);
            }

            fields["reply_to_message_id"] = Number(message["reply_to_message"]?["message_id"]);
            return new ConnectorRecord(fields);
        }

        private static long? Number(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        // The Bot API's method under the endpoint, the token in its path: /bot{token}/{method}. A token holding anything but the
        // characters Telegram issues (digits, a colon, letters, - and _) is refused - it is written into the path.
        private static bool TryMethod(string endpoint, IReadOnlyDictionary<string, string> credentials, string method, out Uri target, out string problem)
        {
            target = null!;
            if (!credentials.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
            {
                problem = "The connection has no bot token.";
                return false;
            }

            foreach (var character in token)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_'))
                {
                    problem = "The bot token is not one Telegram issues (digits, a colon, letters).";
                    return false;
                }
            }

            if (!ChatMessages.TryUnder(endpoint, "bot" + token + "/" + method, out target))
            {
                problem = "Endpoint must be an absolute http(s) URL - Telegram's is https://api.telegram.org.";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        // An HTTP failure's words, never the address - the address holds the token. .NET's own messages name a host and port,
        // not a path, but this keeps it so whatever a runtime writes.
        private static string Unaddressed(HttpRequestException exception) =>
            exception.Message.Contains("/bot", StringComparison.Ordinal) ? "Telegram could not be reached." : exception.Message;
    }

    /// <summary>
    /// Discord (n8n's Discord node, its message sent by a bot): a bot's token as the Bearer token - sent as <c>Bot {token}</c>,
    /// as Discord has it - the endpoint Discord's API, <c>https://discord.com/api/v10</c>. A test reads <c>users/@me</c>;
    /// <c>message</c> posts to <c>channels/{to}/messages</c> - <c>to</c> a channel's id, digits only since it is written into
    /// the path, <c>reply_to</c> a message's id. A refusal is a 4xx whose JSON <c>message</c> says why.
    /// </summary>
    public sealed class DiscordConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "discord";

        public string DisplayName => "Discord";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Discord names it: a bot's token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Bot token" };

        public string EndpointExample => "https://discord.com/api/v10";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://discord.com");

        public Uri ApiDocumentationUrl { get; } = new("https://discord.com/developers/docs/resources/message#create-message");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A channel's id (digits - Developer Mode, then Copy Channel ID).", "A message's id, to answer it."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!ChatMessages.TryUnder(context.Endpoint, "users/@me", out var target))
            {
                return ConnectionTestResult.Fail("Endpoint must be an absolute http(s) URL - Discord's is https://discord.com/api/v10.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            Authorize(request, context.Credentials);
            try
            {
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return status is >= 200 and < 300 && ChatMessages.Text(json?["username"]) is { Length: > 0 } name
                    ? ConnectionTestResult.Ok($"Signed in as {name}.")
                    : ConnectionTestResult.Fail(json?["message"] is { } reason ? "Discord: " + ChatMessages.Text(reason) : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            if (!IsSnowflake(message.To))
            {
                yield return ChatMessages.Refused("Discord", $"'{message.To}' is not a channel id (digits).");
                yield break;
            }

            if (message.ReplyTo is { } reply && !IsSnowflake(reply))
            {
                yield return ChatMessages.Refused("Discord", $"'{reply}' is not a message id (digits).");
                yield break;
            }

            if (!ChatMessages.TryUnder(context.Endpoint, "channels/" + message.To + "/messages", out var target))
            {
                throw new InvalidOperationException("Endpoint must be an absolute http(s) URL.");
            }

            var body = new JsonObject { ["content"] = message.Text };
            if (message.ReplyTo is { } replyId)
            {
                body["message_reference"] = new JsonObject { ["message_id"] = replyId };
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            Authorize(request, context.Credentials);
            var (status, json) = await ChatMessages.PostAsync(client, request, body, cancellationToken);
            if (status is >= 200 and < 300 && ChatMessages.Text(json?["id"]) is { Length: > 0 } id)
            {
                yield return ChatMessages.Sent(id, ChatMessages.Text(json!["channel_id"]) ?? message.To);
            }
            else if (status is >= 400 and < 500 && json?["message"] is { } reason)
            {
                yield return ChatMessages.Refused("Discord", ChatMessages.Text(reason));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Discord's answer.");
            }
        }

        private static bool IsSnowflake(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);

        private static void Authorize(HttpRequestMessage request, IReadOnlyDictionary<string, string> credentials)
        {
            if (credentials.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            }
        }
    }

    /// <summary>
    /// Mattermost (n8n's Mattermost node, its message posted): a bot account's or a personal access token as the Bearer token,
    /// the endpoint the server's own address (<c>https://chat.example.com</c> - the API's <c>/api/v4</c> added here, and not
    /// added twice when it is typed). A test reads <c>users/me</c>; <c>message</c> creates a post - <c>to</c> a channel's id
    /// (26 letters and digits: the channel's View Info), <c>reply_to</c> the id of the post a thread starts with, to answer in
    /// it. A refusal is a 4xx whose JSON <c>message</c> says why. The rest of n8n's node - channels, a post deleted or sent to
    /// one user, reactions and users - is <see cref="MattermostApi"/>'s, after the message.
    /// </summary>
    public sealed class MattermostConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "mattermost";

        public string DisplayName => "Mattermost";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Mattermost names it: a bot account's or a personal access token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Access token" };

        public string EndpointExample => "https://chat.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://mattermost.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.mattermost.com/api-documentation/#/operations/CreatePost");

        // The message first - what a send names, and the port's default - then the rest of n8n's node.
        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A channel's id (26 letters and digits - the channel's View Info).", "The id of the post a thread starts with, to answer in it."),
        }.Concat(MattermostApi.Operations).ToArray();

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryApi(context.Endpoint, "users/me", out var target))
            {
                return ConnectionTestResult.Fail("Endpoint must be the server's absolute http(s) address - https://chat.example.com.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            Authorize(request, context.Credentials);
            try
            {
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return status is >= 200 and < 300 && ChatMessages.Text(json?["username"]) is { Length: > 0 } name
                    ? ConnectionTestResult.Ok($"Signed in as {name}.")
                    : ConnectionTestResult.Fail(json?["message"] is { } reason ? "Mattermost: " + ChatMessages.Text(reason) : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = (context.OperationName ?? ChatMessages.Operation).Trim().ToLowerInvariant();
            if (operation.Length > 0 && operation != ChatMessages.Operation)
            {
                await foreach (var record in MattermostApi.RunAsync(context, operation, cancellationToken))
                {
                    yield return record;
                }

                yield break;
            }

            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            if (!TryApi(context.Endpoint, "posts", out var target))
            {
                throw new InvalidOperationException("Endpoint must be an absolute http(s) URL.");
            }

            var body = new JsonObject { ["channel_id"] = message.To, ["message"] = message.Text };
            if (message.ReplyTo is { } root)
            {
                body["root_id"] = root;
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            Authorize(request, context.Credentials);
            var (status, json) = await ChatMessages.PostAsync(client, request, body, cancellationToken);
            if (status is >= 200 and < 300 && ChatMessages.Text(json?["id"]) is { Length: > 0 } id)
            {
                yield return ChatMessages.Sent(id, ChatMessages.Text(json!["channel_id"]) ?? message.To);
            }
            else if (status is >= 400 and < 500 && json?["message"] is { } reason)
            {
                yield return ChatMessages.Refused("Mattermost", ChatMessages.Text(reason));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Mattermost's answer.");
            }
        }

        // The server's API under the endpoint: /api/v4 added unless the endpoint already ends with it.
        private static bool TryApi(string endpoint, string path, out Uri target)
        {
            var root = (endpoint ?? string.Empty).Trim().TrimEnd('/');
            return ChatMessages.TryUnder(root.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? root : root + "/api/v4", path, out target);
        }

        private static void Authorize(HttpRequestMessage request, IReadOnlyDictionary<string, string> credentials)
        {
            if (credentials.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    /// <summary>
    /// Twilio (n8n's Twilio node, its SMS sent): the account's SID and auth token as the user name and password - Basic, as
    /// Twilio has it - the endpoint Twilio's API, <c>https://api.twilio.com</c>, and the connection's <c>from</c>: one of the
    /// account's numbers (+15017122661), a Messaging Service's SID (MG...), or a WhatsApp sender (whatsapp:+1...). A test reads
    /// the account; <c>message</c> creates a message under <c>2010-04-01/Accounts/{sid}/Messages.json</c>, form-encoded -
    /// <c>to</c> a number in E.164 (+46701740605) or whatsapp:+46..., the sender then whatsapp: too, as n8n's "To WhatsApp" has
    /// it. A text message answers nothing, so <c>reply_to</c> is refused. The SID is written into the path, so it is held to
    /// its shape (AC and 32 hex digits) before anything is sent. A refusal is a 4xx whose JSON <c>message</c> says why.
    /// </summary>
    public sealed class TwilioConnector : IConnector, IConnectorExtractor
    {
        public const string FromSetting = "from";

        public string ConnectorType => "twilio";

        public string DisplayName => "Twilio";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The Basic scheme as Twilio names it: the account's SID and auth token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Account SID (AC...)", ["password"] = "Auth token" };

        public string EndpointExample => "https://api.twilio.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.twilio.com");

        public Uri ApiDocumentationUrl { get; } = new("https://www.twilio.com/docs/messaging/api/message-resource#create-a-message-resource");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField(FromSetting, "From", ConnectorConfigFieldType.Text, Required: true,
                Description: "Who the messages come from: one of the account's numbers (+15017122661), a Messaging Service's SID (MG...) or whatsapp:+15017122661. The user name is the account's SID (AC...), the password its auth token."),
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A phone number in E.164 (+46701740605), or whatsapp:+46701740605.", "Not for a text message - it answers nothing."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryAccount(context.Endpoint, context.Credentials, ".json", out var target, out var problem))
            {
                return ConnectionTestResult.Fail(problem);
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            try
            {
                request.Headers.Authorization = BearerClient.Basic(context.Credentials, "Twilio");
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return status is >= 200 and < 300 && ChatMessages.Text(json?["friendly_name"]) is { Length: > 0 } name
                    ? ConnectionTestResult.Ok($"Signed in to {name}.")
                    : ConnectionTestResult.Fail(json?["message"] is { } reason ? "Twilio: " + ChatMessages.Text(reason) : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            if (message.ReplyTo is not null)
            {
                yield return ChatMessages.Refused("Twilio", "a text message cannot answer another one - leave In reply to empty.");
                yield break;
            }

            var from = Values.Get(context.Settings, FromSetting) ?? throw new InvalidOperationException("Name who the messages come from - the connection's From setting.");
            if (!TryAccount(context.Endpoint, context.Credentials, "/Messages.json", out var target, out problem))
            {
                throw new InvalidOperationException(problem);
            }

            var form = new List<KeyValuePair<string, string>> { new("To", message.To), new("Body", message.Text) };
            if (from.StartsWith("MG", StringComparison.Ordinal))
            {
                form.Add(new("MessagingServiceSid", from));
            }
            else
            {
                var whatsApp = message.To.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) && !from.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase);
                form.Add(new("From", whatsApp ? "whatsapp:" + from : from));
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Authorization = BearerClient.Basic(context.Credentials, "Twilio");
            var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
            if (status is >= 200 and < 300 && ChatMessages.Text(json?["sid"]) is { Length: > 0 } sid)
            {
                yield return ChatMessages.Sent(sid, ChatMessages.Text(json!["to"]) ?? message.To);
            }
            else if (status is >= 400 and < 500 && json?["message"] is { } reason)
            {
                yield return ChatMessages.Refused("Twilio", ChatMessages.Text(reason));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Twilio's answer.");
            }
        }

        // The account's own address under the endpoint: 2010-04-01/Accounts/{sid}{rest}, the SID - the Basic user name - held to
        // AC and 32 hex digits, since it is written into the path.
        private static bool TryAccount(string endpoint, IReadOnlyDictionary<string, string> credentials, string rest, out Uri target, out string problem)
        {
            target = null!;
            var sid = Values.Get(credentials, "username");
            if (sid is null || sid.Length != 34 || !sid.StartsWith("AC", StringComparison.Ordinal) || !sid[2..].All(char.IsAsciiHexDigit))
            {
                problem = "The user name must be the account's SID - AC and 32 hex digits (Twilio's Console, Account Info).";
                return false;
            }

            if (!ChatMessages.TryUnder(endpoint, "2010-04-01/Accounts/" + sid + rest, out target))
            {
                problem = "Endpoint must be an absolute http(s) URL - Twilio's is https://api.twilio.com.";
                return false;
            }

            problem = string.Empty;
            return true;
        }
    }
}
