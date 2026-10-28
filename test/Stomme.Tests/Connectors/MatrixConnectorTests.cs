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
using Stomme.Modules.Connectors.Matrix;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Matrix (n8n's Matrix node: messages sent and read, rooms made, joined and left, people invited and kicked):
    /// every call the homeserver's client-server API v3 with the access token as a bearer token; a message sent under a
    /// transaction id of its own as a text, a notice or an emote with its HTML; a room's messages read newest first page by
    /// page by the homeserver's token, no more than asked, with a filter; rooms created, joined by id or alias, left; people
    /// invited and kicked with a reason; an event and a room's members by membership got; what could go wrong refused before
    /// anything is sent; Matrix's code and word on a failure; a test naming whose the token is.
    /// </summary>
    public sealed class MatrixConnectorTests
    {
        private const string Home = "https://matrix.kafe.example.test";

        private const string Api = Home + "/_matrix/client/v3";

        private const string Room = "%21fika%3Akafe.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "syt_matrix_1" };

        private sealed class Homeserver : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Homeserver homeserver, string operation, Dictionary<string, string> parameters, string endpoint = Home, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MatrixConnector(), Contexts.Extract(new StubEgress(homeserver), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Messages(int first, int count, string? end) =>
            "{\"start\":\"s\"," + (end is null ? "" : "\"end\":\"" + end + "\",") + "\"chunk\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"type\":\"m.room.message\",\"event_id\":\"$e" + i + "\",\"sender\":\"@ada:kafe.example.test\",\"origin_server_ts\":" + (1760000000000L + i) + ",\"content\":{\"msgtype\":\"m.text\",\"body\":\"Fika " + i + "\"}}")) + "]}";

        [Fact]
        public async Task Messages_are_sent_and_read_page_by_page_newest_first()
        {
            var homeserver = new Homeserver
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("PUT", _) => (HttpStatusCode.OK, "{\"event_id\":\"$sent1\"}"),
                    ("GET", var u) when u.Contains("from=t3", StringComparison.Ordinal) => (HttpStatusCode.OK, Messages(151, 0, "t4")),
                    ("GET", var u) when u.Contains("from=t2", StringComparison.Ordinal) => (HttpStatusCode.OK, Messages(101, 50, "t3")),
                    _ => (HttpStatusCode.OK, Messages(1, 100, "t2")),
                },
            };
            (string, string, string?) Last() => (homeserver.Requests[^1].Method.Method, homeserver.Requests[^1].Url, homeserver.Requests[^1].Body);

            var sent = Assert.Single(await Run(homeserver, MatrixConnector.SendMessageOperation, new() { ["room"] = "!fika:kafe.example.test", ["text"] = "Fika at 3 - Kafé \"Bröd\"", ["html"] = "<b>Fika</b> at 3", ["type"] = "M.Notice" })).Fields;
            var (method, url, body) = Last();
            Assert.Equal("PUT", method);
            Assert.Matches($"^{Api.Replace(".", "\\.", StringComparison.Ordinal)}/rooms/{Room}/send/m\\.room\\.message/stomme[0-9a-f]{{32}}$", url); // a transaction id of its own
            Assert.Equal("{\"msgtype\":\"m.notice\",\"body\":\"Fika at 3 - Kafé \\\"Bröd\\\"\",\"format\":\"org.matrix.custom.html\",\"formatted_body\":\"<b>Fika</b> at 3\"}", body);
            Assert.Equal(("!fika:kafe.example.test", "$sent1"), ((string?)sent["room_id"], (string?)sent["event_id"]));
            await Run(homeserver, MatrixConnector.SendMessageOperation, new() { ["room"] = "!fika:kafe.example.test", ["text"] = "waves" , ["type"] = "m.emote" });
            Assert.Equal("{\"msgtype\":\"m.emote\",\"body\":\"waves\"}", Last().Item3);
            await Run(homeserver, null!, new() { ["room"] = "!fika:kafe.example.test", ["text"] = "Hej" }); // no operation: a message sent
            Assert.Equal("{\"msgtype\":\"m.text\",\"body\":\"Hej\"}", Last().Item3);
            Assert.NotEqual(homeserver.Requests[^1].Url, homeserver.Requests[^2].Url); // every send its own
            Assert.All(homeserver.Requests, request => Assert.Equal("syt_matrix_1", request.Token));
            homeserver.Requests.Clear();

            var read = await Run(homeserver, MatrixConnector.MessagesOperation, new() { ["room"] = "!fika:kafe.example.test", ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/rooms/{Room}/messages?dir=b&limit=100", $"{Api}/rooms/{Room}/messages?dir=b&limit=100&from=t2" }, homeserver.Requests.Select(request => request.Url)); // by the homeserver's token
            var first = read[0].Fields;
            Assert.Equal(("$e1", "@ada:kafe.example.test", 1760000000001L, "Fika 1", "m.text", "{\"msgtype\":\"m.text\",\"body\":\"Fika 1\"}"), ((string?)first["event_id"], (string?)first["sender"], (long?)first["origin_server_ts"], (string?)first["body"], (string?)first["msgtype"], (string?)first["content"]));
            homeserver.Requests.Clear();
            Assert.Equal(150, (await Run(homeserver, MatrixConnector.MessagesOperation, new() { ["room"] = "!fika:kafe.example.test" })).Count); // an empty chunk the end
            Assert.Equal(3, homeserver.Requests.Count);
            homeserver.Requests.Clear();
            Assert.Equal(5, (await Run(homeserver, MatrixConnector.MessagesOperation, new() { ["room"] = "!fika:kafe.example.test", ["max"] = "5", ["filter"] = "{\"types\": [\"m.room.message\"], \"contains_url\": false}" })).Count);
            Assert.Equal($"{Api}/rooms/{Room}/messages?dir=b&limit=5&filter=%7B%22types%22%3A%5B%22m.room.message%22%5D%2C%22contains_url%22%3Afalse%7D", Assert.Single(homeserver.Requests).Url);

            var endless = new Homeserver { Answer = (_, _) => (HttpStatusCode.OK, Messages(1, 100, "t" + Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, MatrixConnector.MessagesOperation, new() { ["room"] = "!fika:kafe.example.test" })).Count); // 1,000 at most when no max is given
            Assert.Equal(10, endless.Requests.Count);

            var last = new Homeserver { Answer = (_, _) => (HttpStatusCode.OK, Messages(1, 3, null)) };
            Assert.Equal(3, (await Run(last, MatrixConnector.MessagesOperation, new() { ["room"] = "!fika" })).Count); // no next token the end - a room version 12 room's id
            Assert.Equal($"{Api}/rooms/%21fika/messages?dir=b&limit=100", Assert.Single(last.Requests).Url);
        }

        [Fact]
        public async Task Rooms_are_created_joined_and_left_and_people_invited_and_kicked()
        {
            var homeserver = new Homeserver
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.EndsWith("/account/whoami", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"user_id\":\"@ada:kafe.example.test\",\"device_id\":\"KAFE\",\"is_guest\":false}"),
                    ("GET", var u) when u.EndsWith("/joined_rooms", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"joined_rooms\":[\"!fika:kafe.example.test\",\"!brod:kafe.example.test\",\"!kaffe:kafe.example.test\"]}"),
                    ("GET", var u) when u.Contains("/members", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"chunk\":[{\"type\":\"m.room.member\",\"state_key\":\"@bo:kafe.example.test\",\"event_id\":\"$m1\",\"content\":{\"membership\":\"join\",\"displayname\":\"Bo\",\"avatar_url\":null}}]}"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"type\":\"m.room.message\",\"event_id\":\"$e1\",\"room_id\":\"!fika:kafe.example.test\",\"content\":{\"msgtype\":\"m.text\",\"body\":\"Fika?\",\"m.mentions\":{}}}"),
                    ("POST", var u) when u.EndsWith("/createRoom", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"room_id\":\"!new:kafe.example.test\"}"),
                    ("POST", var u) when u.Contains("/join/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"room_id\":\"!fika:kafe.example.test\"}"),
                    _ => (HttpStatusCode.OK, "{}"),
                },
            };
            (string, string, string?) Last() => (homeserver.Requests[^1].Method.Method, homeserver.Requests[^1].Url, homeserver.Requests[^1].Body);

            var me = Assert.Single(await Run(homeserver, MatrixConnector.MeOperation, new(), endpoint: Home + "/_matrix/client/v3/")).Fields;
            Assert.Equal(("GET", $"{Api}/account/whoami", (string?)null), Last()); // the API's path given once
            Assert.Equal(("@ada:kafe.example.test", "KAFE", false), ((string?)me["user_id"], (string?)me["device_id"], (bool?)me["is_guest"]));
            Assert.Equal(new[] { "!fika:kafe.example.test", "!brod:kafe.example.test" }, (await Run(homeserver, MatrixConnector.RoomsOperation, new() { ["max"] = "2" })).Select(room => (string?)room.Fields["room_id"]));
            Assert.Equal(("GET", $"{Api}/joined_rooms", (string?)null), Last());

            Assert.Equal("!new:kafe.example.test", (string?)Assert.Single(await Run(homeserver, MatrixConnector.CreateRoomOperation, new()
            {
                ["name"] = "Fika & bröd", ["topic"] = "At 3", ["preset"] = "Private_Chat", ["alias"] = "fika", ["invite"] = "@bo:kafe.example.test, @cy:kafe.example.test:8448", ["direct"] = "False",
            })).Fields["room_id"]);
            Assert.Equal(("POST", $"{Api}/createRoom", (string?)"{\"name\":\"Fika & bröd\",\"topic\":\"At 3\",\"preset\":\"private_chat\",\"room_alias_name\":\"fika\",\"invite\":[\"@bo:kafe.example.test\",\"@cy:kafe.example.test:8448\"],\"is_direct\":false}"), Last());
            await Run(homeserver, MatrixConnector.CreateRoomOperation, new());
            Assert.Equal("{}", Last().Item3);
            Assert.Equal("!fika:kafe.example.test", (string?)Assert.Single(await Run(homeserver, MatrixConnector.JoinRoomOperation, new() { ["room"] = "#fika:kafe.example.test" })).Fields["room_id"]);
            Assert.Equal(("POST", $"{Api}/join/%23fika%3Akafe.example.test", (string?)"{}"), Last()); // by its alias
            await Run(homeserver, MatrixConnector.JoinRoomOperation, new() { ["room"] = "!fika:kafe.example.test" });
            Assert.Equal($"{Api}/join/{Room}", Last().Item2);
            var left = Assert.Single(await Run(homeserver, MatrixConnector.LeaveRoomOperation, new() { ["room"] = "!fika:kafe.example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/rooms/{Room}/leave", (string?)"{}"), Last());
            Assert.Equal(("!fika:kafe.example.test", true), ((string?)left["room_id"], (bool?)left["left"]));

            var invited = Assert.Single(await Run(homeserver, MatrixConnector.InviteOperation, new() { ["room"] = "!fika:kafe.example.test", ["user"] = "@bo:kafe.example.test", ["reason"] = "Fika" })).Fields;
            Assert.Equal(("POST", $"{Api}/rooms/{Room}/invite", (string?)"{\"user_id\":\"@bo:kafe.example.test\",\"reason\":\"Fika\"}"), Last());
            Assert.Equal(("!fika:kafe.example.test", "@bo:kafe.example.test", true), ((string?)invited["room_id"], (string?)invited["user_id"], (bool?)invited["invited"]));
            var kicked = Assert.Single(await Run(homeserver, MatrixConnector.KickOperation, new() { ["room"] = "!fika:kafe.example.test", ["user"] = "@bo:kafe.example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/rooms/{Room}/kick", (string?)"{\"user_id\":\"@bo:kafe.example.test\"}"), Last());
            Assert.Equal(("@bo:kafe.example.test", true), ((string?)kicked["user_id"], (bool?)kicked["kicked"]));

            var gotten = Assert.Single(await Run(homeserver, MatrixConnector.EventOperation, new() { ["room"] = "!fika:kafe.example.test", ["event"] = "$e1" })).Fields;
            Assert.Equal(("GET", $"{Api}/rooms/{Room}/event/%24e1", (string?)null), Last());
            Assert.Equal(("Fika?", "m.text", "{\"msgtype\":\"m.text\",\"body\":\"Fika?\",\"m.mentions\":{}}"), ((string?)gotten["body"], (string?)gotten["msgtype"], (string?)gotten["content"]));
            var member = Assert.Single(await Run(homeserver, MatrixConnector.MembersOperation, new() { ["room"] = "!fika:kafe.example.test", ["membership"] = "Join", ["not_membership"] = "ban" })).Fields;
            Assert.Equal(("GET", $"{Api}/rooms/{Room}/members?membership=join&not_membership=ban", (string?)null), Last());
            Assert.Equal(("@bo:kafe.example.test", "join", "Bo"), ((string?)member["state_key"], (string?)member["membership"], (string?)member["displayname"]));
            await Run(homeserver, MatrixConnector.MembersOperation, new() { ["room"] = "!fika:kafe.example.test" });
            Assert.Equal($"{Api}/rooms/{Room}/members", Last().Item2);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_matrixs_words_on_a_failure()
        {
            var homeserver = new Homeserver();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Home, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(homeserver, operation, parameters, endpoint, credentials))).Message;
            const string Fika = "!fika:kafe.example.test";

            Assert.StartsWith("Matrix has no operation 'upload' - me, rooms, create_room,", await Refused("upload", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MatrixConnector.RoomsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the room - !abc:example.org.", await Refused(MatrixConnector.MessagesOperation, new()));
            Assert.Equal("'#fika:kafe.example.test' is not a room's id - !abc:example.org (room).", await Refused(MatrixConnector.LeaveRoomOperation, new() { ["room"] = "#fika:kafe.example.test" }));
            Assert.Equal("'!fika/../x' is not a room's id - !abc:example.org (room).", await Refused(MatrixConnector.EventOperation, new() { ["room"] = "!fika/../x", ["event"] = "$e1" }));
            Assert.Equal("Give the room - !abc:example.org or #fika:example.org.", await Refused(MatrixConnector.JoinRoomOperation, new()));
            Assert.Equal("'fika' is not a room's id or alias - #fika:example.org (room).", await Refused(MatrixConnector.JoinRoomOperation, new() { ["room"] = "fika" }));
            Assert.Equal("'#fika' is not a room's id or alias - #fika:example.org (room).", await Refused(MatrixConnector.JoinRoomOperation, new() { ["room"] = "#fika" }));
            Assert.Equal("Give the user - @ada:example.org.", await Refused(MatrixConnector.InviteOperation, new() { ["room"] = Fika }));
            Assert.Equal("'bo' is not a user's id - @ada:example.org (user).", await Refused(MatrixConnector.KickOperation, new() { ["room"] = Fika, ["user"] = "bo" }));
            Assert.Equal("Give the event - $abc.", await Refused(MatrixConnector.EventOperation, new() { ["room"] = Fika }));
            Assert.Equal("'e1' is not an event's id - $abc (event).", await Refused(MatrixConnector.EventOperation, new() { ["room"] = Fika, ["event"] = "e1" }));
            Assert.Equal("Give the text - the plain text when HTML is given too.", await Refused(MatrixConnector.SendMessageOperation, new() { ["room"] = Fika, ["html"] = "<b>Fika</b>" }));
            Assert.Equal("'m.image' is not m.text, m.notice or m.emote (type).", await Refused(MatrixConnector.SendMessageOperation, new() { ["room"] = Fika, ["text"] = "Fika", ["type"] = "m.image" }));
            Assert.Equal("The filter is not a JSON object - {\"types\": [\"m.room.message\"]}.", await Refused(MatrixConnector.MessagesOperation, new() { ["room"] = Fika, ["filter"] = "[\"m.room.message\"]" }));
            Assert.Equal("The filter is not a JSON object - {\"types\": [\"m.room.message\"]}.", await Refused(MatrixConnector.MessagesOperation, new() { ["room"] = Fika, ["filter"] = "types = m.room.message" }));
            Assert.Equal("'gone' is not join, invite, leave, ban or knock (membership).", await Refused(MatrixConnector.MembersOperation, new() { ["room"] = Fika, ["membership"] = "gone" }));
            Assert.Equal("'joined' is not join, invite, leave, ban or knock (not_membership).", await Refused(MatrixConnector.MembersOperation, new() { ["room"] = Fika, ["not_membership"] = "joined" }));
            Assert.Equal("'open' is not public_chat, private_chat or trusted_private_chat (preset).", await Refused(MatrixConnector.CreateRoomOperation, new() { ["preset"] = "open" }));
            Assert.Equal("'#fika:kafe.example.test' is not a room's local alias - fika (alias).", await Refused(MatrixConnector.CreateRoomOperation, new() { ["alias"] = "#fika:kafe.example.test" }));
            Assert.Equal("'bo' is not a user's id - @ada:example.org (invite).", await Refused(MatrixConnector.CreateRoomOperation, new() { ["invite"] = "@ada:kafe.example.test,bo" }));
            Assert.Equal("'yes' is not true or false (direct).", await Refused(MatrixConnector.CreateRoomOperation, new() { ["direct"] = "yes" }));
            Assert.Equal("Give the homeserver's address - https://matrix.example.org.", await Refused(MatrixConnector.MeOperation, new(), endpoint: ""));
            Assert.Equal("The endpoint 'matrix.kafe.example.test' is not an absolute http(s) URL.", await Refused(MatrixConnector.MeOperation, new(), endpoint: "matrix.kafe.example.test"));
            Assert.Equal("The Matrix connection has no access token.", await Refused(MatrixConnector.MeOperation, new(), credentials: new()));
            Assert.Empty(homeserver.Requests); // nothing sent
            Assert.Single(await Run(homeserver, MatrixConnector.CreateRoomOperation, new() { ["alias"] = new string('a', 255) }));
            Assert.Equal("'" + new string('a', 256) + "' is not a room's local alias - fika (alias).", await Refused(MatrixConnector.CreateRoomOperation, new() { ["alias"] = new string('a', 256) }));

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Homeserver { Answer = (_, _) => (status, answer) }, MatrixConnector.InviteOperation, new() { ["room"] = Fika, ["user"] = "@bo:kafe.example.test" }))).Message;
            Assert.Equal("HTTP 403 (M_FORBIDDEN): You are not allowed to invite here.", await Failed(HttpStatusCode.Forbidden, "{\"errcode\":\"M_FORBIDDEN\",\"error\":\"You are not allowed to invite here.\"}"));
            Assert.Equal("HTTP 401 (M_UNKNOWN_TOKEN).", await Failed(HttpStatusCode.Unauthorized, "{\"errcode\":\"M_UNKNOWN_TOKEN\"}"));
            Assert.Equal("HTTP 500 (M_UNKNOWN): " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errcode\":\"M_UNKNOWN\",\"error\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Matrix answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Homeserver homeserver, string endpoint = Home) =>
                new MatrixConnector().TestConnectionAsync(Contexts.Test(new StubEgress(homeserver), endpoint, null, Credentials));
            var whoami = new Homeserver { Answer = (_, _) => (HttpStatusCode.OK, "{\"user_id\":\"@ada:kafe.example.test\",\"device_id\":\"KAFE\"}") };
            var tested = await Test(whoami);
            Assert.Equal((true, "Matrix: the token is @ada:kafe.example.test's (device KAFE)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/account/whoami", "GET", "syt_matrix_1"), (whoami.Requests[0].Url, whoami.Requests[0].Method.Method, whoami.Requests[0].Token));
            Assert.Equal("Matrix: the token is a user's.", (await Test(new Homeserver())).Message);
            var refused = await Test(new Homeserver { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errcode\":\"M_UNKNOWN_TOKEN\",\"error\":\"Invalid access token passed.\"}") });
            Assert.Equal((false, "HTTP 401 (M_UNKNOWN_TOKEN): Invalid access token passed."), (refused.Success, refused.Message));
            Assert.Equal((false, "Give the homeserver's address - https://matrix.example.org."), ((await Test(new Homeserver(), "")).Success, (await Test(new Homeserver(), "")).Message));
            Assert.Equal((AuthenticationType.Bearer, "matrix"), (new MatrixConnector().AuthType, new MatrixConnector().ConnectorType));
            Assert.Equal(11, new MatrixConnector().Operations.Count);
            Assert.Equal(("token", "Access token (Element: Settings > Help & About)"), (Assert.Single(new MatrixConnector().CredentialLabels!).Key, Assert.Single(new MatrixConnector().CredentialLabels!).Value)); // in Matrix's words
        }
    }
}
