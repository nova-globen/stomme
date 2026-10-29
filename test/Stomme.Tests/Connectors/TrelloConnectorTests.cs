#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Trello;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Trello (n8n's Trello node: a card created, got, updated and deleted, a comment added, a list's cards got): the
    /// API key and token in Trello's OAuth header, never in an address; a list's or a board's open cards up to the most asked for,
    /// each a plain record - its labels by name; a card made on a list, read by its address, changed, moved, marked done,
    /// archived and deleted; a comment added; a card, a list, a label, a member, a name's line break, a due date, a position and an
    /// update of nothing refused before anything is sent; Trello's refusal its words, JSON or text; a test naming the member.
    /// </summary>
    public sealed class TrelloConnectorTests
    {
        private const string Api = "https://trello.example.test/1";

        private const string Card = "5f1a2b3c4d5e6f7a8b9c0d1e";

        private const string List = "60a1b2c3d4e5f6a7b8c9d0e1";

        private const string Label = "61a1b2c3d4e5f6a7b8c9d0e1";

        private static readonly Dictionary<string, string> Keys = new() { ["access-key"] = "key-1", ["secret-key"] = "token-1" };

        private sealed class TrelloApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, answer.StartsWith('{') || answer.StartsWith('[') ? "application/json" : "text/plain") };
            }
        }

        private static ConnectorExtractContext Context(TrelloApi trello, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(trello), Api, operation, parameters, credentials: Keys);

        private static string CardJson(string id, string name, string due = "null", bool done = false) =>
            "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"desc\":\"Twelve, please.\",\"shortUrl\":\"https://trello.example/c/AbCd1234\",\"idList\":\"" + List + "\",\"idBoard\":\"b1\",\"due\":" + due
                + ",\"dueComplete\":" + (done ? "true" : "false") + ",\"closed\":false,\"labels\":[{\"id\":\"" + Label + "\",\"name\":\"Billing\",\"color\":\"red\"},{\"id\":\"l2\",\"name\":\"\",\"color\":\"green\"}],\"idMembers\":[\"m1\",\"m2\"],\"pos\":16384,\"dateLastActivity\":\"2026-10-02T10:00:00.000Z\"}";

        [Fact]
        public async Task A_lists_or_a_boards_open_cards_are_read_up_to_the_most_asked_for_with_the_key_pair_in_the_oauth_header()
        {
            var trello = new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.OK, "[" + CardJson(Card, "Order 1001") + "," + CardJson("5f1a2b3c4d5e6f7a8b9c0d1f", "Order 1002") + "," + CardJson("5f1a2b3c4d5e6f7a8b9c0d20", "Order 1003") + "]") };
            var records = await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.CardsOperation, new() { ["list"] = List, ["max"] = "2" }));

            Assert.Equal(new[] { "Order 1001", "Order 1002" }, records.Select(record => (string?)record.Fields["name"])); // the most asked for
            Assert.Equal(($"{Api}/lists/{List}/cards", "OAuth oauth_consumer_key=\"key-1\", oauth_token=\"token-1\""), (trello.Requests[0].Url, trello.Requests[0].Authorization));
            Assert.DoesNotContain("token-1", trello.Requests[0].Url); // never in an address
            var first = records[0].Fields;
            Assert.Equal((Card, "Twelve, please.", "https://trello.example/c/AbCd1234", List, "Billing, green", "m1, m2", 16384d, false),
                ((string?)first["id"], (string?)first["description"], (string?)first["url"], (string?)first["list"], (string?)first["labels"], (string?)first["members"], (double?)first["position"], (bool?)first["closed"]));

            var board = new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.OK, "[" + CardJson(Card, "Order 1001") + "]") };
            Assert.Single(await Contexts.DrainAsync(new TrelloConnector(), Context(board, TrelloConnector.CardsOperation, new() { ["board"] = "https://trello.com/b/AbCd1234/orders" })));
            Assert.Equal($"{Api}/boards/AbCd1234/cards/open", board.Requests[0].Url); // a board's address cut to its short link
        }

        [Fact]
        public async Task A_card_is_made_on_a_list_read_by_its_address_changed_moved_done_archived_commented_on_and_deleted()
        {
            var trello = new TrelloApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"_value\":null}"),
                    ("POST", _) when url.EndsWith("/actions/comments", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"id\":\"c777\",\"data\":{\"text\":\"Shipped.\"}}"),
                    _ => (HttpStatusCode.OK, CardJson(Card, "Order 1001", "\"2026-10-12T15:00:00.000Z\"")),
                },
            };

            var made = await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.CreateCardOperation, new()
            {
                ["list"] = List, ["name"] = "Order 1001", ["description"] = "Twelve, please.\nThanks.", ["due"] = "2026-10-12T17:00:00+02:00", ["labels"] = Label, ["position"] = "Top",
            }));
            Assert.Equal((HttpMethod.Post, $"{Api}/cards"), (trello.Requests[0].Method, trello.Requests[0].Url));
            Assert.Equal("{\"name\":\"Order 1001\",\"desc\":\"Twelve, please.\\nThanks.\",\"due\":\"2026-10-12T15:00:00.000Z\",\"idLabels\":\"" + Label + "\",\"pos\":\"top\",\"idList\":\"" + List + "\"}", trello.Requests[0].Body);
            Assert.Equal((Card, "2026-10-12T15:00:00.000Z"), ((string?)Assert.Single(made).Fields["id"], (string?)made[0].Fields["due"]));

            await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.GetCardOperation, new() { ["card"] = "https://trello.com/c/AbCd1234/42-order-1001" }));
            Assert.Equal($"{Api}/cards/AbCd1234", trello.Requests[1].Url); // its address cut to its short link

            await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.UpdateCardOperation, new() { ["card"] = Card, ["list"] = "60a1b2c3d4e5f6a7b8c9d0ff", ["due_complete"] = "true", ["archived"] = "False" }));
            Assert.Equal((HttpMethod.Put, $"{Api}/cards/{Card}", "{\"idList\":\"60a1b2c3d4e5f6a7b8c9d0ff\",\"dueComplete\":true,\"closed\":false}"), (trello.Requests[2].Method, trello.Requests[2].Url, trello.Requests[2].Body));

            var comment = await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.CommentOperation, new() { ["card"] = Card, ["text"] = "Shipped." }));
            Assert.Equal(($"{Api}/cards/{Card}/actions/comments", "{\"text\":\"Shipped.\"}"), (trello.Requests[3].Url, trello.Requests[3].Body));
            Assert.Equal("c777", Assert.Single(comment).Fields["comment_id"]);

            var removed = await Contexts.DrainAsync(new TrelloConnector(), Context(trello, TrelloConnector.DeleteCardOperation, new() { ["card"] = Card }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/cards/{Card}"), (trello.Requests[4].Method, trello.Requests[4].Url));
            Assert.True((bool?)Assert.Single(removed).Fields["deleted"]);
        }

        [Fact]
        public async Task What_could_reach_the_path_or_mislead_trello_is_refused_before_anything_is_sent_and_trellos_refusal_is_its_words()
        {
            var trello = new TrelloApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TrelloConnector(), Context(trello, operation, parameters)))).Message;

            Assert.StartsWith("'../x' is not a Trello card", await Refused(TrelloConnector.GetCardOperation, new() { ["card"] = "../x" }));
            Assert.StartsWith("'AbCd1234' is not a Trello list", await Refused(TrelloConnector.CardsOperation, new() { ["list"] = "AbCd1234" })); // a list has a long id only
            Assert.Equal("Name the list - or the board - whose cards to read.", await Refused(TrelloConnector.CardsOperation, new()));
            Assert.Equal("'red' is not a Trello label's id.", await Refused(TrelloConnector.CreateCardOperation, new() { ["list"] = List, ["name"] = "x", ["labels"] = "red" }));
            Assert.Equal("'ada' is not a Trello member's id.", await Refused(TrelloConnector.CreateCardOperation, new() { ["list"] = List, ["name"] = "x", ["members"] = "ada" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(TrelloConnector.CreateCardOperation, new() { ["list"] = List, ["name"] = "Order\nBcc" }));
            Assert.StartsWith("'next friday' is not a date", await Refused(TrelloConnector.CreateCardOperation, new() { ["list"] = List, ["name"] = "x", ["due"] = "next friday" }));
            Assert.StartsWith("'middle' is not a position", await Refused(TrelloConnector.CreateCardOperation, new() { ["list"] = List, ["name"] = "x", ["position"] = "middle" }));
            Assert.StartsWith("'yes' is not true or false", await Refused(TrelloConnector.UpdateCardOperation, new() { ["card"] = Card, ["archived"] = "yes" }));
            Assert.StartsWith("Give something to change", await Refused(TrelloConnector.UpdateCardOperation, new() { ["card"] = Card }));
            Assert.Empty(trello.Requests);

            var text = new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.BadRequest, "invalid id") };
            Assert.Equal("HTTP 400: invalid id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TrelloConnector(), Context(text, TrelloConnector.GetCardOperation, new() { ["card"] = Card })))).Message);
            var json = new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"invalid token\",\"error\":\"ERROR\"}") };
            Assert.Equal("HTTP 401: invalid token", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TrelloConnector(), Context(json, TrelloConnector.GetCardOperation, new() { ["card"] = Card })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_member()
        {
            var trello = new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"id\":\"m1\",\"fullName\":\"Ops Bot\",\"username\":\"opsbot\"}") };
            var tested = await new TrelloConnector().TestConnectionAsync(Contexts.Test(new StubEgress(trello), Api, credentials: Keys));
            Assert.Equal((true, "Signed in as Ops Bot."), (tested.Success, tested.Message));
            Assert.Equal($"{Api}/members/me?fields=fullName,username", trello.Requests[0].Url);
            var refused = await new TrelloConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new TrelloApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "invalid key") }), Api, credentials: Keys));
            Assert.Equal((false, "HTTP 401: invalid key"), (refused.Success, refused.Message));
            Assert.Equal("The Trello connection has no token (the secret key).", (await new TrelloConnector().TestConnectionAsync(Contexts.Test(new StubEgress(trello), Api, credentials: new Dictionary<string, string> { ["access-key"] = "key-1" }))).Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "trello", ConnectorCategory.Productivity), (new TrelloConnector().AuthType, new TrelloConnector().ConnectorType, Assert.Single(new TrelloConnector().Categories)));
            Assert.Equal(new[] { "cards", "get_card", "create_card", "update_card", "delete_card", "comment" }, new TrelloConnector().Operations.Select(operation => operation.Name));
        }
    }
}
