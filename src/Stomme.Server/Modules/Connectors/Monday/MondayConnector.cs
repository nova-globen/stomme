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

namespace Stomme.Modules.Connectors.Monday
{
    /// <summary>
    /// Monday.com (n8n's Monday.com node, its board items: found - a board's or a group's, or by a column's value - got,
    /// created, changed, moved, deleted and updated): monday.com's GraphQL API (<c>https://api.monday.com/v2</c>), a personal API
    /// token as monday takes it, in <c>Authorization</c> alone. A test names the token's user and account.
    /// </summary>
    /// <remarks>
    /// A board and an item are named by their id (digits), a group and a column by theirs (letters, digits and _), each held to that
    /// shape before it is sent. <c>items</c>: a board's items - or one group's - page by page by monday's cursor (100 a page, a page no
    /// bigger than what is left) up to <c>max</c> (1,000); one record each: its id, name, board, group, state, its columns (a JSON
    /// object of each column's id and its text) and its times. <c>items_by_column_value</c>: the items whose column holds one of the
    /// values given. <c>get_item</c> one, or several by their ids comma-separated. <c>create_item</c>: a board, a group, a name and its column values (a JSON object of column
    /// ids and monday's values); <c>update_item</c> changes column values; <c>move_item</c> moves one to another group;
    /// <c>delete_item</c> deletes one; <c>add_update</c> posts an update on one. <c>boards</c>: the account's boards. A failure is
    /// thrown with monday's message, its code beside it.
    /// </remarks>
    public sealed class MondayConnector : IConnector, IConnectorExtractor
    {
        public const string ItemsOperation = "items";

        public const string ItemsByColumnValueOperation = "items_by_column_value";

        public const string GetItemOperation = "get_item";

        public const string CreateItemOperation = "create_item";

        public const string UpdateItemOperation = "update_item";

        public const string MoveItemOperation = "move_item";

        public const string DeleteItemOperation = "delete_item";

        public const string AddUpdateOperation = "add_update";

        public const string BoardsOperation = "boards";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const string ItemFields = "fragment ItemFields on Item { id name state created_at updated_at board { id } group { id title } column_values { id text } }";

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Key = new("^[A-Za-z0-9_]{1,100}$", RegexOptions.CultureInvariant);

        // JSON as written: an update's HTML and a column's words sent as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "monday";

        public string DisplayName => "monday.com";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The token as monday.com names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "API token (Avatar > Developers > My access tokens)" };

