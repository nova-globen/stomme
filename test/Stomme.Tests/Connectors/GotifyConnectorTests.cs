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
using Stomme.Modules.Connectors.Gotify;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Gotify (n8n's Gotify node: its messages sent, read and deleted): every call the server's API with the
    /// application's token in X-Gotify-Key to send and the client's to read and delete; a message sent with every extra; the
    /// messages read newest first page by page by Gotify's since - never the address it names - no more than asked; one deleted;
    /// the applications listed; what could go wrong refused before anything is sent; Gotify's words on a failure; a test naming
    /// whose the client token is.
    /// </summary>
    public sealed class GotifyConnectorTests
    {
        private const string Server = "https://gotify.kafe.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "A_app", ["password"] = "C_client" };

        private sealed class Gotify : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("X-Gotify-Key", out var keys) ? keys.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Gotify gotify, string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new GotifyConnector(), Contexts.Extract(new StubEgress(gotify), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Page(int newest, int count, bool next) =>
            "{\"messages\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => "{\"id\":" + (newest - i) + ",\"appid\":1,\"message\":\"Fika " + (newest - i) + "\",\"title\":\"Kafé\",\"priority\":5,\"date\":\"2026-10-13T10:00:00Z\",\"extras\":{\"client::display\":{\"contentType\":\"text/plain\"}}}")) + "],"
            + "\"paging\":{\"size\":" + count + ",\"limit\":100,\"since\":" + (count == 0 ? 0 : newest - count + 1) + (next ? ",\"next\":\"https://elsewhere.example.test/message?limit=100&since=" + (newest - count + 1) + "\"" : "") + "}}";

        [Fact]
        public async Task Messages_are_sent_read_page_by_page_and_deleted()
        {
            var gotify = new Gotify
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("POST", _) => (HttpStatusCode.OK, "{\"id\":301,\"appid\":1,\"message\":\"Fika at 3\",\"priority\":8}"),
                    ("GET", var u) when u.Contains("since=201", StringComparison.Ordinal) => (HttpStatusCode.OK, Page(200, 80, false)),
                    ("GET", var u) when u.Contains("/message?", StringComparison.Ordinal) => (HttpStatusCode.OK, Page(300, 100, true)),
                    ("GET", _) => (HttpStatusCode.OK, "[{\"id\":1,\"name\":\"Kassa\",\"token\":\"A_app\",\"internal\":false}]"),
                    _ => (HttpStatusCode.OK, ""),
                },
            };
            (string, string, string?, string?) Last() => (gotify.Requests[^1].Method.Method, gotify.Requests[^1].Url, gotify.Requests[^1].Key, gotify.Requests[^1].Body);

            var sent = Assert.Single(await Run(gotify, GotifyConnector.CreateMessageOperation, new()
            {
                ["message"] = "Fika at 3 - **Kafé** & \"bröd\"", ["title"] = "Fika", ["priority"] = "8", ["content_type"] = "Text/Markdown", ["click_url"] = "https://kafe.example.test/fika",
                ["big_image_url"] = "https://kafe.example.test/bun.png", ["intent_url"] = "kafe://fika",
            }, endpoint: Server + "/")).Fields;
            Assert.Equal(("POST", $"{Server}/message", (string?)"A_app", (string?)"{\"message\":\"Fika at 3 - **Kafé** & \\\"bröd\\\"\",\"title\":\"Fika\",\"priority\":8,\"extras\":{\"client::display\":{\"contentType\":\"text/markdown\"},\"client::notification\":{\"click\":{\"url\":\"https://kafe.example.test/fika\"},\"bigImageUrl\":\"https://kafe.example.test/bun.png\"},\"android::action\":{\"onReceive\":{\"intentUrl\":\"kafe://fika\"}}}}"), Last()); // the application's token to send
            Assert.Equal((301L, 8L), ((long?)sent["id"], (long?)sent["priority"]));
            await Run(gotify, null!, new() { ["message"] = "Hej", ["priority"] = "0", ["big_image_url"] = "https://kafe.example.test/bun.png" }); // no operation: a message sent
            Assert.Equal("{\"message\":\"Hej\",\"priority\":0,\"extras\":{\"client::notification\":{\"bigImageUrl\":\"https://kafe.example.test/bun.png\"}}}", Last().Item4);
            await Run(gotify, GotifyConnector.CreateMessageOperation, new() { ["message"] = "Hej", ["priority"] = "10" });
            Assert.Equal("{\"message\":\"Hej\",\"priority\":10}", Last().Item4);
            gotify.Requests.Clear();

            var read = await Run(gotify, GotifyConnector.MessagesOperation, new() { ["max"] = "150" });
            Assert.Equal(150, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Server}/message?limit=100", $"{Server}/message?limit=100&since=201" }, gotify.Requests.Select(request => request.Url)); // Gotify's since, never the address it names
            Assert.All(gotify.Requests, request => Assert.Equal("C_client", request.Key)); // the client's token to read
            Assert.Equal((300L, "Fika 300", 5L, "{\"client::display\":{\"contentType\":\"text/plain\"}}"), ((long?)read[0].Fields["id"], (string?)read[0].Fields["message"], (long?)read[0].Fields["priority"], (string?)read[0].Fields["extras"]));
            gotify.Requests.Clear();
            Assert.Equal(180, (await Run(gotify, GotifyConnector.MessagesOperation, new())).Count); // no next the end
            Assert.Equal(2, gotify.Requests.Count);
            gotify.Requests.Clear();
            Assert.Equal(5, (await Run(gotify, GotifyConnector.MessagesOperation, new() { ["max"] = "5" })).Count);
            Assert.Equal($"{Server}/message?limit=5", Assert.Single(gotify.Requests).Url);
            var empty = new Gotify { Answer = (_, _) => (HttpStatusCode.OK, "{\"messages\":[],\"paging\":{\"size\":0,\"since\":5,\"limit\":100,\"next\":\"https://gotify.kafe.example.test/message?limit=100&since=5\"}}") };
            Assert.Empty(await Run(empty, GotifyConnector.MessagesOperation, new())); // an empty page the end
            Assert.Single(empty.Requests);
            var unpaged = new Gotify { Answer = (_, _) => (HttpStatusCode.OK, "{\"messages\":[{\"id\":3}],\"paging\":{\"size\":1,\"since\":0,\"limit\":100,\"next\":\"https://gotify.kafe.example.test/message?limit=100&since=0\"}}") };
            Assert.Single(await Run(unpaged, GotifyConnector.MessagesOperation, new())); // a since of none the end, never the newest again
            Assert.Single(unpaged.Requests);
            var endless = new Gotify { Answer = (_, url) => (HttpStatusCode.OK, Page(url.Contains("since=", StringComparison.Ordinal) ? int.Parse(url[(url.IndexOf("since=", StringComparison.Ordinal) + 6)..]) - 1 : 100000, 100, true)) };
            Assert.Equal(1000, (await Run(endless, GotifyConnector.MessagesOperation, new())).Count); // 1,000 at most when no max is given

            var deleted = Assert.Single(await Run(gotify, GotifyConnector.DeleteMessageOperation, new() { ["id"] = "301" })).Fields;
            Assert.Equal(("DELETE", $"{Server}/message/301", (string?)"C_client", (string?)null), Last()); // the client's token to delete
            Assert.Equal((301L, true), ((long?)deleted["id"], (bool?)deleted["deleted"]));
            var application = Assert.Single(await Run(gotify, GotifyConnector.ApplicationsOperation, new())).Fields;
            Assert.Equal(("GET", $"{Server}/application", (string?)"C_client", (string?)null), Last());
            Assert.Equal(("Kassa", false), ((string?)application["name"], (bool?)application["internal"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_gotifys_words_on_a_failure()
        {
            var gotify = new Gotify();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(gotify, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Gotify has no operation 'clients' - create_message, messages, delete_message", await Refused("clients", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GotifyConnector.MessagesOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the message.", await Refused(GotifyConnector.CreateMessageOperation, new() { ["title"] = "Fika" }));
            Assert.Equal("'11' is not a whole number from 0 to 10 (priority).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["priority"] = "11" }));
            Assert.Equal("'-1' is not a whole number from 0 to 10 (priority).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["priority"] = "-1" }));
            Assert.Equal("'text/html' is not text/plain or text/markdown (content_type).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["content_type"] = "text/html" }));
            Assert.Equal("'kafe' is not an address (click_url).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["click_url"] = "kafe" }));
            Assert.Equal("'bun.png' is not an address (big_image_url).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["big_image_url"] = "bun.png" }));
            Assert.Equal("'fika' is not an address (intent_url).", await Refused(GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika", ["intent_url"] = "fika" }));
            Assert.Equal("Give the message's id.", await Refused(GotifyConnector.DeleteMessageOperation, new()));
            Assert.Equal("'301/../x' is not an id (id).", await Refused(GotifyConnector.DeleteMessageOperation, new() { ["id"] = "301/../x" }));
            Assert.Equal("Give the Gotify server's address - https://gotify.example.com.", await Refused(GotifyConnector.MessagesOperation, new(), endpoint: ""));
            Assert.Equal("The Gotify connection has no application token.", await Refused(GotifyConnector.MessagesOperation, new(), credentials: new() { ["password"] = "C" }));
            Assert.Equal("The Gotify connection has no client token.", await Refused(GotifyConnector.MessagesOperation, new(), credentials: new() { ["username"] = "A" }));
            Assert.Empty(gotify.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Gotify { Answer = (_, _) => (status, answer) }, GotifyConnector.CreateMessageOperation, new() { ["message"] = "Fika" }))).Message;
            Assert.Equal("HTTP 401: Unauthorized: you need to provide a valid access token or user credentials to access this api", await Failed(HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\",\"errorCode\":401,\"errorDescription\":\"you need to provide a valid access token or user credentials to access this api\"}"));
            Assert.Equal("HTTP 400: Bad Request", await Failed(HttpStatusCode.BadRequest, "{\"error\":\"Bad Request\",\"errorCode\":400}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Gotify answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_client_token_is()
        {
            Task<ConnectionTestResult> Test(Gotify gotify) =>
                new GotifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(gotify), Server, null, Credentials));
            var user = new Gotify { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":1,\"name\":\"ada\",\"admin\":true}") };
            var tested = await Test(user);
            Assert.Equal((true, "Gotify: the client token is ada's, an admin."), (tested.Success, tested.Message));
            Assert.Equal(($"{Server}/current/user", "GET", "C_client"), (user.Requests[0].Url, user.Requests[0].Method.Method, user.Requests[0].Key));
            Assert.Equal("Gotify: the client token is bo's.", (await Test(new Gotify { Answer = (_, _) => (HttpStatusCode.OK, "{\"name\":\"bo\",\"admin\":false}") })).Message);
            Assert.Equal("Gotify: the client token is a user's.", (await Test(new Gotify())).Message);
            var refused = await Test(new Gotify { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\",\"errorCode\":401}") });
            Assert.Equal((false, "HTTP 401: Unauthorized"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "gotify"), (new GotifyConnector().AuthType, new GotifyConnector().ConnectorType));
            Assert.Equal(4, new GotifyConnector().Operations.Count);
            Assert.Equal("Client token (Clients > a client - to read and delete)", new GotifyConnector().CredentialLabels!["password"]); // in Gotify's words
        }
    }
}
