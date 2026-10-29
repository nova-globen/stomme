#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Monday;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Monday.com (n8n's Monday.com node, its board items) over the GraphQL API: the token in Authorization alone; a page of
    /// monday.com taken to the API; a board's items - and one group's, and those a column's values keep - page by page by cursor up to
    /// the most asked for, each a plain record with its columns as a JSON object of ids and texts; an item got, created with column
    /// values (sent as JSON text), changed, moved, deleted and updated; the boards page by page; what is not an id, a group's or a
    /// column's id, column values that are a JSON object, a name of one line or a known operation refused before anything is sent;
    /// monday's refusal its message and code; a test naming the user and account.
    /// </summary>
    public sealed class MondayConnectorTests
    {
        private const string Api = "https://api.monday.com/v2";

        private const string Token = "eyJhbGciOiJIUzI1NiJ9.monday.test";

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private sealed class MondayApi : HttpMessageHandler
        {
            public List<(string Url, string? Authorization, string Query, JsonObject Variables)> Requests { get; } = new();

            public Func<string, JsonObject, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var query = body["query"]!.GetValue<string>();
                var variables = body["variables"] as JsonObject ?? new JsonObject();
                Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null, query, variables));
                var (status, answer) = Answer(query, variables);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(MondayApi monday, string operation, Dictionary<string, string> parameters, string endpoint = "https://api.monday.com") =>
            Contexts.Extract(new StubEgress(monday), endpoint, operation, parameters, null, new Dictionary<string, string> { ["token"] = Token });

        private static MondayConnector Connector() => new();

        private static string Item(int id) =>
            "{\"id\":\"" + id + "\",\"name\":\"Order " + id + "\",\"state\":\"active\",\"created_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\",\"board\":{\"id\":\"1234567890\"},\"group\":{\"id\":\"topics\",\"title\":\"To do\"},"
            + "\"column_values\":[{\"id\":\"status\",\"text\":\"Working on it\"},{\"id\":\"date4\",\"text\":\"2026-10-31\"},{\"id\":\"text\",\"text\":null},{\"id\":\"note\",\"text\":\"Café <b>gift</b> & card\"}]}";

        private static string Page(string field, int from, int count, string? cursor) =>
            "{\"cursor\":" + (cursor is null ? "null" : "\"" + cursor + "\"") + ",\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(Item)) + "]}";

        [Fact]
        public async Task A_boards_items_are_read_page_by_page_by_cursor_up_to_the_most_asked_for_the_token_alone()
        {
            var monday = new MondayApi
            {
                Answer = (query, _) => query.StartsWith("query First", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"data\":{\"boards\":[{\"items_page\":" + Page("items_page", 1, 100, "c1") + "}]}}")
                    : (HttpStatusCode.OK, "{\"data\":{\"next_items_page\":" + Page("next_items_page", 101, 100, "c2") + "}}"),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.ItemsOperation, new() { ["board"] = "1234567890", ["max"] = "150" }, endpoint: "https://acme.monday.com/boards/1234567890"));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal(2, monday.Requests.Count);
            Assert.All(monday.Requests, request => Assert.Equal((Api, Token), (request.Url, request.Authorization))); // a monday.com page: the API; the token alone
            Assert.Equal("{\"board\":\"1234567890\",\"limit\":100}", monday.Requests[0].Variables.ToJsonString(Wire));
            Assert.Equal("{\"cursor\":\"c1\",\"limit\":50}", monday.Requests[1].Variables.ToJsonString(Wire)); // monday's cursor; a page no bigger than what is left
            var item = records[0].Fields;
            Assert.Equal(("1", "Order 1", "1234567890", "topics", "To do", "active", "{\"status\":\"Working on it\",\"date4\":\"2026-10-31\",\"text\":null,\"note\":\"Café <b>gift</b> & card\"}"),
                ((string?)item["id"], (string?)item["name"], (string?)item["board_id"], (string?)item["group_id"], (string?)item["group"], (string?)item["state"], (string?)item["columns"]));

            var group = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"boards\":[{\"groups\":[{\"items_page\":" + Page("items_page", 1, 3, null) + "}]}]}}") };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(group, MondayConnector.ItemsOperation, new() { ["board"] = "1234567890", ["group"] = "topics" }, endpoint: "http://127.0.0.1:5272"))).Count);
            var only = Assert.Single(group.Requests); // no cursor: the last
            Assert.Equal(("http://127.0.0.1:5272/v2", "{\"board\":\"1234567890\",\"group\":\"topics\",\"limit\":100}"), (only.Url, only.Variables.ToJsonString(Wire)));
            Assert.Contains("groups(ids: [$group])", only.Query, StringComparison.Ordinal);

            var ten = new MondayApi { Answer = (query, _) => (HttpStatusCode.OK, query.StartsWith("query First", StringComparison.Ordinal) ? "{\"data\":{\"boards\":[{\"items_page\":" + Page("", 1, 100, "c") + "}]}}" : "{\"data\":{\"next_items_page\":" + Page("", 1, 100, "c") + "}}") };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, MondayConnector.ItemsOperation, new() { ["board"] = "1234567890" }))).Count); // by default up to 1,000
            Assert.Equal(10, ten.Requests.Count);

            var byValue = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"items_page_by_column_values\":" + Page("", 1, 2, null) + "}}") };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(byValue, MondayConnector.ItemsByColumnValueOperation, new() { ["board"] = "1234567890", ["column"] = "status", ["values"] = "Done, Stuck" }))).Count);
            Assert.Equal("{\"board\":\"1234567890\",\"columns\":[{\"column_id\":\"status\",\"column_values\":[\"Done\",\"Stuck\"]}],\"limit\":100}", Assert.Single(byValue.Requests).Variables.ToJsonString(Wire));

            var boards = new MondayApi { Answer = (_, variables) => (HttpStatusCode.OK, variables["page"]!.GetValue<int>() == 1 ? "{\"data\":{\"boards\":[" + string.Join(",", Enumerable.Range(1, 100).Select(id => "{\"id\":\"" + id + "\",\"name\":\"Board " + id + "\",\"state\":\"active\",\"board_kind\":\"public\",\"workspace_id\":7}")) + "]}}" : "{\"data\":{\"boards\":[{\"id\":\"101\",\"name\":\"Last\",\"state\":\"active\",\"board_kind\":\"private\",\"workspace_id\":null}]}}") };
            var listed = await Contexts.DrainAsync(Connector(), Context(boards, MondayConnector.BoardsOperation, new()));
            Assert.Equal(101, listed.Count);
            Assert.Equal(("Board 1", "public", "7"), ((string?)listed[0].Fields["name"], (string?)listed[0].Fields["kind"], (string?)listed[0].Fields["workspace_id"]));
            Assert.Equal(new[] { 1, 2 }, boards.Requests.Select(request => request.Variables["page"]!.GetValue<int>())); // a short page the last
        }

        [Fact]
        public async Task An_item_is_got_created_changed_moved_deleted_and_updated()
        {
            var monday = new MondayApi
            {
                Answer = (query, _) => (HttpStatusCode.OK, query switch
                {
                    _ when query.Contains("create_item", StringComparison.Ordinal) => "{\"data\":{\"create_item\":" + Item(9) + "}}",
                    _ when query.Contains("change_multiple_column_values", StringComparison.Ordinal) => "{\"data\":{\"change_multiple_column_values\":" + Item(9) + "}}",
                    _ when query.Contains("move_item_to_group", StringComparison.Ordinal) => "{\"data\":{\"move_item_to_group\":" + Item(9) + "}}",
                    _ when query.Contains("delete_item", StringComparison.Ordinal) => "{\"data\":{\"delete_item\":{\"id\":\"9\"}}}",
                    _ when query.Contains("create_update", StringComparison.Ordinal) => "{\"data\":{\"create_update\":{\"id\":\"501\",\"body\":\"<p>Shipped.</p>\",\"created_at\":\"2026-10-12T08:00:00Z\"}}}",
                    _ => "{\"data\":{\"items\":[" + Item(9) + "]}}",
                }),
            };

            Assert.Equal("Order 9", Assert.Single(await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.GetItemOperation, new() { ["item"] = "9" }))).Fields["name"]);
            Assert.Equal("{\"item\":[\"9\"]}", monday.Requests[^1].Variables.ToJsonString(Wire));
            var two = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"items\":[" + Item(9) + "," + Item(10) + "]}}") };
            Assert.Equal(new[] { "9", "10" }, (await Contexts.DrainAsync(Connector(), Context(two, MondayConnector.GetItemOperation, new() { ["item"] = "9, 10" }))).Select(record => (string?)record.Fields["id"]));
            Assert.Equal("{\"item\":[\"9\",\"10\"]}", Assert.Single(two.Requests).Variables.ToJsonString(Wire)); // several by their ids, as n8n's Get takes them

            await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890", ["group"] = "topics", ["name"] = "Order 9", ["columns"] = "{\"status\": {\"label\": \"Done\"}, \"date4\": {\"date\": \"2026-10-31\"}}" }));
            Assert.Equal("{\"board\":\"1234567890\",\"group\":\"topics\",\"name\":\"Order 9\",\"columns\":\"{\\\"status\\\":{\\\"label\\\":\\\"Done\\\"},\\\"date4\\\":{\\\"date\\\":\\\"2026-10-31\\\"}}\"}", monday.Requests[^1].Variables.ToJsonString(Wire)); // the column values as JSON text
            await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890", ["name"] = "Order 10" }));
            Assert.Equal("{\"board\":\"1234567890\",\"group\":null,\"name\":\"Order 10\",\"columns\":null}", monday.Requests[^1].Variables.ToJsonString(Wire)); // the board's first group, no columns

            await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.UpdateItemOperation, new() { ["board"] = "1234567890", ["item"] = "9", ["columns"] = "{\"status\": {\"label\": \"Stuck\"}}" }));
            Assert.Equal("{\"board\":\"1234567890\",\"item\":\"9\",\"columns\":\"{\\\"status\\\":{\\\"label\\\":\\\"Stuck\\\"}}\"}", monday.Requests[^1].Variables.ToJsonString(Wire));
            await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.MoveItemOperation, new() { ["item"] = "9", ["group"] = "done_group" }));
            Assert.Equal("{\"item\":\"9\",\"group\":\"done_group\"}", monday.Requests[^1].Variables.ToJsonString(Wire));
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.DeleteItemOperation, new() { ["item"] = "9" })));
            Assert.Equal(("9", true), ((string?)deleted.Fields["id"], (bool?)deleted.Fields["deleted"]));
            Assert.Contains("delete_item(item_id: $item)", monday.Requests[^1].Query, StringComparison.Ordinal);
            var update = Assert.Single(await Contexts.DrainAsync(Connector(), Context(monday, MondayConnector.AddUpdateOperation, new() { ["item"] = "9", ["body"] = "<p>Shipped.</p>\nToday." })));
            Assert.Equal("{\"item\":\"9\",\"body\":\"<p>Shipped.</p>\\nToday.\"}", monday.Requests[^1].Variables.ToJsonString(Wire));
            Assert.Equal(("501", "9"), ((string?)update.Fields["id"], (string?)update.Fields["item"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mondays_refusal_is_its_words()
        {
            var monday = new MondayApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://api.monday.com") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(monday, operation, parameters, endpoint)))).Message;

            Assert.Equal("'my board' is not a monday.com board's id (digits).", await Refused(MondayConnector.ItemsOperation, new() { ["board"] = "my board" }));
            Assert.Equal("'To do' is not a group's id (letters, digits and _).", await Refused(MondayConnector.ItemsOperation, new() { ["board"] = "1234567890", ["group"] = "To do" }));
            Assert.Equal("'Due date' is not a column's id (letters, digits and _).", await Refused(MondayConnector.ItemsByColumnValueOperation, new() { ["board"] = "1234567890", ["column"] = "Due date", ["values"] = "x" }));
            Assert.Equal("Give the values to look for - Done, Stuck.", await Refused(MondayConnector.ItemsByColumnValueOperation, new() { ["board"] = "1234567890", ["column"] = "status" }));
            Assert.Equal("The value has a line break - one line only.", await Refused(MondayConnector.ItemsByColumnValueOperation, new() { ["board"] = "1234567890", ["column"] = "status", ["values"] = "Done\nStuck" }));
            Assert.Equal("'#9' is not a monday.com item's id (digits).", await Refused(MondayConnector.GetItemOperation, new() { ["item"] = "#9" }));
            Assert.Equal("'x' is not a monday.com item's id (digits).", await Refused(MondayConnector.GetItemOperation, new() { ["item"] = "9, x" }));
            Assert.Equal("'#9' is not a monday.com item's id (digits).", await Refused(MondayConnector.MoveItemOperation, new() { ["item"] = "#9", ["group"] = "done_group" }));
            Assert.Equal("Give the item's name.", await Refused(MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890" }));
            Assert.Equal("'To do' is not a group's id (letters, digits and _).", await Refused(MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890", ["group"] = "To do", ["name"] = "x" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890", ["name"] = "a\nb" }));
            Assert.Equal("The column values are not JSON - {\"status\": {\"label\": \"Done\"}}.", await Refused(MondayConnector.CreateItemOperation, new() { ["board"] = "1234567890", ["name"] = "x", ["columns"] = "status=Done" }));
            Assert.Equal("The column values are a JSON object of column ids - {\"status\": {\"label\": \"Done\"}}.", await Refused(MondayConnector.UpdateItemOperation, new() { ["board"] = "1234567890", ["item"] = "9", ["columns"] = "{}" }));
            Assert.Equal("'Due date' is not a column's id (letters, digits and _).", await Refused(MondayConnector.UpdateItemOperation, new() { ["board"] = "1234567890", ["item"] = "9", ["columns"] = "{\"Due date\": \"2026-10-31\"}" }));
            Assert.Equal("Give the column values to set - {\"status\": {\"label\": \"Done\"}}.", await Refused(MondayConnector.UpdateItemOperation, new() { ["board"] = "1234567890", ["item"] = "9" }));
            Assert.Equal("'' is not a group's id (letters, digits and _).", await Refused(MondayConnector.MoveItemOperation, new() { ["item"] = "9", ["group"] = "" }));
            Assert.Equal("Give the update's text.", await Refused(MondayConnector.AddUpdateOperation, new() { ["item"] = "9" }));
            Assert.StartsWith("monday.com has no operation 'archive_item'", await Refused("archive_item", new() { ["item"] = "9" }));
            Assert.Equal("'ftp://x.example' is not monday.com's API address - https://api.monday.com.", await Refused(MondayConnector.BoardsOperation, new(), "ftp://x.example"));
            Assert.Empty(monday.Requests);

            var gone = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"boards\":[]}}") };
            Assert.Equal("monday.com has no board 42 - or the token cannot see it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(gone, MondayConnector.ItemsOperation, new() { ["board"] = "42" })))).Message);
            var noGroup = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"boards\":[{\"groups\":[]}]}}") };
            Assert.Equal("monday.com's board 42 has no group topics.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(noGroup, MondayConnector.ItemsOperation, new() { ["board"] = "42", ["group"] = "topics" })))).Message);
            var none = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"items\":[]}}") };
            Assert.Equal("monday.com has no item 9 - or the token cannot see it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(none, MondayConnector.GetItemOperation, new() { ["item"] = "9" })))).Message);
            var refusing = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Column value is not valid\",\"extensions\":{\"code\":\"ColumnValueException\"}}],\"data\":null}") };
            Assert.Equal("monday.com said: Column value is not valid (ColumnValueException)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, MondayConnector.UpdateItemOperation, new() { ["board"] = "1234567890", ["item"] = "9", ["columns"] = "{\"status\": 5}" })))).Message);
            var unauthorized = new MondayApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error_message\":\"Not Authenticated\",\"error_code\":\"UserUnauthorizedException\",\"status_code\":401}") };
            Assert.Equal("HTTP 401: Not Authenticated (UserUnauthorizedException)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(unauthorized, MondayConnector.BoardsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user_and_account()
        {
            var monday = new MondayApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"me\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"account\":{\"name\":\"Acme\"}}}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(monday), "https://api.monday.com", credentials: new Dictionary<string, string> { ["token"] = Token }));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com) on Acme."), (tested.Success, tested.Message));
            Assert.Equal((Api, Token), (monday.Requests[0].Url, monday.Requests[0].Authorization));
            Assert.Equal((AuthenticationType.Bearer, "monday", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "items", "items_by_column_value", "get_item", "create_item", "update_item", "move_item", "delete_item", "add_update", "boards" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