        public string EndpointExample => "https://api.monday.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://monday.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.monday.com/api-reference/docs");

        private static readonly ConnectorConfigField BoardField = new("board", "Board", ConnectorConfigFieldType.Text, Required: true, Description: "The board's id (digits) - in its address, /boards/1234567890.");

        private static readonly ConnectorConfigField ItemField = new("item", "Item", ConnectorConfigFieldType.Text, Required: true, Description: "The item's id (digits).");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ColumnsField = new("columns", "Column values", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON of column ids and monday's values - {\"status\": {\"label\": \"Done\"}, \"text\": \"Note\"}.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ItemsOperation, "Get items", "A board's items - or one group's - one record each: its name, group and columns - n8n's board item Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BoardField,
                    new ConnectorConfigField("group", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "Only this group's - its id, topics."),
                    MaxField,
                }),
            },
            new ConnectorOperation(ItemsByColumnValueOperation, "Get items by a column's value", "The items whose column holds one of the values - n8n's board item Get by Column Value.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BoardField,
                    new ConnectorConfigField("column", "Column", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - status."),
                    new ConnectorConfigField("values", "Values", ConnectorConfigFieldType.Text, Required: true, Description: "Comma-separated - Done, Stuck."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetItemOperation, "Get items by id", "One item or several - n8n's board item Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ItemField with { Description = "The item's id (digits) - several comma-separated, 9, 10." } }),
            },
            new ConnectorOperation(CreateItemOperation, "Create an item", "Adds an item to a board - n8n's board item Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BoardField,
                    new ConnectorConfigField("group", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "Its id. Empty: the board's first."),
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    ColumnsField,
                }),
            },
            new ConnectorOperation(UpdateItemOperation, "Change an item's columns", "Sets column values on an item - n8n's board item Change Column Value(s).")
            {
                Parameters = new ConnectorConfigSchema(new[] { BoardField, ItemField, ColumnsField with { Required = true } }),
            },
            new ConnectorOperation(MoveItemOperation, "Move an item", "Moves an item to another group - n8n's board item Move.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ItemField, new ConnectorConfigField("group", "Group", ConnectorConfigFieldType.Text, Required: true, Description: "The group's id.") }),
            },
            new ConnectorOperation(DeleteItemOperation, "Delete an item", "Deletes an item - n8n's board item Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ItemField }),
            },
            new ConnectorOperation(AddUpdateOperation, "Post an update", "Posts an update on an item - n8n's board item Add Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ItemField, new ConnectorConfigField("body", "Update", ConnectorConfigFieldType.Multiline, Required: true, Description: "Text or HTML.") }),
            },
            new ConnectorOperation(BoardsOperation, "Get boards", "The account's boards - their ids, names and kinds - n8n's board Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { MaxField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var data = await QueryAsync(client, Token(context.Credentials), api, "query { me { name email account { name } } }", new JsonObject(), cancellationToken);
                return Text(data?["me"]?["email"]) is { } email
                    ? ConnectionTestResult.Ok($"Signed in as {Text(data?["me"]?["name"]) ?? email} ({email}){(Text(data?["me"]?["account"]?["name"]) is { } account ? $" on {account}" : "")}.")
                    : ConnectionTestResult.Fail("monday.com answered without a user - check the API token.");
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
            var api = Api(context.Endpoint);
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? ItemsOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            if (!new[] { ItemsOperation, ItemsByColumnValueOperation, GetItemOperation, CreateItemOperation, UpdateItemOperation, MoveItemOperation, DeleteItemOperation, AddUpdateOperation, BoardsOperation }.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"monday.com has no operation '{operation}' - items, items_by_column_value, get_item, create_item, update_item, move_item, delete_item, add_update or boards.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, ItemsOperation) || Is(operation, ItemsByColumnValueOperation))
            {
                var board = Id(Values.Get(parameters, "board"), "board");
                string first;
                var variables = new JsonObject { ["board"] = board };
                if (Is(operation, ItemsByColumnValueOperation))
                {
                    var column = Name(Values.Get(parameters, "column"), "column");
                    var values = (Values.Get(parameters, "values") ?? throw new InvalidOperationException("Give the values to look for - Done, Stuck."))
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => OneLine(value, "value")).ToArray();
                    variables["columns"] = new JsonArray(new JsonObject { ["column_id"] = column, ["column_values"] = new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) });
                    first = "query First($board: ID!, $columns: [ItemsPageByColumnValuesQuery!], $limit: Int!) { items_page_by_column_values(board_id: $board, columns: $columns, limit: $limit) { cursor items { ...ItemFields } } } " + ItemFields;
                }
                else if (Values.Get(parameters, "group") is { } group)
                {
                    variables["group"] = Name(group, "group");
                    first = "query First($board: ID!, $group: String!, $limit: Int!) { boards(ids: [$board]) { groups(ids: [$group]) { items_page(limit: $limit) { cursor items { ...ItemFields } } } } } " + ItemFields;
                }
                else
                {
                    first = "query First($board: ID!, $limit: Int!) { boards(ids: [$board]) { items_page(limit: $limit) { cursor items { ...ItemFields } } } } " + ItemFields;
                }

                var read = 0;
                string? cursor = null;
                for (var page = 0; ; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    JsonNode? found;
                    if (page == 0)
                    {
                        variables["limit"] = size;
                        var data = await QueryAsync(client, token, api, first, variables, cancellationToken);
                        if (data?["boards"] is JsonArray { Count: 0 })
                        {
                            throw new InvalidOperationException($"monday.com has no board {board} - or the token cannot see it.");
                        }

                        var onBoard = First(data?["boards"]);
                        if (variables["group"] is { } group && onBoard is not null && First(onBoard["groups"]) is null)
                        {
                            throw new InvalidOperationException($"monday.com's board {board} has no group {group}.");
                        }

                        found = data?["items_page_by_column_values"] ?? (variables.ContainsKey("group") ? First(onBoard?["groups"])?["items_page"] : onBoard?["items_page"]);
                    }
                    else
                    {
                        var data = await QueryAsync(client, token, api, "query Next($cursor: String!, $limit: Int!) { next_items_page(cursor: $cursor, limit: $limit) { cursor items { ...ItemFields } } } " + ItemFields,
                            new JsonObject { ["cursor"] = cursor, ["limit"] = size }, cancellationToken);
                        found = data?["next_items_page"];
                    }

                    var items = (found?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var each in items)
                    {
                        read++;
                        yield return Record(each);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    cursor = Text(found?["cursor"]);
                    if (cursor is null || items.Count == 0)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, BoardsOperation))
            {
                var read = 0;
                for (var page = 1; ; page++)
                {
                    var size = Math.Min(PageSize, max - read);
                    var data = await QueryAsync(client, token, api, "query Boards($limit: Int!, $page: Int!) { boards(limit: $limit, page: $page) { id name state board_kind workspace_id } }", new JsonObject { ["limit"] = size, ["page"] = page }, cancellationToken);
                    var boards = (data?["boards"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var board in boards)
                    {
                        read++;
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = Text(board["id"]), ["name"] = Text(board["name"]), ["state"] = Text(board["state"]), ["kind"] = Text(board["board_kind"]), ["workspace_id"] = Text(board["workspace_id"]) ?? board["workspace_id"]?.ToJsonString(),
                        });
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    if (boards.Count < size)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, CreateItemOperation))
            {
                var variables = new JsonObject
                {
                    ["board"] = Id(Values.Get(parameters, "board"), "board"),
                    ["group"] = Values.Get(parameters, "group") is { } group ? Name(group, "group") : null,
                    ["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the item's name."), "name"),
                    ["columns"] = Values.Get(parameters, "columns") is { } columns ? Columns(columns) : null,
                };
                var data = await QueryAsync(client, token, api, "mutation Create($board: ID!, $group: String, $name: String!, $columns: JSON) { create_item(board_id: $board, group_id: $group, item_name: $name, column_values: $columns) { ...ItemFields } } " + ItemFields, variables, cancellationToken);
                yield return Record(data?["create_item"] as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, GetItemOperation))
            {
                var ids = (Values.Get(parameters, "item") ?? "").Split(',', StringSplitOptions.TrimEntries).Select(id => Id(id, "item")).ToArray();
                var data = await QueryAsync(client, token, api, "query Item($item: [ID!]) { items(ids: $item) { ...ItemFields } } " + ItemFields, new JsonObject { ["item"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) }, cancellationToken);
                var found = (data?["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                if (found.Count == 0)
                {
                    throw new InvalidOperationException($"monday.com has no item {string.Join(", ", ids)} - or the token cannot see it.");
                }

                foreach (var one in found)
                {
                    yield return Record(one);
                }

                yield break;
            }

            var item = Id(Values.Get(parameters, "item"), "item");

            if (Is(operation, UpdateItemOperation))
            {
                var variables = new JsonObject
                {
                    ["board"] = Id(Values.Get(parameters, "board"), "board"),
                    ["item"] = item,
                    ["columns"] = Columns(Values.Get(parameters, "columns") ?? throw new InvalidOperationException("Give the column values to set - {\"status\": {\"label\": \"Done\"}}.")),
                };
                var data = await QueryAsync(client, token, api, "mutation Change($board: ID!, $item: ID!, $columns: JSON!) { change_multiple_column_values(board_id: $board, item_id: $item, column_values: $columns) { ...ItemFields } } " + ItemFields, variables, cancellationToken);
                yield return Record(data?["change_multiple_column_values"] as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, MoveItemOperation))
            {
                var group = Name(Values.Get(parameters, "group"), "group");
                var data = await QueryAsync(client, token, api, "mutation Move($item: ID!, $group: String!) { move_item_to_group(item_id: $item, group_id: $group) { ...ItemFields } } " + ItemFields, new JsonObject { ["item"] = item, ["group"] = group }, cancellationToken);
                yield return Record(data?["move_item_to_group"] as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, DeleteItemOperation))
            {
                await QueryAsync(client, token, api, "mutation Delete($item: ID!) { delete_item(item_id: $item) { id } }", new JsonObject { ["item"] = item }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = item, ["deleted"] = true });
                yield break;
            }

            // An update posted on the item.
            var bodyText = Values.Get(parameters, "body") ?? throw new InvalidOperationException("Give the update's text.");
            var posted = await QueryAsync(client, token, api, "mutation Post($item: ID!, $body: String!) { create_update(item_id: $item, body: $body) { id body created_at } }", new JsonObject { ["item"] = item, ["body"] = bodyText }, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(posted?["create_update"]?["id"]), ["item"] = item, ["body"] = Text(posted?["create_update"]?["body"]), ["created_at"] = Text(posted?["create_update"]?["created_at"]),
            });
        }

        // Column values as monday takes them: a JSON object of column ids and values, sent as JSON text.
        private static string Columns(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The column values are not JSON - {\"status\": {\"label\": \"Done\"}}.");
            }

            if (parsed is not JsonObject columns || columns.Count == 0)
            {
                throw new InvalidOperationException("The column values are a JSON object of column ids - {\"status\": {\"label\": \"Done\"}}.");
            }

            foreach (var (column, _) in columns)
            {
                Name(column, "column");
            }

            return columns.ToJsonString(Wire);
        }

        private static ConnectorRecord Record(JsonObject item)
        {
            var columns = new JsonObject();
            foreach (var column in (item["column_values"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (Text(column["id"]) is { } id)
                {
                    columns[id] = Text(column["text"]);
                }
            }

            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(item["id"]),
                ["name"] = Text(item["name"]),
                ["board_id"] = Text(item["board"]?["id"]),
                ["group_id"] = Text(item["group"]?["id"]),
                ["group"] = Text(item["group"]?["title"]),
                ["state"] = Text(item["state"]),
                ["columns"] = columns.ToJsonString(Wire),
                ["created_at"] = Text(item["created_at"]),
                ["updated_at"] = Text(item["updated_at"]),
            });
        }

        // monday.com's GraphQL endpoint: its own address (a stand-in's as given), /v2; a monday.com page taken to the API.
        private static string Api(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not monday.com's API address - https://api.monday.com.");
            }

            return address.Host.Equals("monday.com", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".monday.com", StringComparison.OrdinalIgnoreCase)
                ? "https://api.monday.com/v2"
                : address.GetLeftPart(UriPartial.Authority) + "/v2";
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The monday.com connection has no API token.");

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a monday.com {what}'s id (digits).");

        private static string Name(string? given, string what) =>
            given is not null && Key.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s id (letters, digits and _).");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // An array's first element, or null - a JsonArray's indexer throws on an empty one.
        private static JsonNode? First(JsonNode? node) => node is JsonArray { Count: > 0 } array ? array[0] : null;

        // A GraphQL query's data, the token in Authorization alone as monday takes it; a throttled call waits as monday says; a failure
        // thrown with monday's message and its code.
        private static async Task<JsonNode?> QueryAsync(HttpClient client, string token, string api, string query, JsonObject variables, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["query"] = query, ["variables"] = variables.DeepClone() }.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Authorization", token);
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
                throw new InvalidOperationException($"monday.com answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            var error = (json?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var said = error is not null
                ? string.Join(" ", new[] { Text(error["message"]), Text(error["extensions"]?["code"]) is { } code ? $"({code})" : null }.Where(part => part is not null))
                : Text(json?["error_message"]) is { } message ? message + (Text(json?["error_code"]) is { } errorCode ? $" ({errorCode})" : "") : null;
            if (!response.IsSuccessStatusCode || said is not null)
            {
                var words = said ?? "";
                throw new InvalidOperationException($"{(response.IsSuccessStatusCode ? "monday.com said" : $"HTTP {(int)response.StatusCode}")}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return json?["data"];
        }
    }
}
