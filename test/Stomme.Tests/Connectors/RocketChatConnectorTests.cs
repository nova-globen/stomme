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
using Stomme.Modules.Connectors.RocketChat;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Rocket.Chat (n8n's Rocket.Chat node: a message posted, the subscriptions, a direct conversation's messages):
    /// every call a server's API v1 with the user's id and token; a message posted to a channel, a user or a room with its
    /// alias, emoji, avatar and attachments, updated and deleted; a channel's, a private group's or a direct conversation's
    /// messages newest first page by page by Rocket.Chat's total; the public channels or those joined, the users, the
    /// subscriptions, a room marked read; what could go wrong refused before anything is sent; Rocket.Chat's word on a
    /// failure, an answer it does not say succeeded one too; a test naming whose the token is.
    /// </summary>
    public sealed class RocketChatConnectorTests
    {
        private const string Site = "https://chat.example.test";

        private const string Api = Site + "/api/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "aobEdbYhXfu5hkeqG", ["password"] = "rc-token-1" };

        private sealed class RocketChat : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? User, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"success\":true}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("X-User-Id", out var user) ? user.Single() : null, request.Headers.TryGetValues("X-Auth-Token", out var token) ? token.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(RocketChat rocketChat, string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new RocketChatConnector(), Contexts.Extract(new StubEgress(rocketChat), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Messages(int first, int count, long total) =>
            "{\"messages\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"_id\":\"m" + i + "\",\"rid\":\"GENERAL\",\"msg\":\"Fika " + i + "\",\"u\":{\"username\":\"ada\"},\"groupable\":false}")) + "],\"offset\":0,\"count\":" + count + ",\"total\":" + total + ",\"success\":true}";

        [Fact]
        public async Task Messages_are_posted_updated_deleted_and_read_page_by_page()
        {
            var rocketChat = new RocketChat
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("POST", var u) when u.EndsWith("/chat.postMessage", StringComparison.Ordinal) => "{\"ts\":1700000000000,\"channel\":\"general\",\"message\":{\"_id\":\"m42\",\"rid\":\"GENERAL\",\"msg\":\"Kaffe på café\",\"alias\":\"Kafé\"},\"success\":true}",
                    ("POST", var u) when u.EndsWith("/chat.update", StringComparison.Ordinal) => "{\"message\":{\"_id\":\"m42\",\"msg\":\"Kaffe!\",\"editedAt\":\"2026-10-13T10:00:00.000Z\"},\"success\":true}",
                    ("GET", var u) when u.Contains("offset=200", StringComparison.Ordinal) => Messages(201, 50, 250),
                    ("GET", var u) when u.Contains("offset=100", StringComparison.Ordinal) => Messages(101, 100, 250),
                    ("GET", _) => Messages(1, 100, 250),
                    _ => "{\"success\":true}",
                }),
            };
            (string, string, string?) Last() => (rocketChat.Requests[^1].Method.Method, rocketChat.Requests[^1].Url, rocketChat.Requests[^1].Body);

            var posted = Assert.Single(await Run(rocketChat, RocketChatConnector.PostMessageOperation, new()
            {
                ["channel"] = "#general", ["text"] = "Kaffe på café\nKl. 15", ["alias"] = "Kafé", ["emoji"] = ":coffee:", ["avatar"] = "https://kafe.example.test/a.png",
                ["attachments"] = "[{\"title\": \"Fika\", \"text\": \"At three\", \"color\": \"#ff0000\", \"fields\": [{\"short\": true, \"title\": \"Bun\", \"value\": \"Kanel\"}]}]",
            })).Fields;
            Assert.Equal(("POST", $"{Api}/chat.postMessage", (string?)"{\"channel\":\"#general\",\"text\":\"Kaffe på café\\nKl. 15\",\"alias\":\"Kafé\",\"emoji\":\":coffee:\",\"avatar\":\"https://kafe.example.test/a.png\",\"attachments\":[{\"title\":\"Fika\",\"text\":\"At three\",\"color\":\"#ff0000\",\"fields\":[{\"short\":true,\"title\":\"Bun\",\"value\":\"Kanel\"}]}]}"), Last());
            Assert.Equal(("aobEdbYhXfu5hkeqG", "rc-token-1"), (rocketChat.Requests[^1].User, rocketChat.Requests[^1].Token)); // the user's id and token
            Assert.Equal(("m42", "Kaffe på café", "general"), ((string?)posted["_id"], (string?)posted["msg"], (string?)posted["channel"]));
            await Run(rocketChat, RocketChatConnector.PostMessageOperation, new() { ["channel"] = "@ada", ["text"] = "Hej" }, endpoint: Api + "/");
            Assert.Equal(("POST", $"{Api}/chat.postMessage", (string?)"{\"channel\":\"@ada\",\"text\":\"Hej\"}"), Last()); // /api/v1 given once
            await Run(rocketChat, RocketChatConnector.PostMessageOperation, new() { ["channel"] = "GENERAL", ["attachments"] = "[{\"text\": \"Fika\"}]" });
            Assert.Equal("{\"channel\":\"GENERAL\",\"attachments\":[{\"text\":\"Fika\"}]}", Last().Item3); // a room's id; attachments alone
            await Run(rocketChat, null!, new() { ["channel"] = "#fika", ["text"] = "Hej" });
            Assert.Equal(("POST", $"{Api}/chat.postMessage", (string?)"{\"channel\":\"#fika\",\"text\":\"Hej\"}"), Last()); // no operation: a message posted

            var updated = Assert.Single(await Run(rocketChat, RocketChatConnector.UpdateMessageOperation, new() { ["room"] = "GENERAL", ["message"] = "m42", ["text"] = "Kaffe!" })).Fields;
            Assert.Equal(("POST", $"{Api}/chat.update", (string?)"{\"roomId\":\"GENERAL\",\"msgId\":\"m42\",\"text\":\"Kaffe!\"}"), Last());
            Assert.Equal(("m42", "Kaffe!"), ((string?)updated["_id"], (string?)updated["msg"]));
            var deleted = Assert.Single(await Run(rocketChat, RocketChatConnector.DeleteMessageOperation, new() { ["room"] = "GENERAL", ["message"] = "m42" })).Fields;
            Assert.Equal(("POST", $"{Api}/chat.delete", (string?)"{\"roomId\":\"GENERAL\",\"msgId\":\"m42\"}"), Last());
            Assert.Equal(("m42", "GENERAL", true), ((string?)deleted["_id"], (string?)deleted["rid"], (bool?)deleted["deleted"]));

            rocketChat.Requests.Clear();
            var read = await Run(rocketChat, RocketChatConnector.MessagesOperation, new() { ["room"] = "GENERAL", ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            const string Newest = "sort=%7B%22ts%22%3A-1%7D";
            Assert.Equal(new[] { $"{Api}/channels.messages?roomId=GENERAL&{Newest}&offset=0&count=100", $"{Api}/channels.messages?roomId=GENERAL&{Newest}&offset=100&count=100" }, rocketChat.Requests.Select(request => request.Url));
            Assert.Equal(("m1", "Fika 1", "{\"username\":\"ada\"}", false), ((string?)read[0].Fields["_id"], (string?)read[0].Fields["msg"], (string?)read[0].Fields["u"], (bool?)read[0].Fields["groupable"]));
            rocketChat.Requests.Clear();
            Assert.Equal(250, (await Run(rocketChat, RocketChatConnector.MessagesOperation, new() { ["room"] = "GENERAL" })).Count); // every one, Rocket.Chat's total the end
            Assert.Equal(3, rocketChat.Requests.Count);
            rocketChat.Requests.Clear();
            rocketChat.Answer = (_, url) => (HttpStatusCode.OK, url.Contains("offset=60", StringComparison.Ordinal) ? Messages(61, 10, 70) : url.Contains("offset=30", StringComparison.Ordinal) ? Messages(31, 30, 70) : Messages(1, 30, 70));
            Assert.Equal(70, (await Run(rocketChat, RocketChatConnector.MessagesOperation, new() { ["room"] = "aobEdbYhXfu5hkeqGrocketcat", ["kind"] = "DM" })).Count); // a server capping its pages read to its total
            Assert.Equal(new[] { 0, 30, 60 }.Select(offset => $"{Api}/dm.messages?roomId=aobEdbYhXfu5hkeqGrocketcat&{Newest}&offset={offset}&count=100"), rocketChat.Requests.Select(request => request.Url));
            rocketChat.Requests.Clear();
            rocketChat.Answer = (_, _) => (HttpStatusCode.OK, "{\"messages\":[],\"total\":5,\"success\":true}");
            Assert.Empty(await Run(rocketChat, RocketChatConnector.MessagesOperation, new() { ["room"] = "kafe", ["kind"] = "group" }));
            Assert.Equal($"{Api}/groups.messages?roomId=kafe&{Newest}&offset=0&count=100", Assert.Single(rocketChat.Requests).Url); // an empty page the end
        }

        [Fact]
        public async Task Channels_users_and_subscriptions_are_read_and_a_room_marked_read()
        {
            var rocketChat = new RocketChat
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/channels.list", StringComparison.Ordinal) => "{\"channels\":[{\"_id\":\"GENERAL\",\"name\":\"general\",\"usersCount\":12}],\"offset\":0,\"count\":1,\"total\":1,\"success\":true}",
                    ("GET", var u) when u.Contains("/users.list", StringComparison.Ordinal) => "{\"users\":[{\"_id\":\"u1\",\"username\":\"ada\",\"active\":true,\"roles\":[\"user\"]},{\"_id\":\"u2\",\"username\":\"bo\",\"active\":false}],\"total\":2,\"success\":true}",
                    ("GET", _) => "{\"update\":[{\"rid\":\"GENERAL\",\"name\":\"general\",\"unread\":3,\"t\":\"c\"}],\"remove\":[{\"rid\":\"OLD\"}],\"success\":true}",
                    _ => "{\"success\":true}",
                }),
            };
            (string, string, string?) Last() => (rocketChat.Requests[^1].Method.Method, rocketChat.Requests[^1].Url, rocketChat.Requests[^1].Body);

            var channel = Assert.Single(await Run(rocketChat, RocketChatConnector.ChannelsOperation, new())).Fields;
            Assert.Equal($"{Api}/channels.list?offset=0&count=100", Last().Item2); // the total the end
            Assert.Equal(("GENERAL", 12L), ((string?)channel["_id"], (long?)channel["usersCount"]));
            await Run(rocketChat, RocketChatConnector.ChannelsOperation, new() { ["joined"] = "TRUE", ["max"] = "10" });
            Assert.Equal($"{Api}/channels.list.joined?offset=0&count=10", Last().Item2);
            var users = await Run(rocketChat, RocketChatConnector.UsersOperation, new());
            Assert.Equal($"{Api}/users.list?offset=0&count=100", Last().Item2);
            Assert.Equal(new[] { ("ada", true), ("bo", false) }, users.Select(user => ((string)user.Fields["username"]!, (bool)user.Fields["active"]!)));
            var subscribed = Assert.Single(await Run(rocketChat, RocketChatConnector.SubscriptionsOperation, new())).Fields; // what is to be removed not among them
            Assert.Equal(("GET", $"{Api}/subscriptions.get", (string?)null), Last());
            Assert.Equal(("GENERAL", 3L), ((string?)subscribed["rid"], (long?)subscribed["unread"]));
            var marked = Assert.Single(await Run(rocketChat, RocketChatConnector.MarkReadOperation, new() { ["room"] = "GENERAL" })).Fields;
            Assert.Equal(("POST", $"{Api}/subscriptions.read", (string?)"{\"rid\":\"GENERAL\"}"), Last());
            Assert.Equal(("GENERAL", true), ((string?)marked["rid"], (bool?)marked["read"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_rocket_chats_word_on_a_failure()
        {
            var rocketChat = new RocketChat();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(rocketChat, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Rocket.Chat has no operation 'react' - post_message, update_message,", await Refused("react", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(RocketChatConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the room's id.", await Refused(RocketChatConnector.MarkReadOperation, new()));
            Assert.Equal("'GEN ERAL' is not an id (room).", await Refused(RocketChatConnector.MessagesOperation, new() { ["room"] = "GEN ERAL" }));
            Assert.Equal("Give the message's id.", await Refused(RocketChatConnector.DeleteMessageOperation, new() { ["room"] = "GENERAL" }));
            Assert.Equal("'m/42' is not an id (message).", await Refused(RocketChatConnector.UpdateMessageOperation, new() { ["room"] = "GENERAL", ["message"] = "m/42", ["text"] = "x" }));
            Assert.Equal("Give its new text.", await Refused(RocketChatConnector.UpdateMessageOperation, new() { ["room"] = "GENERAL", ["message"] = "m42" }));
            Assert.Equal("'thread' is not channel, group or dm (kind).", await Refused(RocketChatConnector.MessagesOperation, new() { ["room"] = "GENERAL", ["kind"] = "thread" }));
            Assert.Equal("'yes' is not true or false (joined).", await Refused(RocketChatConnector.ChannelsOperation, new() { ["joined"] = "yes" }));
            Assert.Equal("Give the channel - #channel, @user or a room's id.", await Refused(RocketChatConnector.PostMessageOperation, new() { ["text"] = "x" }));
            Assert.Equal("'#fika bröd' is not #channel, @user or a room's id (channel).", await Refused(RocketChatConnector.PostMessageOperation, new() { ["channel"] = "#fika bröd", ["text"] = "x" }));
            Assert.Equal("'#' is not #channel, @user or a room's id (channel).", await Refused(RocketChatConnector.PostMessageOperation, new() { ["channel"] = "#", ["text"] = "x" }));
            Assert.Equal("Give the text or attachments.", await Refused(RocketChatConnector.PostMessageOperation, new() { ["channel"] = "#general", ["alias"] = "Kafé" }));
            Assert.Equal("The attachments are not a JSON list of objects - [{\"title\": \"Fika\", \"text\": \"At three\"}].", await Refused(RocketChatConnector.PostMessageOperation, new() { ["channel"] = "#general", ["attachments"] = "[\"Fika\"]" }));
            Assert.Equal("Give the server's address - https://chat.example.com.", await Refused(RocketChatConnector.UsersOperation, new(), endpoint: ""));
            Assert.Equal("The Rocket.Chat connection has no user id.", await Refused(RocketChatConnector.UsersOperation, new(), credentials: new() { ["password"] = "t" }));
            Assert.Equal("The Rocket.Chat connection has no access token.", await Refused(RocketChatConnector.UsersOperation, new(), credentials: new() { ["username"] = "u" }));
            Assert.Empty(rocketChat.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RocketChat { Answer = (_, _) => (status, answer) }, RocketChatConnector.PostMessageOperation, new() { ["channel"] = "#general", ["text"] = "x" }))).Message;
            Assert.Equal("HTTP 400: error-room-not-found", await Failed(HttpStatusCode.BadRequest, "{\"success\":false,\"error\":\"error-room-not-found\",\"errorType\":\"error-room-not-found\"}"));
            Assert.Equal("HTTP 401: You must be logged in to do this.", await Failed(HttpStatusCode.Unauthorized, "{\"status\":\"error\",\"message\":\"You must be logged in to do this.\"}"));
            Assert.Equal("HTTP 200: The room is read-only.", await Failed(HttpStatusCode.OK, "{\"success\":false,\"error\":\"The room is read-only.\"}")); // a failure answered with 200
            Assert.Equal("HTTP 200: Rocket.Chat did not say it succeeded.", await Failed(HttpStatusCode.OK, "{\"message\":{}}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"success\":false,\"error\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Rocket.Chat answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Login</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(RocketChat rocketChat) =>
                new RocketChatConnector().TestConnectionAsync(Contexts.Test(new StubEgress(rocketChat), Site, null, Credentials));
            var me = new RocketChat { Answer = (_, _) => (HttpStatusCode.OK, "{\"_id\":\"aobEdbYhXfu5hkeqG\",\"name\":\"Kafé bot\",\"username\":\"kafe-bot\",\"success\":true}") };
            var tested = await Test(me);
            Assert.Equal((true, "Rocket.Chat: the token is Kafé bot's (@kafe-bot)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/me", "GET", "aobEdbYhXfu5hkeqG", "rc-token-1"), (me.Requests[0].Url, me.Requests[0].Method.Method, me.Requests[0].User, me.Requests[0].Token));
            Assert.Equal("Rocket.Chat: the token is a user's.", (await Test(new RocketChat())).Message);
            var refused = await Test(new RocketChat { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"status\":\"error\",\"message\":\"You must be logged in to do this.\"}") });
            Assert.Equal((false, "HTTP 401: You must be logged in to do this."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "rocketchat"), (new RocketChatConnector().AuthType, new RocketChatConnector().ConnectorType));
            Assert.Equal(8, new RocketChatConnector().Operations.Count);
            Assert.Equal(new[] { ("password", "Personal access token"), ("username", "User ID") }, new RocketChatConnector().CredentialLabels!.OrderBy(label => label.Key, StringComparer.Ordinal).Select(label => (label.Key, label.Value))); // in Rocket.Chat's words
        }
    }
}
