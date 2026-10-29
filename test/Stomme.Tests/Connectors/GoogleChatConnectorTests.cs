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
using Stomme.Modules.Connectors.Messaging;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Chat (n8n's Google Chat node) without a server: every name held to Chat's shape before a token is asked for
    /// - a send to somewhere that is not a space, or a reply to another space's thread, a refused record as the chat connectors
    /// answer; a text sent to a space, in a thread by its name or a key of the author's own, its message's name the record; Chat's
    /// refusal of a send its record, any other failure thrown; a message's JSON sent with a thread key and a request id; a message
    /// read, edited by an update mask of what is given and deleted; spaces and members page by page up to the most; Google's
    /// status and message on a failure; a test naming the spaces.
    /// </summary>
    public sealed class GoogleChatConnectorTests
    {
        private const string Api = "https://chat.googleapis.com/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Chat : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == GoogleChatConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"chat-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static IEnumerable<Sent> Calls(Chat chat) => chat.Requests.Where(sent => sent.Url != GoogleChatConnector.DefaultTokenUrl);

        private static Task<List<ConnectorRecord>> Run(Chat chat, string operation, Dictionary<string, string> parameters, string endpoint = "") =>
            Contexts.DrainAsync(new GoogleChatConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(chat), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private const string Posted = "{\"name\":\"spaces/AAAAkassa/messages/BBBB.CCCC\",\"sender\":{\"name\":\"users/42\",\"type\":\"HUMAN\"},\"text\":\"Kassan stängd\",\"thread\":{\"name\":\"spaces/AAAAkassa/threads/DDDD\"},\"space\":{\"name\":\"spaces/AAAAkassa\"},\"createTime\":\"2026-10-17T18:00:00Z\"}";

        [Fact]
        public void It_declares_itself_as_n8ns_google_chat_node_and_a_chat_connector()
        {
            IConnector connector = new GoogleChatConnector();
            Assert.Equal(("googlechat", "Google Chat", AuthenticationType.OAuth2RefreshToken, ConnectorEndpointKind.Url), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.Equal(new[] { ConnectorCategory.Messaging }, connector.Categories);
            Assert.Equal(new[] { "message", "message_create", "message_get", "message_update", "message_delete", "spaces", "space_get", "members", "member_get" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "to", "text", "reply_to" }, ((IConnectorExtractor)connector).Operations[0].Parameters!.Fields.Select(field => field.Name)); // Send chat message's own
        }

        [Fact]
        public async Task Every_name_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var chat = new Chat();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(chat, operation, parameters));
                Assert.Empty(chat.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            async Task<string?> RefusedRecord(Dictionary<string, string> parameters)
            {
                var chat = new Chat();
                var refused = Assert.Single(await Run(chat, "message", parameters)).Fields;
                Assert.Empty(chat.Requests);
                return (string?)refused["refused"];
            }

            Assert.StartsWith("Google Chat has no operation 'react' - message, message_create,", await Refused("react", new()), StringComparison.Ordinal);
            Assert.Equal("A message needs somewhere to go (to).", await Refused("message", new() { ["text"] = "Hej" }));
            Assert.Equal("A message needs a text.", await Refused("message", new() { ["to"] = "spaces/AAAA" }));
            Assert.Equal("Google Chat: 'rooms/AAAA' is not a space - spaces/AAAA..., or its id.", await RefusedRecord(new() { ["to"] = "rooms/AAAA", ["text"] = "Hej" })); // a chat connector's refusal: a record
            Assert.Equal("Google Chat: 'spaces/OTHER/threads/X' is not one of this space's threads (its name) or a thread key.", await RefusedRecord(new() { ["to"] = "AAAA", ["text"] = "Hej", ["reply_to"] = "spaces/OTHER/threads/X" }));
            Assert.Equal("Google Chat: 'kassa thread' is not one of this space's threads (its name) or a thread key.", await RefusedRecord(new() { ["to"] = "AAAA", ["text"] = "Hej", ["reply_to"] = "kassa thread" }));
            Assert.Equal("'spaces/A A' is not a space - spaces/AAAA..., or its id (space).", await Refused("message_create", new() { ["space"] = "spaces/A A", ["json"] = "{\"text\": \"Hej\"}" }));
            Assert.Equal("Give the message's JSON - {\"text\": \"...\"} (json).", await Refused("message_create", new() { ["space"] = "AAAA" }));
            Assert.Equal("The json is not a JSON object (json).", await Refused("message_create", new() { ["space"] = "AAAA", ["json"] = "Hej" }));
            Assert.Equal("'kassa 1' is not a thread key - letters, digits, _ . : - (thread_key).", await Refused("message_create", new() { ["space"] = "AAAA", ["json"] = "{\"text\": \"Hej\"}", ["thread_key"] = "kassa 1" }));
            Assert.Equal("'r/1' is not a request id - letters, digits, _ . : - (request_id).", await Refused("message_create", new() { ["space"] = "AAAA", ["json"] = "{\"text\": \"Hej\"}", ["request_id"] = "r/1" }));
            Assert.Equal("Give the message's name - spaces/AAAA.../messages/BBBB... (message).", await Refused("message_get", new()));
            Assert.Equal("'spaces/AAAA/messages/../members' is not a message's name - spaces/AAAA.../messages/BBBB... (message).", await Refused("message_delete", new() { ["message"] = "spaces/AAAA/messages/../members" }));
            Assert.Equal("Give what changes - its text, or its fields as JSON: text, cardsV2, attachment or accessoryWidgets.", await Refused("message_update", new() { ["message"] = "spaces/AAAA/messages/BBBB" }));
            Assert.Equal("Give what changes - its text, or its fields as JSON: text, cardsV2, attachment or accessoryWidgets.", await Refused("message_update", new() { ["message"] = "spaces/AAAA/messages/BBBB", ["json"] = "{\"text\": \"Hej\", \"sender\": \"users/1\"}" }));
            Assert.Equal("'spaces/AAAA/members/x/y' is not a member's name - spaces/AAAA.../members/1234... (member).", await Refused("member_get", new() { ["member"] = "spaces/AAAA/members/x/y" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("spaces", new() { ["max"] = "0" }));
        }

        [Fact]
        public async Task A_text_is_sent_to_a_space_in_a_thread_by_its_name_or_a_key_and_a_refusal_is_its_record()
        {
            var chat = new Chat { Answer = _ => (HttpStatusCode.OK, Posted) };
            var sent = Assert.Single(await Run(chat, "message", new() { ["to"] = "AAAAkassa", ["text"] = "Kassan stängd\nÅter 08:00" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "spaces/AAAAkassa/messages/BBBB.CCCC", ["to"] = "spaces/AAAAkassa" }, sent);
            var post = Calls(chat).Single();
            Assert.Equal(("POST", Api + "/spaces/AAAAkassa/messages", "Bearer chat-token", "{\"text\":\"Kassan stängd\\nÅter 08:00\"}"), (post.Method, post.Url, post.Authorization, post.Body));

            await Run(chat, "message", new() { ["to"] = "spaces/AAAAkassa", ["text"] = "Hej", ["reply_to"] = "spaces/AAAAkassa/threads/DDDD" });
            Assert.Equal((Api + "/spaces/AAAAkassa/messages?messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD", "{\"text\":\"Hej\",\"thread\":{\"name\":\"spaces/AAAAkassa/threads/DDDD\"}}"), (Calls(chat).Last().Url, Calls(chat).Last().Body));
            await Run(chat, "message", new() { ["to"] = "spaces/AAAAkassa", ["text"] = "Hej", ["reply_to"] = "order-1042" });
            Assert.Equal("{\"text\":\"Hej\",\"thread\":{\"threadKey\":\"order-1042\"}}", Calls(chat).Last().Body); // a key of the author's own

            var refusing = new Chat { Answer = _ => (HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"Space spaces/AAAAnope not found.\",\"status\":\"NOT_FOUND\"}}") };
            Assert.Equal("Google Chat: Space spaces/AAAAnope not found.", Assert.Single(await Run(refusing, "message", new() { ["to"] = "AAAAnope", ["text"] = "Hej" })).Fields["refused"]);
            var broken = new Chat { Answer = _ => (HttpStatusCode.BadGateway, "") };
            Assert.Equal("HTTP 502: not Google Chat's answer.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(broken, "message", new() { ["to"] = "AAAAkassa", ["text"] = "Hej" }))).Message);
        }

        [Fact]
        public async Task A_messages_json_is_sent_and_a_message_read_edited_and_deleted()
        {
            var chat = new Chat { Answer = sent => (HttpStatusCode.OK, sent.Method == "DELETE" ? "{}" : Posted) };
            var created = Assert.Single(await Run(chat, "message_create", new() { ["space"] = "AAAAkassa", ["json"] = "{\"text\": \"Kassan\", \"cardsV2\": [{\"cardId\": \"c1\"}]}", ["thread_key"] = "order-1042", ["request_id"] = "req.1042" })).Fields;
            Assert.Equal((Api + "/spaces/AAAAkassa/messages?threadKey=order-1042&messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD&requestId=req.1042", "{\"text\":\"Kassan\",\"cardsV2\":[{\"cardId\":\"c1\"}]}"), (Calls(chat).Last().Url, Calls(chat).Last().Body));
            Assert.Equal(("spaces/AAAAkassa/messages/BBBB.CCCC", "{\"name\":\"users/42\",\"type\":\"HUMAN\"}"), ((string?)created["name"], (string?)created["sender"]));

            await Run(chat, "message_get", new() { ["message"] = "spaces/AAAAkassa/messages/BBBB.CCCC" });
            Assert.Equal(("GET", Api + "/spaces/AAAAkassa/messages/BBBB.CCCC"), (Calls(chat).Last().Method, Calls(chat).Last().Url));
            await Run(chat, "message_update", new() { ["message"] = "spaces/AAAAkassa/messages/BBBB.CCCC", ["text"] = "Kassan öppen", ["json"] = "{\"cardsV2\": []}" });
            Assert.Equal(("PATCH", Api + "/spaces/AAAAkassa/messages/BBBB.CCCC?updateMask=cardsV2,text", "{\"cardsV2\":[],\"text\":\"Kassan öppen\"}"), (Calls(chat).Last().Method, Calls(chat).Last().Url, Calls(chat).Last().Body)); // only what is given
            await Run(chat, "message_update", new() { ["message"] = "spaces/AAAAkassa/messages/BBBB.CCCC", ["query"] = "Rad 1\nRad 2" });
            Assert.Equal((Api + "/spaces/AAAAkassa/messages/BBBB.CCCC?updateMask=text", "{\"text\":\"Rad 1\\nRad 2\"}"), (Calls(chat).Last().Url, Calls(chat).Last().Body)); // over several lines from the Query box
            Assert.Equal(new Dictionary<string, object?> { ["name"] = "spaces/AAAAkassa/messages/BBBB.CCCC", ["deleted"] = true }, Assert.Single(await Run(chat, "message_delete", new() { ["message"] = "spaces/AAAAkassa/messages/BBBB.CCCC" })).Fields);
            Assert.Equal("DELETE", Calls(chat).Last().Method);
        }

        [Fact]
        public async Task Spaces_and_members_are_listed_page_by_page_up_to_the_most()
        {
            string Page(string list, int from, int count, bool more) => "{\"" + list + "\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"name\":\"spaces/S" + i + "\",\"displayName\":\"Kassa " + i + "\"}")) + "]" + (more ? ",\"nextPageToken\":\"p" + (from + count) + "\"" : "") + "}";
            var chat = new Chat
            {
                Answer = sent => sent.Url.Contains("/members", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"memberships\":[{\"name\":\"spaces/AAAA/members/1\",\"member\":{\"name\":\"users/1\",\"type\":\"HUMAN\"},\"role\":\"ROLE_MANAGER\"}]}")
                    : sent.Url.Contains("pageToken=p100", StringComparison.Ordinal) ? (HttpStatusCode.OK, Page("spaces", 100, 20, false)) : (HttpStatusCode.OK, Page("spaces", 0, 100, true)),
            };
            Assert.Equal(120, (await Run(chat, "spaces", new())).Count);
            Assert.Equal(new[] { Api + "/spaces?pageSize=100", Api + "/spaces?pageSize=100&pageToken=p100" }, Calls(chat).Select(sent => sent.Url));
            chat.Requests.Clear();
            var five = await Run(chat, "spaces", new() { ["max"] = "5" });
            Assert.Equal(("spaces/S0", "Kassa 0"), ((string?)five[0].Fields["name"], (string?)five[0].Fields["displayName"]));
            Assert.Equal(new[] { Api + "/spaces?pageSize=5" }, Calls(chat).Select(sent => sent.Url)); // a page no larger than what is wanted

            var member = Assert.Single(await Run(chat, "members", new() { ["space"] = "AAAA" })).Fields;
            Assert.Equal(("ROLE_MANAGER", "{\"name\":\"users/1\",\"type\":\"HUMAN\"}"), ((string?)member["role"], (string?)member["member"]));
            Assert.Equal(Api + "/spaces/AAAA/members?pageSize=100", Calls(chat).Last().Url);
            await Run(chat, "member_get", new() { ["member"] = "spaces/AAAA/members/1" });
            Assert.Equal(Api + "/spaces/AAAA/members/1", Calls(chat).Last().Url);
            await Run(chat, "space_get", new() { ["space"] = "spaces/AAAA" });
            Assert.Equal(Api + "/spaces/AAAA", Calls(chat).Last().Url);
        }

        [Fact]
        public async Task A_failure_is_said_in_googles_words_and_a_test_names_the_spaces()
        {
            async Task<string> Failed(HttpStatusCode status, string body) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Chat { Answer = _ => (status, body) }, "spaces", new()))).Message;

            Assert.Equal("Google Chat answered HTTP 403 PERMISSION_DENIED: Request had insufficient authentication scopes. - check the refresh token and its scopes.", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"Request had insufficient authentication scopes.\",\"status\":\"PERMISSION_DENIED\"}}"));
            Assert.Equal("Google Chat answered HTTP 502.", await Failed(HttpStatusCode.BadGateway, ""));
            Assert.Equal("Google Chat answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));

            var chat = new Chat { Answer = _ => (HttpStatusCode.OK, "{\"spaces\":[{\"name\":\"spaces/A\",\"displayName\":\"Kassa\"},{\"name\":\"spaces/B\"}]}") };
            var tested = await new GoogleChatConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(chat), Api, new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Google Chat: in Kassa, spaces/B."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/spaces?pageSize=10", Calls(chat).Single().Url); // its /v1 address taken as its own
            var elsewhere = await new GoogleChatConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(chat), "https://chat.googleapis.com/v2/x", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "'https://chat.googleapis.com/v2/x' is not Google Chat's address - its own, https://chat.googleapis.com."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
