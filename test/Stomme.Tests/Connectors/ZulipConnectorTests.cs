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
using Stomme.Modules.Connectors.Zulip;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Zulip (n8n's Zulip node: messages sent, got, updated and deleted, streams and users kept): every call an
    /// organization's API v1 with the e-mail and the key, a body form-encoded; a message sent to a stream's topic or to people;
    /// a message got with its Markdown, a message's subject its topic too; messages searched newest first, page by page by
    /// Zulip's anchor, the size fixed and no bigger than asked, Zulip's found oldest or a short page the end; a message updated
    /// and deleted; the streams, the subscribed ones, a stream created, updated and deleted; the users, a user by id or e-mail,
    /// a user created, updated and deactivated; what could go wrong refused before anything is sent; Zulip's code and word on
    /// a failure, its error answered with HTTP 200 too; a test naming whose the key is.
    /// </summary>
    public sealed class ZulipConnectorTests
    {
        private const string Site = "https://chat.example.test";

        private const string Api = Site + "/api/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe-bot@chat.example.test", ["password"] = "zulip-key-1" };

        private sealed class Zulip : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"result\":\"success\",\"msg\":\"\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Zulip zulip, string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new ZulipConnector(), Contexts.Extract(new StubEgress(zulip), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Messages(int newest, int count, bool oldest) =>
            "{\"result\":\"success\",\"found_oldest\":" + (oldest ? "true" : "false") + ",\"messages\":[" + string.Join(",", Enumerable.Range(newest - count + 1, count).Select(i => "{\"id\":" + i + ",\"sender_email\":\"ada@chat.example.test\",\"subject\":\"Fika\",\"content\":\"Kaffe " + i + " på café\",\"flags\":[\"read\"]}")) + "]}";

        [Fact]
        public async Task Messages_are_sent_got_searched_page_by_page_updated_and_deleted()
        {
            var zulip = new Zulip
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("POST", _) => "{\"result\":\"success\",\"msg\":\"\",\"id\":42}",
                    ("GET", var u) when u.Contains("/messages/42", StringComparison.Ordinal) => "{\"result\":\"success\",\"message\":{\"id\":42,\"subject\":\"Fika\",\"content\":\"Kaffe\",\"is_me_message\":false,\"reactions\":[]},\"raw_content\":\"**Kaffe**\"}",
                    ("GET", var u) when u.Contains("anchor=newest", StringComparison.Ordinal) => Messages(500, 100, false),
                    ("GET", _) => Messages(400, 50, true),
                    _ => "{\"result\":\"success\",\"msg\":\"\"}",
                }),
            };
            (string, string, string?) Last() => (zulip.Requests[^1].Method.Method, zulip.Requests[^1].Url, zulip.Requests[^1].Body);

            var sent = Assert.Single(await Run(zulip, ZulipConnector.SendStreamOperation, new() { ["stream"] = "kafé", ["topic"] = "Fika & bröd", ["content"] = "Kaffe på café\nKl. 15" })).Fields;
            Assert.Equal(("POST", $"{Api}/messages", (string?)"type=stream&to=kaf%C3%A9&topic=Fika+%26+br%C3%B6d&content=Kaffe+p%C3%A5+caf%C3%A9%0AKl.+15"), Last()); // form-encoded, its line break kept
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("kafe-bot@chat.example.test:zulip-key-1")), zulip.Requests[^1].Authorization);
            Assert.Equal((42L, true), ((long?)sent["id"], (bool?)sent["sent"]));
            await Run(zulip, ZulipConnector.SendDirectOperation, new() { ["to"] = "ada@chat.example.test, 9", ["content"] = "Hej" }, endpoint: Site + "/api/v1/");
            Assert.Equal(("POST", $"{Api}/messages", (string?)"type=direct&to=%5B%22ada%40chat.example.test%22%2C9%5D&content=Hej"), Last()); // its API's address the organization's; people as Zulip's list

            var message = Assert.Single(await Run(zulip, ZulipConnector.MessageOperation, new() { ["id"] = "42" })).Fields;
            Assert.Equal(("GET", $"{Api}/messages/42", (string?)null), Last());
            Assert.Equal((42L, "Fika", "Fika", "**Kaffe**", false, "[]"), ((long?)message["id"], (string?)message["subject"], (string?)message["topic"], (string?)message["raw_content"], (bool?)message["is_me_message"], (string?)message["reactions"])); // its subject its topic too

            zulip.Requests.Clear();
            var found = await Run(zulip, ZulipConnector.MessagesOperation, new() { ["stream"] = "kafé", ["topic"] = "Fika", ["sender"] = "ada@chat.example.test", ["search"] = "bröd" });
            Assert.Equal(150, found.Count);
            const string Narrow = "narrow=%5B%7B%22operator%22%3A%22stream%22%2C%22operand%22%3A%22kaf%C3%A9%22%7D%2C%7B%22operator%22%3A%22topic%22%2C%22operand%22%3A%22Fika%22%7D%2C%7B%22operator%22%3A%22sender%22%2C%22operand%22%3A%22ada%40chat.example.test%22%7D%2C%7B%22operator%22%3A%22search%22%2C%22operand%22%3A%22br%C3%B6d%22%7D%5D";
            Assert.Equal(new[] { $"{Api}/messages?anchor=newest&include_anchor=true&num_before=100&num_after=0&apply_markdown=false&{Narrow}", $"{Api}/messages?anchor=401&include_anchor=false&num_before=100&num_after=0&apply_markdown=false&{Narrow}" }, zulip.Requests.Select(request => request.Url)); // before the oldest read; Zulip's found oldest the end
            Assert.Equal((500L, 401L, 400L), ((long?)found[0].Fields["id"], (long?)found[99].Fields["id"], (long?)found[100].Fields["id"])); // newest first
            Assert.Equal(("Kaffe 500 på café", "[\"read\"]"), ((string?)found[0].Fields["content"], (string?)found[0].Fields["flags"]));
            zulip.Requests.Clear();
            Assert.Equal(30, (await Run(zulip, ZulipConnector.MessagesOperation, new() { ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/messages?anchor=newest&include_anchor=true&num_before=30&num_after=0&apply_markdown=false&narrow=%5B%5D", Assert.Single(zulip.Requests).Url); // every message; a page as small as asked
            zulip.Answer = (_, _) => (HttpStatusCode.OK, Messages(10, 10, false));
            Assert.Equal(10, (await Run(zulip, ZulipConnector.MessagesOperation, new())).Count); // a short page the end
            zulip.Requests.Clear();
            zulip.Answer = (_, _) => (HttpStatusCode.OK, Messages(100, 100, true));
            Assert.Equal(100, (await Run(zulip, ZulipConnector.MessagesOperation, new())).Count);
            Assert.Single(zulip.Requests); // Zulip's found oldest the end, though the page was full

            zulip.Answer = (_, _) => (HttpStatusCode.OK, "{\"result\":\"success\",\"msg\":\"\"}");
            var updated = Assert.Single(await Run(zulip, ZulipConnector.UpdateMessageOperation, new() { ["id"] = "42", ["content"] = "Kaffe!", ["topic"] = "Fika 2", ["propagate_mode"] = "Change_All" })).Fields;
            Assert.Equal((42L, true), ((long?)updated["id"], (bool?)updated["updated"]));
            Assert.Equal(("PATCH", $"{Api}/messages/42", (string?)"content=Kaffe%21&topic=Fika+2&propagate_mode=change_all"), Last());
            Assert.True((bool?)Assert.Single(await Run(zulip, ZulipConnector.DeleteMessageOperation, new() { ["id"] = "42", ["content"] = "x" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/messages/42", (string?)null), Last());
        }

        [Fact]
        public async Task Streams_and_users_are_read_created_updated_and_deleted()
        {
            var zulip = new Zulip
            {
                Answer = (method, url) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    ("GET", var u) when u.EndsWith("/streams", StringComparison.Ordinal) => "{\"result\":\"success\",\"streams\":[{\"stream_id\":7,\"name\":\"kafé\",\"invite_only\":false},{\"stream_id\":8,\"name\":\"bageri\",\"invite_only\":true}]}",
                    ("GET", var u) when u.Contains("/subscriptions", StringComparison.Ordinal) => "{\"result\":\"success\",\"subscriptions\":[{\"stream_id\":7,\"name\":\"kafé\",\"subscribers\":[9,10]}]}",
                    ("GET", var u) when u.EndsWith("/users", StringComparison.Ordinal) => "{\"result\":\"success\",\"members\":[{\"user_id\":9,\"email\":\"ada@chat.example.test\",\"full_name\":\"Ada\",\"is_bot\":false},{\"user_id\":10,\"full_name\":\"Bo\"}]}",
                    ("GET", _) => "{\"result\":\"success\",\"user\":{\"user_id\":9,\"full_name\":\"Ada\",\"role\":400}}",
                    ("POST", var u) when u.EndsWith("/users", StringComparison.Ordinal) => "{\"result\":\"success\",\"msg\":\"\",\"user_id\":11}",
                    ("POST", _) => "{\"result\":\"success\",\"msg\":\"\",\"subscribed\":{\"9\":[\"kafé\"]},\"already_subscribed\":{}}",
                    _ => "{\"result\":\"success\",\"msg\":\"\"}",
                }),
            };
            (string, string, string?) Last() => (zulip.Requests[^1].Method.Method, zulip.Requests[^1].Url, zulip.Requests[^1].Body);

            var streams = await Run(zulip, ZulipConnector.StreamsOperation, new());
            Assert.Equal(("GET", $"{Api}/streams", (string?)null), Last());
            Assert.Equal(new[] { (7L, "kafé", false), (8L, "bageri", true) }, streams.Select(stream => ((long)stream.Fields["stream_id"]!, (string)stream.Fields["name"]!, (bool)stream.Fields["invite_only"]!)));
            Assert.Single(await Run(zulip, ZulipConnector.StreamsOperation, new() { ["max"] = "1" }));
            Assert.Equal("[9,10]", (string?)Assert.Single(await Run(zulip, ZulipConnector.SubscriptionsOperation, new() { ["subscribers"] = "True" })).Fields["subscribers"]);
            Assert.Equal(("GET", $"{Api}/users/me/subscriptions?include_subscribers=true", (string?)null), Last());
            await Run(zulip, ZulipConnector.SubscriptionsOperation, new());
            Assert.Equal($"{Api}/users/me/subscriptions", Last().Item2);

            var created = Assert.Single(await Run(zulip, ZulipConnector.CreateStreamOperation, new() { ["name"] = "kafé", ["description"] = "Fika & mer", ["private"] = "true", ["principals"] = "ada@chat.example.test, 10", ["announce"] = "false" })).Fields;
            Assert.Equal(("POST", $"{Api}/users/me/subscriptions", (string?)"subscriptions=%5B%7B%22name%22%3A%22kaf%C3%A9%22%2C%22description%22%3A%22Fika+%26+mer%22%7D%5D&invite_only=true&principals=%5B%22ada%40chat.example.test%22%2C10%5D&announce=false"), Last());
            Assert.Equal(("kafé", "{\"9\":[\"kafé\"]}", "{}"), ((string?)created["name"], (string?)created["subscribed"], (string?)created["already_subscribed"]));
            await Run(zulip, ZulipConnector.CreateStreamOperation, new() { ["name"] = "bageri" });
            Assert.Equal(("POST", $"{Api}/users/me/subscriptions", (string?)"subscriptions=%5B%7B%22name%22%3A%22bageri%22%7D%5D"), Last());
            Assert.True((bool?)Assert.Single(await Run(zulip, ZulipConnector.UpdateStreamOperation, new() { ["id"] = "7", ["new_name"] = "kafét", ["description"] = "Fika", ["private"] = "FALSE" })).Fields["updated"]);
            Assert.Equal(("PATCH", $"{Api}/streams/7", (string?)"new_name=kaf%C3%A9t&description=Fika&is_private=false"), Last());
            Assert.True((bool?)Assert.Single(await Run(zulip, ZulipConnector.DeleteStreamOperation, new() { ["id"] = "7" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/streams/7", (string?)null), Last());

            var users = await Run(zulip, ZulipConnector.UsersOperation, new());
            Assert.Equal(("GET", $"{Api}/users", (string?)null), Last());
            Assert.Equal(new[] { "Ada", "Bo" }, users.Select(user => (string)user.Fields["full_name"]!));
            Assert.Equal(400L, (long?)Assert.Single(await Run(zulip, ZulipConnector.UserOperation, new() { ["id"] = "9" })).Fields["role"]);
            Assert.Equal($"{Api}/users/9", Last().Item2);
            await Run(zulip, ZulipConnector.UserOperation, new() { ["id"] = "ada@chat.example.test" });
            Assert.Equal($"{Api}/users/ada%40chat.example.test", Last().Item2); // by e-mail, escaped
            var made = Assert.Single(await Run(zulip, ZulipConnector.CreateUserOperation, new() { ["email"] = "cy@chat.example.test", ["full_name"] = "Cy Åberg", ["password"] = "Fika-1234" })).Fields;
            Assert.Equal(("POST", $"{Api}/users", (string?)"email=cy%40chat.example.test&full_name=Cy+%C3%85berg&password=Fika-1234"), Last());
            Assert.Equal((11L, "cy@chat.example.test"), ((long?)made["user_id"], (string?)made["email"]));
            await Run(zulip, ZulipConnector.UpdateUserOperation, new() { ["id"] = "11", ["full_name"] = "Cy", ["role"] = "Moderator" });
            Assert.Equal(("PATCH", $"{Api}/users/11", (string?)"full_name=Cy&role=300"), Last());
            Assert.True((bool?)Assert.Single(await Run(zulip, ZulipConnector.DeactivateUserOperation, new() { ["id"] = "11" })).Fields["deactivated"]);
            Assert.Equal(("DELETE", $"{Api}/users/11", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_zulips_word_on_a_failure()
        {
            var zulip = new Zulip();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(zulip, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Zulip has no operation 'react' - send_stream, send_direct, message,", await Refused("react", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(ZulipConnector.MessagesOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the id.", await Refused(ZulipConnector.MessageOperation, new()));
            Assert.Equal("'4x' is not an id.", await Refused(ZulipConnector.DeleteStreamOperation, new() { ["id"] = "4x" }));
            Assert.Equal("'ada@chat.example.test' is not an id.", await Refused(ZulipConnector.DeactivateUserOperation, new() { ["id"] = "ada@chat.example.test" })); // an e-mail only to get one
            Assert.Equal("'ada' is not an id or an e-mail.", await Refused(ZulipConnector.UserOperation, new() { ["id"] = "ada" }));
            Assert.Equal("Give the stream - its name or id.", await Refused(ZulipConnector.SendStreamOperation, new() { ["topic"] = "x", ["content"] = "x" }));
            Assert.Equal("Give the topic.", await Refused(ZulipConnector.SendStreamOperation, new() { ["stream"] = "x", ["content"] = "x" }));
            Assert.Equal("Give the message.", await Refused(ZulipConnector.SendDirectOperation, new() { ["to"] = "9" }));
            Assert.Equal("Give who it is to - e-mails or user ids.", await Refused(ZulipConnector.SendDirectOperation, new() { ["content"] = "x" }));
            Assert.Equal("'ada' is not an e-mail or a user id (to).", await Refused(ZulipConnector.SendDirectOperation, new() { ["to"] = "9, ada", ["content"] = "x" }));
            Assert.Equal("'change_some' is not change_one, change_later or change_all (propagate_mode).", await Refused(ZulipConnector.UpdateMessageOperation, new() { ["id"] = "42", ["topic"] = "x", ["propagate_mode"] = "change_some" }));
            Assert.Equal("Give what to change - the message or its topic.", await Refused(ZulipConnector.UpdateMessageOperation, new() { ["id"] = "42" }));
            Assert.Equal("'yes' is not true or false (subscribers).", await Refused(ZulipConnector.SubscriptionsOperation, new() { ["subscribers"] = "yes" }));
            Assert.Equal("Give the stream's name.", await Refused(ZulipConnector.CreateStreamOperation, new()));
            Assert.Equal("'bo' is not an e-mail or a user id (principals).", await Refused(ZulipConnector.CreateStreamOperation, new() { ["name"] = "x", ["principals"] = "bo" }));
            Assert.Equal("'yes' is not true or false (private).", await Refused(ZulipConnector.UpdateStreamOperation, new() { ["id"] = "7", ["private"] = "yes" }));
            Assert.Equal("Give what to change - its name, description or privacy.", await Refused(ZulipConnector.UpdateStreamOperation, new() { ["id"] = "7" }));
            Assert.Equal("'cy' is not an e-mail (email).", await Refused(ZulipConnector.CreateUserOperation, new() { ["email"] = "cy", ["full_name"] = "Cy", ["password"] = "x" }));
            Assert.Equal("Give their first password.", await Refused(ZulipConnector.CreateUserOperation, new() { ["email"] = "cy@chat.example.test", ["full_name"] = "Cy" }));
            Assert.Equal("'boss' is not owner, administrator, moderator, member or guest (role).", await Refused(ZulipConnector.UpdateUserOperation, new() { ["id"] = "11", ["role"] = "boss" }));
            Assert.Equal("Give what to change - their name or role.", await Refused(ZulipConnector.UpdateUserOperation, new() { ["id"] = "11" }));
            Assert.Equal("Give the organization's address - https://chat.example.com.", await Refused(ZulipConnector.UsersOperation, new(), endpoint: ""));
            Assert.Equal("The Zulip connection has no e-mail - the bot's or the user's.", await Refused(ZulipConnector.UsersOperation, new(), credentials: new() { ["password"] = "k" }));
            Assert.Equal("The Zulip connection has no API key.", await Refused(ZulipConnector.UsersOperation, new(), credentials: new() { ["username"] = "kafe-bot@chat.example.test" }));
            Assert.Empty(zulip.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Zulip { Answer = (_, _) => (status, answer) }, ZulipConnector.SendStreamOperation, new() { ["stream"] = "x", ["topic"] = "t", ["content"] = "c" }))).Message;
            Assert.Equal("HTTP 400 (STREAM_DOES_NOT_EXIST): Stream 'x' does not exist", await Failed(HttpStatusCode.BadRequest, "{\"result\":\"error\",\"msg\":\"Stream 'x' does not exist\",\"code\":\"STREAM_DOES_NOT_EXIST\",\"stream\":\"x\"}"));
            Assert.Equal("HTTP 200 (BAD_REQUEST): Missing 'content' argument", await Failed(HttpStatusCode.OK, "{\"result\":\"error\",\"msg\":\"Missing 'content' argument\",\"code\":\"BAD_REQUEST\"}")); // an error answered with 200
            Assert.Equal("HTTP 401: Invalid API key", await Failed(HttpStatusCode.Unauthorized, "{\"result\":\"error\",\"msg\":\"Invalid API key\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"result\":\"error\",\"msg\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Zulip answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_key_is()
        {
            Task<ConnectionTestResult> Test(Zulip zulip) =>
                new ZulipConnector().TestConnectionAsync(Contexts.Test(new StubEgress(zulip), Site, null, Credentials));
            var bot = new Zulip { Answer = (_, _) => (HttpStatusCode.OK, "{\"result\":\"success\",\"full_name\":\"Kafé bot\",\"email\":\"kafe-bot@chat.example.test\",\"user_id\":12}") };
            var tested = await Test(bot);
            Assert.Equal((true, "Zulip: the key is Kafé bot's (kafe-bot@chat.example.test)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/users/me", "GET"), (bot.Requests[0].Url, bot.Requests[0].Method.Method));
            Assert.Equal("Zulip: the key is a user's.", (await Test(new Zulip())).Message);
            var refused = await Test(new Zulip { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"result\":\"error\",\"msg\":\"Invalid API key\",\"code\":\"UNAUTHORIZED\"}") });
            Assert.Equal((false, "HTTP 401 (UNAUTHORIZED): Invalid API key"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "zulip"), (new ZulipConnector().AuthType, new ZulipConnector().ConnectorType));
            Assert.Equal(16, new ZulipConnector().Operations.Count);
        }
    }
}
