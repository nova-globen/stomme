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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Trello
{
    /// <summary>
    /// Trello (BL-229, n8n's Trello node: a card created, got, updated and deleted, a comment added, a list's cards got): Trello's
    /// REST API (<c>https://api.trello.com/1</c>), the account's API key and token as the key pair - sent in Trello's
    /// <c>Authorization: OAuth oauth_consumer_key, oauth_token</c> header, never in an address, so the token is in no log line. A
    /// test asks Trello who the token is (<c>members/me</c>).
    /// </summary>
    /// <remarks>
    /// A card, a list and a board are named by their id (24 hex digits) or their short link (8 letters and digits) - a card's or a
    /// board's address cut down to it - and held to that shape before they reach the path. <c>cards</c>: the open cards of a
    /// <c>list</c>, or of a <c>board</c>, up to <c>max</c>; one record each: id, name, description, url, list, board, due,
    /// due_complete, closed, labels (their names, comma-separated), members (their ids), position and last_activity.
    /// <c>get_card</c> the same for one. <c>create_card</c>: a card on a list, its name, description, due date, labels and members
    /// (ids, comma-separated) and position (top or bottom); <c>update_card</c> changes those given, moves it to another list, marks
    /// it done or archives it; <c>delete_card</c> removes it; <c>comment</c> adds a comment. A throttled call waits as Trello says;
    /// a failure is thrown with its status and Trello's words.
    /// </remarks>
    public sealed class TrelloConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.trello.com/1";

        public const string CardsOperation = "cards";

        public const string GetCardOperation = "get_card";

        public const string CreateCardOperation = "create_card";

        public const string UpdateCardOperation = "update_card";

        public const string DeleteCardOperation = "delete_card";

        public const string CommentOperation = "comment";

        public const int DefaultMax = 1000;

        private static readonly Regex TrelloId = new("^([0-9a-fA-F]{24}|[A-Za-z0-9]{8})$", RegexOptions.CultureInvariant);

        private static readonly Regex LongId = new("^[0-9a-fA-F]{24}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "trello";

        public string DisplayName => "Trello";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        /// <summary>The key pair as Trello names it: its API key and token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["access-key"] = "API key", ["secret-key"] = "Token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://trello.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.atlassian.com/cloud/trello/rest/");

        private static readonly ConnectorConfigField CardField = new("card", "Card", ConnectorConfigFieldType.Text, Required: true, Description: "The card's id, its short link, or its address.");

        private static ConnectorConfigField[] CardFields(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
            new ConnectorConfigField("due", "Due", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-12, or 2026-10-12T17:00:00+02:00."),
            new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Label ids, comma-separated" + (create ? "." : " - they replace the card's.")),
            new ConnectorConfigField("members", "Members", ConnectorConfigFieldType.Text, Required: false, Description: "Member ids, comma-separated" + (create ? "." : " - they replace the card's.")),
            new ConnectorConfigField("position", "Position", ConnectorConfigFieldType.Text, Required: false, Description: "top or bottom."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CardsOperation, "Get cards", "The open cards of a list or a board - one record each: its id, name, description, page, list, due date, labels and members - n8n's list Get Cards.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: false, Description: "The list's id. Or name a board."),
                    new ConnectorConfigField("board", "Board", ConnectorConfigFieldType.Text, Required: false, Description: "The board's id, short link or address - every list's cards."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many cards, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetCardOperation, "Get a card", "One card - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CardField }),
            },
            new ConnectorOperation(CreateCardOperation, "Create a card", "Adds a card to a list - one record: the card - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id.") }.Concat(CardFields(create: true)).ToArray()),
            },
            new ConnectorOperation(UpdateCardOperation, "Update a card", "Changes a card - its name, description, due date, labels or members - moves it, marks it done or archives it - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CardField }.Concat(CardFields(create: false)).Concat(new[]
                {
                    new ConnectorConfigField("list", "Move to list", ConnectorConfigFieldType.Text, Required: false, Description: "Another list's id."),
                    new ConnectorConfigField("due_complete", "Done", ConnectorConfigFieldType.Boolean, Required: false, Description: "Its due date marked complete."),
                    new ConnectorConfigField("archived", "Archived", ConnectorConfigFieldType.Boolean, Required: false, Description: "Closed, or open again."),
                }).ToArray()),
            },
            new ConnectorOperation(DeleteCardOperation, "Delete a card", "Removes a card for good - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CardField }),
            },
            new ConnectorOperation(CommentOperation, "Comment on a card", "Adds a comment to a card - one record: its id - n8n's card comment Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { CardField, new ConnectorConfigField("text", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Markdown.") }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Authorization(context.Credentials), HttpMethod.Get, api + "/members/me?fields=fullName,username", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {Text(me?["fullName"]) ?? Text(me?["username"]) ?? "the token's member"}.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var authorization = Authorization(context.Credentials);
            var operation = context.OperationName ?? CardsOperation;
            var parameters = context.Parameters;
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, CardsOperation))
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var source = Values.Get(parameters, "list") is { } list ? $"{api}/lists/{Id(list, "list", longOnly: true)}/cards"
                    : Values.Get(parameters, "board") is { } board ? $"{api}/boards/{Id(board, "board")}/cards/open"
                    : throw new InvalidOperationException("Name the list - or the board - whose cards to read.");
                var cards = await SendAsync(client, authorization, HttpMethod.Get, source, null, cancellationToken);
                foreach (var card in (cards as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return CardRecord(card);
                }

                yield break;
            }

            if (Is(operation, CreateCardOperation))
            {
                var body = CardBody(parameters, create: true);
                body["idList"] = Id(Values.Get(parameters, "list"), "list", longOnly: true);
                var made = await SendAsync(client, authorization, HttpMethod.Post, api + "/cards", body, cancellationToken);
                yield return CardRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            var cardId = Id(Values.Get(parameters, "card"), "card");
            if (Is(operation, GetCardOperation))
            {
                var found = await SendAsync(client, authorization, HttpMethod.Get, $"{api}/cards/{cardId}", null, cancellationToken);
                yield return CardRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateCardOperation))
            {
                var body = CardBody(parameters, create: false);
                if (Values.Get(parameters, "list") is { } moveTo)
                {
                    body["idList"] = Id(moveTo, "list", longOnly: true);
                }

                foreach (var (parameter, field) in new[] { ("due_complete", "dueComplete"), ("archived", "closed") })
                {
                    if (Values.Get(parameters, parameter) is { } flag)
                    {
                        body[field] = flag.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : flag.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                            : throw new InvalidOperationException($"'{flag}' is not true or false ({parameter}).");
                    }
                }

                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - name, description, due, labels, members, position, list, due_complete or archived.");
                }

                var changed = await SendAsync(client, authorization, HttpMethod.Put, $"{api}/cards/{cardId}", body, cancellationToken);
                yield return CardRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, DeleteCardOperation))
            {
                await SendAsync(client, authorization, HttpMethod.Delete, $"{api}/cards/{cardId}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = cardId, ["deleted"] = true });
                yield break;
            }

            if (Is(operation, CommentOperation))
            {
                var text = Values.Get(parameters, "text") ?? throw new InvalidOperationException("Give the comment's text.");
                var comment = await SendAsync(client, authorization, HttpMethod.Post, $"{api}/cards/{cardId}/actions/comments", new JsonObject { ["text"] = text }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["card"] = cardId, ["comment_id"] = Text(comment?["id"]) });
                yield break;
            }

            throw new InvalidOperationException($"Trello has no operation '{operation}' - cards, get_card, create_card, update_card, delete_card or comment.");
        }

        // A card's fields as Trello's: its name, description, due date, labels, members and position, as given.
        private static JsonObject CardBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "name") is { } name)
            {
                body["name"] = name.Contains('\n', StringComparison.Ordinal) || name.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The name has a line break - one line only.") : name;
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the card's name.");
            }

            if (parameters.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
            {
                body["desc"] = description;
            }

            if (Values.Get(parameters, "due") is { } due)
            {
                body["due"] = DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{due}' is not a date - 2026-10-12, or 2026-10-12T17:00:00+02:00.");
            }

            foreach (var (parameter, field, what) in new[] { ("labels", "idLabels", "label"), ("members", "idMembers", "member") })
            {
                if (Values.Get(parameters, parameter) is { } ids)
                {
                    body[field] = string.Join(",", ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => LongId.IsMatch(id) ? id : throw new InvalidOperationException($"'{id}' is not a Trello {what}'s id.")));
                }
            }

            if (Values.Get(parameters, "position") is { } position)
            {
                body["pos"] = position.ToLowerInvariant() is "top" or "bottom" ? position.ToLowerInvariant() : throw new InvalidOperationException($"'{position}' is not a position - top or bottom.");
            }

            return body;
        }

        // A card as a record - its labels by name, its members by id.
        private static ConnectorRecord CardRecord(JsonObject card) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(card["id"]),
            ["name"] = Text(card["name"]) ?? "",
            ["description"] = Text(card["desc"]) ?? "",
            ["url"] = Text(card["shortUrl"]) ?? Text(card["url"]),
            ["list"] = Text(card["idList"]),
            ["board"] = Text(card["idBoard"]),
            ["due"] = Text(card["due"]),
            ["due_complete"] = card["dueComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var done) && done,
            ["closed"] = card["closed"] is JsonValue closed && closed.TryGetValue<bool>(out var archived) && archived,
            ["labels"] = string.Join(", ", (card["labels"] as JsonArray ?? new JsonArray()).Select(label => Text(label?["name"]) is { Length: > 0 } named ? named : Text(label?["color"])).Where(label => !string.IsNullOrEmpty(label))),
            ["members"] = string.Join(", ", (card["idMembers"] as JsonArray ?? new JsonArray()).Select(Text).Where(id => !string.IsNullOrEmpty(id))),
            ["position"] = card["pos"] is JsonValue pos && pos.TryGetValue<double>(out var place) ? place : null,
            ["last_activity"] = Text(card["dateLastActivity"]),
        });

        // An id or a short link, a card's or a board's address cut down to its short link; a list has a long id only.
        private static string Id(string? given, string what, bool longOnly = false)
        {
            var value = given?.Trim() ?? throw new InvalidOperationException($"Name the {what}.");
            if (Uri.TryCreate(value, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var segments = address.AbsolutePath.Trim('/').Split('/');
                value = segments.Length >= 2 && segments[0] is "c" or "b" ? segments[1] : value; // trello.com/c/{shortLink}/... or /b/{shortLink}/...
            }

            return (longOnly ? LongId : TrelloId).IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not a Trello {what} - its id{(longOnly ? "" : ", short link or address")}.");
        }

        // Trello's OAuth header: the key pair's access key the consumer key, its secret the token - never in an address.
        private static AuthenticationHeaderValue Authorization(IReadOnlyDictionary<string, string> credentials)
        {
            var key = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Trello connection has no API key (the access key).");
            var token = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Trello connection has no token (the secret key).");
            if (key.Any(character => character is '"' or '\\') || token.Any(character => character is '"' or '\\'))
            {
                throw new InvalidOperationException("The Trello key or token holds a quote - not one Trello issues.");
            }

            return new AuthenticationHeaderValue("OAuth", $"oauth_consumer_key=\"{key}\", oauth_token=\"{token}\"");
        }

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Trello's answer as JSON (an empty answer null), a throttled call waiting as Trello says; a failure thrown with its status and
        // Trello's words - its JSON message, or the text it answered.
        private static async Task<JsonNode?> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Trello answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = (json is JsonObject error ? Text(error["message"]) ?? Text(error["error"]) : null) ?? (json is null && payload.Length > 0 ? payload.Trim() : null);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
