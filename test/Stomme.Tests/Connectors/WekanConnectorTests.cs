#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Wekan;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Wekan (n8n's Wekan node) without a server: every id, title, colour, date and number held to Wekan's shape before
    /// anything is sent - not even the sign-in; signed in with the user's name and password for a token, the token a bearer on
    /// every call and signed out again when the run ends, the password in no address; a card made only in a list and a swimlane
    /// the board has, its author the connection's user; a comment added only to a card first read on its board - Wekan 12 disables
    /// the account otherwise; a change sent and the card read back; nothing found said where Wekan answers 200 with nothing;
    /// Wekan's message on a failure; a test naming the user.
    /// </summary>
    public sealed class WekanConnectorTests
    {
        private const string Address = "https://wekan.kafe.example";

        private const string Board = "dX832agCuApmLL4d6";

        private const string List = "PP7zcioSEdexNYW36";

        private const string Lane = "Xe8uf6Cc4wmxFEutm";

        private const string Card = "k4o2rtFzsyWyEMXC2";

        private const string User = "zZGARpsxENXrkXDbY";

        private const string Token = "eOq6oPWBgGgybX7dJZoeAiwCiyeBTZgAxCAytcEDYN2";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe", ["password"] = "rostad-bona-7" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Wekan : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Wekan(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, text) = sent.Url.EndsWith("/users/login", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, $"{{\"id\":\"{User}\",\"token\":\"{Token}\",\"tokenExpires\":\"2027-01-20T02:02:15.196Z\"}}")
                    : sent.Url.EndsWith("/users/logout", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"message\":\"You've been logged out!\"}") : answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }

            // The calls between the sign-in and the sign-out.
            public List<Sent> Calls => Requests.Where(sent => sent.Url.Contains("/api/", StringComparison.Ordinal)).ToList();
        }

        private static Task<List<ConnectorRecord>> Run(Wekan wekan, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new WekanConnector(), Contexts.Extract(new StubEgress(wekan), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api" + route;

        [Fact]
        public void It_declares_itself_as_n8ns_wekan_node()
        {
            IConnector connector = new WekanConnector();
            Assert.Equal(("wekan", "Wekan", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(
                new[]
                {
                    "boards", "board", "board_create", "board_delete", "swimlanes", "lists", "list", "list_create", "list_delete",
                    "cards", "card", "card_create", "card_update", "card_delete", "comments", "comment", "comment_create", "comment_delete",
                    "checklists", "checklist", "checklist_create", "checklist_delete", "checklist_item", "checklist_item_update", "checklist_item_delete",
                },
                ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_wekans_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address, Dictionary<string, string>? credentials = null)
            {
                var wekan = new Wekan(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WekanConnector(), Contexts.Extract(new StubEgress(wekan), endpoint, operation, parameters, new Dictionary<string, string>(), credentials ?? Credentials)));
                Assert.Empty(wekan.Requests); // refused before anything was sent - the sign-in too
                return refusal.Message;
            }

            var onCard = new Dictionary<string, string> { ["board"] = Board, ["list"] = List, ["card"] = Card };
            Dictionary<string, string> With(Dictionary<string, string> first, params (string Name, string Value)[] more)
            {
                var all = new Dictionary<string, string>(first);
                foreach (var (name, value) in more)
                {
                    all[name] = value;
                }

                return all;
            }

            Assert.StartsWith("Wekan has no operation 'archive' - boards, board, board_create,", await Refused("archive", new()));
            Assert.Equal("Give the board - its id, ar8JWfcYtNtCBjeLh (board).", await Refused("lists", new()));
            Assert.Equal("'dX832/../users' is not an id - letters and digits, ar8JWfcYtNtCBjeLh (board).", await Refused("board", new() { ["board"] = "dX832/../users" }));
            Assert.Equal("'kafe ada' is not an id - letters and digits, ar8JWfcYtNtCBjeLh (user).", await Refused("boards", new() { ["user"] = "kafe ada" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("boards", new() { ["max"] = "0" }));
            Assert.Equal("Give the board's title (title).", await Refused("board_create", new() { ["title"] = "" }));
            Assert.Equal("'Kafe\nkassa' is more than 500 characters or more than one line (title).", await Refused("board_create", new() { ["title"] = "Kafe\nkassa" }));
            Assert.Equal("'teal' is not one Wekan knows - belize, nephritis, pomegranate, pumpkin, wisteria, moderatepink, strongcyan, limegreen ... (color).", await Refused("board_create", new() { ["title"] = "Kafe", ["color"] = "teal" }));
            Assert.Equal("'secret' is not one Wekan knows - private, public (permission).", await Refused("board_create", new() { ["title"] = "Kafe", ["permission"] = "secret" }));
            Assert.Equal("'yes' is not true or false (is_admin).", await Refused("board_create", new() { ["title"] = "Kafe", ["is_admin"] = "yes" }));
            Assert.Equal("Give the list's title (title).", await Refused("list_create", new() { ["board"] = Board }));
            Assert.Equal("Give a list or a swimlane, one of them, whose cards are read (list, swimlane).", await Refused("cards", new() { ["board"] = Board }));
            Assert.Equal("Give a list or a swimlane, one of them, whose cards are read (list, swimlane).", await Refused("cards", new() { ["board"] = Board, ["list"] = List, ["swimlane"] = Lane }));
            Assert.Equal("Give the list - its id, ar8JWfcYtNtCBjeLh (list).", await Refused("card_create", new() { ["board"] = Board, ["title"] = "Beställ kaffe" }));
            Assert.Equal("Give the card's title (title).", await Refused("card_create", new() { ["board"] = Board, ["list"] = List }));
            Assert.Equal("'Ada Lovelace, Bo' is not ids by commas - letters and digits (assignees).", await Refused("card_create", new() { ["board"] = Board, ["list"] = List, ["title"] = "Beställ kaffe", ["assignees"] = "Ada Lovelace, Bo" }));
            Assert.Equal("Give something to change on the card - a title, a description, a colour, a date, a list to move it to.", await Refused("card_update", onCard));
            Assert.Equal("Give something to change on the card - a title, a description, a colour, a date, a list to move it to.", await Refused("card_update", With(onCard, ("title", "")))); // an empty title is none
            Assert.Equal("'teal' is not one Wekan knows - white, green, yellow, orange, red, purple, blue, sky ... (color).", await Refused("card_update", With(onCard, ("color", "teal"))));
            Assert.Equal("'next friday' is not a date - 2026-10-30T10:00:00Z (due_at).", await Refused("card_update", With(onCard, ("due_at", "next friday"))));
            Assert.Equal("'first' is not a number - 0, 1.5 (sort).", await Refused("card_update", With(onCard, ("sort", "first"))));
            Assert.Equal("'-1' is not hours, 0 or more - 2.5 (spent_time).", await Refused("card_update", With(onCard, ("spent_time", "-1"))));
            Assert.Equal("'other board' is not an id - letters and digits, ar8JWfcYtNtCBjeLh (to_list).", await Refused("card_update", With(onCard, ("to_list", "other board"))));
            Assert.Equal("Give the comment - here or in the step's Query box (comment).", await Refused("comment_create", new() { ["board"] = Board, ["card"] = Card }));
            Assert.Equal("A checklist item is one line of at most 500 characters (items).", await Refused("checklist_create", new() { ["board"] = Board, ["card"] = Card, ["title"] = "Inköp", ["items"] = "Bönor\n" + new string('x', 501) }));
            Assert.Equal("Give something to change on the item - a title, or finished true or false.", await Refused("checklist_item_update", new() { ["board"] = Board, ["card"] = Card, ["checklist"] = "WbBP9nWATWDSCEjB5", ["item"] = "6sk8Bhpc9YhsaijhZ" }));
            Assert.Equal("Give Wekan's address - https://wekan.kafe.example.", await Refused("boards", new(), ""));
            Assert.Equal("'https://wekan.kafe.example/?b=1' is not Wekan's address - https://wekan.kafe.example, with no query.", await Refused("boards", new(), "https://wekan.kafe.example/?b=1"));
            Assert.Equal("The Wekan connection has no username.", await Refused("boards", new(), credentials: new() { ["password"] = "x" }));
            Assert.Equal("The Wekan connection has no password.", await Refused("boards", new(), credentials: new() { ["username"] = "kafe" }));
        }

        [Fact]
        public async Task It_signs_in_for_a_token_sends_it_as_a_bearer_and_signs_out_again()
        {
            var wekan = new Wekan(_ => (HttpStatusCode.OK, $"[{{\"_id\":\"{Board}\",\"title\":\"Kafe\"}},{{\"_id\":\"MEG5rDJA9jvdQdERv\",\"title\":\"Kassa\"}}]"));
            var boards = await Run(wekan, "boards", new() { ["max"] = "1" }, Address + "/api/");
            Assert.Equal((Board, "Kafe"), ((string?)Assert.Single(boards).Fields["id"], (string?)boards[0].Fields["title"]));
            Assert.Equal(new[] { ("POST", Address + "/users/login"), ("GET", Api($"/users/{User}/boards")), ("POST", Address + "/users/logout") }, wekan.Requests.Select(sent => (sent.Method, sent.Url))); // the API's own address given: Wekan's
            Assert.Equal(JsonNode.Parse("{\"username\":\"kafe\",\"password\":\"rostad-bona-7\"}")!.ToJsonString(), JsonNode.Parse(wekan.Requests[0].Body!)!.ToJsonString());
            Assert.Null(wekan.Requests[0].Authorization);
            Assert.Equal(new[] { "Bearer " + Token, "Bearer " + Token }, wekan.Requests.Skip(1).Select(sent => sent.Authorization)); // the token signed out is the one used
            Assert.DoesNotContain(wekan.Requests, sent => sent.Url.Contains("rostad", StringComparison.Ordinal));

            var someone = new Wekan(_ => (HttpStatusCode.OK, "[]"));
            Assert.Empty(await Run(someone, "boards", new() { ["user"] = "Ab3" }));
            Assert.Equal(Api("/users/Ab3/boards"), Assert.Single(someone.Calls).Url);

            // signed out after a failure too
            var failing = new Wekan(_ => (HttpStatusCode.NotFound, "{\"error\":\"Board not found\"}"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(failing, "lists", new() { ["board"] = Board }));
            Assert.Equal(Address + "/users/logout", failing.Requests[^1].Url);
        }

        [Fact]
        public async Task A_card_is_made_only_in_a_list_and_a_swimlane_its_board_has()
        {
            Wekan Board(string list, string lanes) => new(sent => sent.Method switch
            {
                "GET" when sent.Url.EndsWith("/swimlanes", StringComparison.Ordinal) => (HttpStatusCode.OK, lanes),
                "GET" => (HttpStatusCode.OK, list),
                _ => (HttpStatusCode.OK, "{\"_id\":\"St7vSnRfrKW7MMXh3\"}"),
            });
            var lanes = $"[{{\"_id\":\"{Lane}\",\"title\":\"Default\"}},{{\"_id\":\"jn9Q3QcJT6mxHCTvD\",\"title\":\"Kassa\"}}]";
            var aList = $"{{\"_id\":\"{List}\",\"title\":\"Att göra\"}}";
            var parameters = new Dictionary<string, string> { ["board"] = WekanConnectorTests.Board, ["list"] = List, ["title"] = "Beställ kaffe", ["query"] = "Två säckar\nmörkrost", ["members"] = "Ab3, Cd4" };

            var wekan = Board(aList, lanes);
            var made = Assert.Single(await Run(wekan, "card_create", parameters)).Fields;
            Assert.Equal(new[] { ("GET", Api($"/boards/{WekanConnectorTests.Board}/lists/{List}")), ("GET", Api($"/boards/{WekanConnectorTests.Board}/swimlanes")), ("POST", Api($"/boards/{WekanConnectorTests.Board}/lists/{List}/cards")) }, wekan.Calls.Select(sent => (sent.Method, sent.Url)));
            var body = JsonNode.Parse(wekan.Calls[2].Body!)!;
            Assert.Equal(("Beställ kaffe", "Två säckar\nmörkrost", Lane, User, "[\"Ab3\",\"Cd4\"]"), (body["title"]!.GetValue<string>(), body["description"]!.GetValue<string>(), body["swimlaneId"]!.GetValue<string>(), body["authorId"]!.GetValue<string>(), body["members"]!.ToJsonString())); // the board's first swimlane; the connection's user the author
            Assert.Equal(("St7vSnRfrKW7MMXh3", "Beställ kaffe", Lane), ((string?)made["id"], (string?)made["title"], (string?)made["swimlaneId"]));

            var named = Board(aList, lanes);
            await Run(named, "card_create", new(parameters) { ["swimlane"] = "jn9Q3QcJT6mxHCTvD" });
            Assert.Equal("jn9Q3QcJT6mxHCTvD", JsonNode.Parse(named.Calls[2].Body!)!["swimlaneId"]!.GetValue<string>());

            async Task<string> Refused(Wekan wekan, Dictionary<string, string> given)
            {
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(wekan, "card_create", given));
                Assert.DoesNotContain(wekan.Calls, sent => sent.Method == "POST"); // nothing made
                return refusal.Message;
            }

            Assert.Equal("Wekan found no such list - the board has no such list.", await Refused(Board("", lanes), parameters)); // Wekan's 200 with nothing
            Assert.Equal("The board has no swimlane 'x' (swimlane).", await Refused(Board(aList, lanes), new(parameters) { ["swimlane"] = "x" }));
            Assert.Equal("The board has no swimlane to put the card in - make one in Wekan (swimlane).", await Refused(Board(aList, "[]"), parameters));
        }

        [Fact]
        public async Task A_comment_is_added_only_to_a_card_first_read_on_its_board()
        {
            var wekan = new Wekan(sent => (HttpStatusCode.OK, sent.Method == "GET" ? "[]" : "{\"_id\":\"KFKTdak8BxjW7bCxz\"}"));
            var made = Assert.Single(await Run(wekan, "comment_create", new() { ["board"] = Board, ["card"] = Card, ["query"] = "Klart på fredag" })).Fields;
            Assert.Equal(new[] { ("GET", Api($"/boards/{Board}/cards/{Card}/comments")), ("POST", Api($"/boards/{Board}/cards/{Card}/comments")) }, wekan.Calls.Select(sent => (sent.Method, sent.Url)));
            Assert.Equal(JsonNode.Parse($"{{\"comment\":\"Klart på fredag\",\"authorId\":\"{User}\"}}")!.ToJsonString(), JsonNode.Parse(wekan.Calls[1].Body!)!.ToJsonString());
            Assert.Equal(("KFKTdak8BxjW7bCxz", "Klart på fredag"), ((string?)made["id"], (string?)made["comment"]));

            // Wekan 12 disables the account at a comment sent to a card not on the board: the read refuses first, nothing is sent
            var elsewhere = new Wekan(sent => sent.Method == "GET" ? (HttpStatusCode.NotFound, "{\"error\":\"Card not found\"}") : (HttpStatusCode.OK, "{\"_id\":\"x\"}"));
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elsewhere, "comment_create", new() { ["board"] = Board, ["card"] = "nosuchcard", ["comment"] = "x" }));
            Assert.Equal("Wekan answered HTTP 404: Card not found.", refusal.Message);
            Assert.DoesNotContain(elsewhere.Calls, sent => sent.Method == "POST");
        }

        [Fact]
        public async Task A_card_is_changed_moved_within_its_board_and_read_back()
        {
            var card = $"{{\"_id\":\"{Card}\",\"title\":\"Beställ mer kaffe\",\"color\":\"green\",\"dueAt\":\"2026-10-30T10:00:00.000Z\",\"labelIds\":[\"a\",\"b\"],\"sort\":3}}";
            var lanes = $"[{{\"_id\":\"{Lane}\",\"title\":\"Default\"}}]";
            var wekan = new Wekan(sent => (HttpStatusCode.OK, sent.Method == "PUT" ? $"{{\"_id\":\"{Card}\"}}" : sent.Url.EndsWith("/swimlanes", StringComparison.Ordinal) ? lanes : card));
            var changed = Assert.Single(await Run(wekan, "card_update", new()
            {
                ["board"] = Board, ["list"] = List, ["card"] = Card, ["title"] = "Beställ mer kaffe", ["color"] = "Green", ["due_at"] = "2026-10-30T12:00:00+02:00",
                ["labels"] = "a,b", ["sort"] = "3", ["spent_time"] = "2.5", ["is_over_time"] = "true", ["to_swimlane"] = Lane,
            })).Fields;
            Assert.Equal(new[] { ("GET", Api($"/boards/{Board}/swimlanes")), ("PUT", Api($"/boards/{Board}/lists/{List}/cards/{Card}")), ("GET", Api($"/cards/{Card}")) }, wekan.Calls.Select(sent => (sent.Method, sent.Url)));
            Assert.Equal(
                JsonNode.Parse($"{{\"title\":\"Beställ mer kaffe\",\"color\":\"green\",\"dueAt\":\"2026-10-30T10:00:00.000Z\",\"swimlaneId\":\"{Lane}\",\"labelIds\":[\"a\",\"b\"],\"sort\":3,\"spentTime\":2.5,\"isOverTime\":true}}")!.ToJsonString(),
                JsonNode.Parse(wekan.Calls[1].Body!)!.ToJsonString()); // Wekan's names, its colour's own spelling, the date in UTC
            Assert.Equal(("Beställ mer kaffe", "[\"a\",\"b\"]", 3L), ((string?)changed["title"], (string?)changed["labelIds"], (long?)changed["sort"]));

            var lost = new Wekan(sent => (HttpStatusCode.OK, lanes));
            Assert.Equal("The board has no swimlane 'Zz9' (to_swimlane).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(lost, "card_update", new() { ["board"] = Board, ["list"] = List, ["card"] = Card, ["to_swimlane"] = "Zz9" }))).Message);
            Assert.DoesNotContain(lost.Calls, sent => sent.Method == "PUT");
        }

        [Fact]
        public async Task Lists_checklists_and_their_items_are_made_read_changed_and_deleted()
        {
            var wekan = new Wekan(sent => (HttpStatusCode.OK, sent.Method switch
            {
                "POST" => "{\"_id\":\"WbBP9nWATWDSCEjB5\"}",
                "DELETE" => "{\"_id\":\"6sk8Bhpc9YhsaijhZ\"}",
                "PUT" => "{\"_id\":\"6sk8Bhpc9YhsaijhZ\"}",
                _ => "{\"_id\":\"6sk8Bhpc9YhsaijhZ\",\"title\":\"Bönor, mörka\",\"isFinished\":true}",
            }));
            var item = new Dictionary<string, string> { ["board"] = Board, ["card"] = Card, ["checklist"] = "WbBP9nWATWDSCEjB5", ["item"] = "6sk8Bhpc9YhsaijhZ" };
            var made = Assert.Single(await Run(wekan, "checklist_create", new() { ["board"] = Board, ["card"] = Card, ["title"] = "Inköp", ["query"] = "Bönor\r\n\r\n  Filter  \n" })).Fields;
            Assert.Equal("{\"title\":\"Inköp\",\"items\":[\"Bönor\",\"Filter\"]}", JsonNode.Parse(wekan.Calls[0].Body!)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); // one item a line, the empty ones left
            Assert.Equal("WbBP9nWATWDSCEjB5", (string?)made["id"]);
            var changed = Assert.Single(await Run(wekan, "checklist_item_update", new(item) { ["title"] = "Bönor, mörka", ["finished"] = "true" })).Fields;
            Assert.Equal("{\"title\":\"Bönor, mörka\",\"isFinished\":true}", JsonNode.Parse(wekan.Calls[1].Body!)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.Equal((true, "Bönor, mörka"), ((bool?)changed["isFinished"], (string?)changed["title"]));
            var deleted = Assert.Single(await Run(wekan, "checklist_item_delete", item)).Fields;
            Assert.Equal(("6sk8Bhpc9YhsaijhZ", "checklist_item_delete"), ((string?)deleted["id"], (string?)deleted["done"]));
            Assert.Equal(("DELETE", Api($"/boards/{Board}/cards/{Card}/checklists/WbBP9nWATWDSCEjB5/items/6sk8Bhpc9YhsaijhZ")), (wekan.Calls[^1].Method, wekan.Calls[^1].Url));

            var lists = new Wekan(sent => (HttpStatusCode.OK, sent.Method == "POST" ? $"{{\"_id\":\"{List}\"}}" : $"[{{\"_id\":\"{List}\",\"title\":\"Att göra\"}}]"));
            Assert.Equal((List, "Att göra"), ((string?)Assert.Single(await Run(lists, "list_create", new() { ["board"] = Board, ["title"] = "Att göra" })).Fields["id"], (string?)Assert.Single(await Run(lists, "lists", new() { ["board"] = Board })).Fields["title"]));
            Assert.Equal(("POST", Api($"/boards/{Board}/lists"), "{\"title\":\"Att göra\"}"), (lists.Calls[0].Method, lists.Calls[0].Url, JsonNode.Parse(lists.Calls[0].Body!)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        }

        [Fact]
        public async Task A_board_is_made_with_its_owner_the_connections_user_unless_named()
        {
            var wekan = new Wekan(_ => (HttpStatusCode.OK, "{\"_id\":\"Anr68tqi6d8a4W8zS\",\"defaultSwimlaneId\":\"jn9Q3QcJT6mxHCTvD\"}"));
            var made = Assert.Single(await Run(wekan, "board_create", new() { ["title"] = "Kafe", ["color"] = "Nephritis", ["permission"] = "public", ["is_admin"] = "true" })).Fields;
            Assert.Equal(JsonNode.Parse($"{{\"title\":\"Kafe\",\"owner\":\"{User}\",\"color\":\"nephritis\",\"permission\":\"public\",\"isAdmin\":true}}")!.ToJsonString(), JsonNode.Parse(wekan.Calls[0].Body!)!.ToJsonString());
            Assert.Equal(("Anr68tqi6d8a4W8zS", "jn9Q3QcJT6mxHCTvD"), ((string?)made["id"], (string?)made["defaultSwimlaneId"]));
            await Run(wekan, "board_create", new() { ["title"] = "Kassa", ["owner"] = "Ab3" });
            Assert.Equal("Ab3", JsonNode.Parse(wekan.Calls[1].Body!)!["owner"]!.GetValue<string>());
        }

        [Fact]
        public async Task Nothing_found_and_wekans_message_are_said_and_an_answer_that_is_not_wekans_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "card") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Wekan(_ => (status, body)), operation, new() { ["board"] = Board, ["list"] = List, ["card"] = Card, ["comment"] = "KFKTdak8BxjW7bCxz" }))).Message;

            Assert.Equal("Wekan found no such card - it is not in that list on that board.", await Failed(HttpStatusCode.OK, "")); // Wekan's 200 with nothing
            Assert.Equal("Wekan found no such card - it is not in that list on that board.", await Failed(HttpStatusCode.OK, "{}")); // an object with nothing in it found nothing either
            Assert.Equal("Wekan found no such comment - it is not on that card.", await Failed(HttpStatusCode.OK, "", "comment"));
            Assert.Equal("Wekan answered HTTP 404: Board not found.", await Failed(HttpStatusCode.NotFound, "{\"error\":\"Board not found\"}"));
            Assert.Equal("Wekan answered HTTP 400: listId does not belong to this board. To move a card to another board, send newBoardId, newSwimlaneId and newListId.", await Failed(HttpStatusCode.BadRequest, "{\"error\":\"listId does not belong to this board. To move a card to another board, send newBoardId, newSwimlaneId and newListId.\"}"));
            Assert.Equal("Wekan answered HTTP 401: Unauthorized. - check the username and password, and that the user may see the board.", await Failed(HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\"}"));
            Assert.Equal("Wekan answered HTTP 500. - Wekan answers so to a value it does not take.", await Failed(HttpStatusCode.InternalServerError, "<!DOCTYPE html><pre>Internal Server Error</pre>"));
            Assert.Equal("Wekan answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html>WeKan</html>"));
            Assert.Equal("Wekan answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"_id\":\"x\"}", "comments"));
        }

        [Fact]
        public async Task A_test_names_the_user_and_a_refused_sign_in_is_said()
        {
            var connector = new WekanConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Wekan(_ => (HttpStatusCode.OK, $"{{\"_id\":\"{User}\",\"username\":\"kafe\"}}"))), Address, null, Credentials));
            Assert.Equal((true, "Wekan answers: signed in as kafe."), (ok.Success, ok.Message));

            var refused = new RefusingWekan();
            var failed = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(refused), Address, null, Credentials));
            Assert.Equal((false, "Wekan refused the sign-in (HTTP 401): login-failed: Incorrect username, email address or password. - check the username and the password; Wekan refuses a disabled account so too."), (failed.Success, failed.Message));
            Assert.Single(refused.Urls); // nothing after a refused sign-in, no sign-out of a token never had
        }

        private sealed class RefusingWekan : HttpMessageHandler
        {
            public List<string> Urls { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"login-failed\",\"reason\":\"Incorrect username, email address or password.\"}", Encoding.UTF8, "application/json") });
            }
        }
    }

    /// <summary>
    /// BL-229 - Wekan LIVE, against this machine's throwaway Wekan 12 over MongoDB (scripts/wekan-test-server.ps1) - fail-not-skip
    /// without it (D46): a test signing in and a wrong password refused in Wekan's words; a board made, its list, a card in its
    /// first swimlane, the card changed and read back, a comment added, read and deleted, a checklist made with its items, an item
    /// finished, everything deleted again; a card made in a swimlane the board does not have refused before Wekan keeps it; a
    /// comment for a card not on the board refused by the read before it - and the account still signs in after, which is the
    /// point: Wekan 12 disables it at such a comment.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class WekanConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> SignIn => new() { ["username"] = WekanTestEndpoint.Username, ["password"] = WekanTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { new Uri(WekanTestEndpoint.Endpoint).Host });

        private static async Task<Dictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) =>
            Assert.Single(await All(operation, parameters)).Fields.ToDictionary(pair => pair.Key, pair => pair.Value);

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new WekanConnector(), Contexts.Extract(Egress, WekanTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), SignIn, timeoutSeconds: 60));

        [Fact]
        public async Task A_test_signs_in_and_a_wrong_user_is_refused()
        {
            var tested = await new WekanConnector().TestConnectionAsync(Contexts.Test(Egress, WekanTestEndpoint.Endpoint, credentials: SignIn, timeoutSeconds: 60));
            Assert.Equal((true, $"Wekan answers: signed in as {WekanTestEndpoint.Username}."), (tested.Success, tested.Message));
            var wrong = await new WekanConnector().TestConnectionAsync(Contexts.Test(Egress, WekanTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["username"] = "nobody-" + Guid.NewGuid().ToString("N")[..8], ["password"] = "x" }, timeoutSeconds: 60));
            Assert.Equal((false, "Wekan refused the sign-in (HTTP 401): login-failed: Incorrect username, email address or password. - check the username and the password; Wekan refuses a disabled account so too."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_board_is_worked_through_and_deleted_again()
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var board = (string)(await One("board_create", new() { ["title"] = "Kafe " + marker, ["color"] = "nephritis" }))["id"]!;
            output.WriteLine($"board {board}");
            try
            {
                var lane = (string)Assert.Single(await All("swimlanes", new() { ["board"] = board })).Fields["id"]!;
                var list = (string)(await One("list_create", new() { ["board"] = board, ["title"] = "Att göra" }))["id"]!;
                var card = await One("card_create", new() { ["board"] = board, ["list"] = list, ["title"] = "Beställ kaffe", ["query"] = "Två säckar" });
                var cardId = (string)card["id"]!;
                Assert.Equal(lane, (string?)card["swimlaneId"]);
                var read = await One("card", new() { ["board"] = board, ["list"] = list, ["card"] = cardId });
                Assert.Equal(("Beställ kaffe", "Två säckar", lane), ((string?)read["title"], (string?)read["description"], (string?)read["swimlaneId"]));

                var changed = await One("card_update", new() { ["board"] = board, ["list"] = list, ["card"] = cardId, ["title"] = "Beställ mer kaffe", ["color"] = "green", ["due_at"] = "2026-10-30T10:00:00Z" });
                Assert.Equal(("Beställ mer kaffe", "green", "2026-10-30T10:00:00.000Z"), ((string?)changed["title"], (string?)changed["color"], (string?)changed["dueAt"]));
                Assert.Equal(cardId, (string?)Assert.Single(await All("cards", new() { ["board"] = board, ["swimlane"] = lane })).Fields["id"]);

                var lost = await Assert.ThrowsAsync<InvalidOperationException>(() => All("card_create", new() { ["board"] = board, ["list"] = list, ["title"] = "Vilse", ["swimlane"] = "nosuchlane" }));
                Assert.Equal("The board has no swimlane 'nosuchlane' (swimlane).", lost.Message);
                var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => All("card_create", new() { ["board"] = board, ["list"] = "nosuchlist", ["title"] = "Vilse" }));
                Assert.Equal("Wekan found no such list - the board has no such list.", gone.Message);
                Assert.Single(await All("cards", new() { ["board"] = board, ["list"] = list })); // neither kept

                var comment = (string)(await One("comment_create", new() { ["board"] = board, ["card"] = cardId, ["comment"] = "Klart på fredag" }))["id"]!;
                Assert.Equal("Klart på fredag", (string?)(await One("comment", new() { ["board"] = board, ["card"] = cardId, ["comment"] = comment }))["text"]);
                var elsewhere = await Assert.ThrowsAsync<InvalidOperationException>(() => All("comment_create", new() { ["board"] = board, ["card"] = "nosuchcard", ["comment"] = "x" }));
                Assert.Equal("Wekan answered HTTP 404: Card not found.", elsewhere.Message);
                Assert.Single(await All("comments", new() { ["board"] = board, ["card"] = cardId })); // still signed in: the account was not disabled

                var checklist = (string)(await One("checklist_create", new() { ["board"] = board, ["card"] = cardId, ["title"] = "Inköp", ["items"] = "Bönor\nFilter" }))["id"]!;
                var items = JsonNode.Parse((string)(await One("checklist", new() { ["board"] = board, ["card"] = cardId, ["checklist"] = checklist }))["items"]!)!.AsArray();
                Assert.Equal(new[] { "Bönor", "Filter" }, items.Select(item => item!["title"]!.GetValue<string>()));
                var item = new Dictionary<string, string> { ["board"] = board, ["card"] = cardId, ["checklist"] = checklist, ["item"] = items[0]!["_id"]!.GetValue<string>() };
                Assert.Equal(true, (bool?)(await One("checklist_item_update", new(item) { ["finished"] = "true" }))["isFinished"]);
                await One("checklist_item_delete", item);
                await One("checklist_delete", new() { ["board"] = board, ["card"] = cardId, ["checklist"] = checklist });
                await One("comment_delete", new() { ["board"] = board, ["card"] = cardId, ["comment"] = comment });
                Assert.Empty(await All("comments", new() { ["board"] = board, ["card"] = cardId }));
                await One("card_delete", new() { ["board"] = board, ["list"] = list, ["card"] = cardId });
                Assert.Equal("Wekan found no such card - it is not in that list on that board.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("card", new() { ["board"] = board, ["list"] = list, ["card"] = cardId }))).Message);
                await One("list_delete", new() { ["board"] = board, ["list"] = list });
            }
            finally
            {
                await One("board_delete", new() { ["board"] = board });
            }

            Assert.Equal("Wekan answered HTTP 404: Board not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("board", new() { ["board"] = board }))).Message);
        }
    }
}
