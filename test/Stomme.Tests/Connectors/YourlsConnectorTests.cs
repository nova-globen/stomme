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
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Yourls;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// YOURLS (n8n's YOURLS node) without a server: a URL, a keyword, a title and a short URL held to YOURLS's shape before
    /// anything is sent - YOURLS itself would store "not a url"; the signature in the form's body and in no address; a URL
    /// shortened with its keyword and title, and one YOURLS already has answered as its existing short URL; a short URL expanded
    /// and its stats read by its keyword or its whole address; YOURLS's message on a failure; a test reading its version.
    /// </summary>
    public sealed class YourlsConnectorTests
    {
        private const string Address = "https://sho.rt";

        private const string Api = Address + "/yourls-api.php";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "0123456789abcdef0123456789abcdef" };

        private sealed record Sent(string Method, string Url, string? Body);

        private sealed class Yourls : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Yourls(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private const string Shortened = "{\"status\":\"success\",\"code\":\"\",\"message\":\"https:\\/\\/kafe.example\\/menu added to database\",\"url\":{\"keyword\":\"meny1\",\"url\":\"https:\\/\\/kafe.example\\/menu\",\"title\":\"Dagens meny\",\"date\":\"2026-10-22 01:48:45\",\"ip\":\"203.0.113.7\"},\"title\":\"Dagens meny\",\"shorturl\":\"https:\\/\\/sho.rt\\/meny1\"}";

        private const string Existing = "{\"status\":\"fail\",\"code\":\"error:url\",\"message\":\"https://kafe.example/menu already exists in database (short URL: sho.rt/meny1)\",\"errorCode\":\"409\",\"statusCode\":\"409\",\"url\":{\"keyword\":\"meny1\",\"url\":\"https://kafe.example/menu\",\"title\":\"Dagens meny\",\"date\":\"2026-10-22 01:48:45\",\"ip\":\"203.0.113.7\",\"clicks\":0},\"title\":\"Dagens meny\",\"shorturl\":\"https://sho.rt/meny1\"}";

        private static Task<List<ConnectorRecord>> Run(Yourls yourls, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new YourlsConnector(), Contexts.Extract(new StubEgress(yourls), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static Dictionary<string, string> Form(string body) => body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

        [Fact]
        public void It_declares_itself_as_n8ns_yourls_node()
        {
            IConnector connector = new YourlsConnector();
            Assert.Equal(("yourls", "YOURLS", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "shorten", "expand", "stats" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_yourls_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var yourls = new Yourls(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(yourls, operation, parameters, endpoint));
                Assert.Empty(yourls.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.Equal("YOURLS has no operation 'delete' - shorten, expand or stats.", await Refused("delete", new()));
            Assert.Equal("Give the URL to shorten - https://kafe.example/menu (url).", await Refused("shorten", new()));
            Assert.Equal("'not a url' is not a web address - https://kafe.example/menu (url).", await Refused("shorten", new() { ["url"] = "not a url" }));
            Assert.Equal("'ftp://kafe.example/menu' is not a web address - https://kafe.example/menu (url).", await Refused("shorten", new() { ["url"] = "ftp://kafe.example/menu" }));
            Assert.Equal("'https://ada:secret@kafe.example/' is not a web address - https://kafe.example/menu (url).", await Refused("shorten", new() { ["url"] = "https://ada:secret@kafe.example/" }));
            Assert.Equal("'dagens meny' is not a keyword - YOURLS's are lowercase letters and digits, and it would drop the rest: meny1 (keyword).", await Refused("shorten", new() { ["url"] = "https://kafe.example/", ["keyword"] = "dagens meny" }));
            Assert.Equal("'MenyAB1' is not a keyword - YOURLS's are lowercase letters and digits, and it would drop the rest: meny1 (keyword).", await Refused("shorten", new() { ["url"] = "https://kafe.example/", ["keyword"] = "MenyAB1" }));
            Assert.Equal("'meny-1' is not a keyword - YOURLS's are lowercase letters and digits, and it would drop the rest: meny1 (keyword).", await Refused("shorten", new() { ["url"] = "https://kafe.example/", ["keyword"] = "meny-1" }));
            Assert.Equal("A title is at most 500 characters, on one line (title).", await Refused("shorten", new() { ["url"] = "https://kafe.example/", ["title"] = "Dagens\nmeny" }));
            Assert.Equal("Give the short URL - its keyword (meny1) or its whole address (short_url).", await Refused("expand", new()));
            Assert.Equal("'meny-1' is not a short URL - its keyword (meny1) or its whole address, https://sho.rt/meny1 (short_url).", await Refused("stats", new() { ["short_url"] = "meny-1" }));
            Assert.Equal("'https://sho.rt/a/b' is not a short URL - its keyword (meny1) or its whole address, https://sho.rt/meny1 (short_url).", await Refused("expand", new() { ["short_url"] = "https://sho.rt/a/b" }));
            Assert.Equal("Give YOURLS's address - https://sho.rt.", await Refused("expand", new() { ["short_url"] = "meny1" }, ""));
            Assert.Equal("'https://sho.rt/?x=1' is not YOURLS's address - https://sho.rt, with no query.", await Refused("expand", new() { ["short_url"] = "meny1" }, "https://sho.rt/?x=1"));
            Assert.Equal("The YOURLS connection has no signature token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new YourlsConnector(), Contexts.Extract(new StubEgress(new Yourls(_ => (HttpStatusCode.OK, "{}"))), Address, "expand", new Dictionary<string, string> { ["short_url"] = "meny1" }, new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_url_is_shortened_with_its_keyword_and_title_and_one_yourls_has_comes_back_as_it_was()
        {
            var yourls = new Yourls(_ => (HttpStatusCode.OK, Shortened));
            var made = Assert.Single(await Run(yourls, "shorten", new() { ["url"] = "https://kafe.example/menu", ["keyword"] = "meny1", ["title"] = "Dagens meny" }, Api)).Fields;
            var sent = Assert.Single(yourls.Requests);
            Assert.Equal(("POST", Api), (sent.Method, sent.Url)); // the API's own address given: the same address
            Assert.Equal(new Dictionary<string, string> { ["action"] = "shorturl", ["url"] = "https://kafe.example/menu", ["keyword"] = "meny1", ["title"] = "Dagens meny", ["format"] = "json", ["signature"] = "0123456789abcdef0123456789abcdef" }, Form(sent.Body!)); // YOURLS's own name for it on the wire
            Assert.DoesNotContain("signature", sent.Url, StringComparison.Ordinal);
            Assert.Equal(("https://sho.rt/meny1", "meny1", "https://kafe.example/menu", "Dagens meny", "2026-10-22 01:48:45", false), ((string?)made["short_url"], (string?)made["keyword"], (string?)made["url"], (string?)made["title"], (string?)made["date"], (bool?)made["existing"]));

            var again = Assert.Single(await Run(new Yourls(_ => (HttpStatusCode.Conflict, Existing)), "shorten", new() { ["url"] = "https://kafe.example/menu" })).Fields;
            Assert.Equal(("https://sho.rt/meny1", "meny1", true), ((string?)again["short_url"], (string?)again["keyword"], (bool?)again["existing"]));
            var taken = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Yourls(_ => (HttpStatusCode.Conflict, Existing)), "expand", new() { ["short_url"] = "meny1" }));
            Assert.StartsWith("YOURLS answered HTTP 409: https://kafe.example/menu already exists", taken.Message); // only a shorten takes it so
        }

        [Fact]
        public async Task A_short_url_is_expanded_and_its_stats_read_by_its_keyword_or_its_address()
        {
            var yourls = new Yourls(sent => (HttpStatusCode.OK, Form(sent.Body!)["action"] == "expand"
                ? "{\"keyword\":\"meny1\",\"shorturl\":\"https:\\/\\/sho.rt\\/meny1\",\"longurl\":\"https:\\/\\/kafe.example\\/menu\",\"title\":\"Dagens meny\",\"message\":\"success\",\"statusCode\":\"200\"}"
                : "{\"statusCode\":\"200\",\"message\":\"success\",\"link\":{\"shorturl\":\"https:\\/\\/sho.rt\\/meny1\",\"url\":\"https:\\/\\/kafe.example\\/menu\",\"title\":\"Dagens meny\",\"timestamp\":\"2026-10-22 01:48:45\",\"ip\":\"203.0.113.7\",\"clicks\":\"12\"}}"));
            var expanded = Assert.Single(await Run(yourls, "expand", new() { ["short_url"] = "meny1" })).Fields;
            Assert.Equal(("https://kafe.example/menu", "meny1", "Dagens meny"), ((string?)expanded["url"], (string?)expanded["keyword"], (string?)expanded["title"]));
            var stats = Assert.Single(await Run(yourls, "stats", new() { ["short_url"] = "https://sho.rt/meny1" })).Fields;
            Assert.Equal((12L, "2026-10-22 01:48:45", "https://kafe.example/menu"), ((long?)stats["clicks"], (string?)stats["date"], (string?)stats["url"]));
            Assert.Equal(("url-stats", "https://sho.rt/meny1"), (Form(yourls.Requests[1].Body!)["action"], Form(yourls.Requests[1].Body!)["shorturl"]));
        }

        [Fact]
        public async Task Yourls_message_is_said_and_an_answer_that_is_not_yours_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "expand") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Yourls(_ => (status, body)), operation, operation == "shorten" ? new() { ["url"] = "https://kafe.example/" } : new() { ["short_url"] = "meny1" }))).Message;

            Assert.Equal("YOURLS answered HTTP 404: Error: short URL not found.", await Failed(HttpStatusCode.NotFound, "{\"keyword\":\"meny1\",\"message\":\"Error: short URL not found\",\"errorCode\":\"404\"}"));
            Assert.Equal("YOURLS answered HTTP 400: Short URL meny1 already exists in database or is reserved.", await Failed(HttpStatusCode.BadRequest, "{\"status\":\"fail\",\"code\":\"error:keyword\",\"message\":\"Short URL meny1 already exists in database or is reserved\"}", "shorten"));
            Assert.Equal("YOURLS answered HTTP 403: Please log in. - check the signature token (YOURLS admin - Tools).", await Failed(HttpStatusCode.Forbidden, "{\"message\":\"Please log in\",\"errorCode\":\"403\"}"));
            Assert.Equal("YOURLS answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("YOURLS answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[{\"longurl\":\"https://kafe.example/\"}]"));
            Assert.Equal("YOURLS answered without the long URL - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"message\":\"success\"}"));
            Assert.Equal("YOURLS answered without the link - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"status\":\"success\"}", "shorten"));
        }

        [Fact]
        public async Task A_test_reads_yourls_version()
        {
            var connector = new YourlsConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Yourls(_ => (HttpStatusCode.OK, "{\"version\":\"1.10.6\"}"))), Address, null, Credentials));
            Assert.True(ok.Success);
            Assert.False((await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Yourls(_ => (HttpStatusCode.OK, "<html>Sign in</html>"))), Address, null, Credentials))).Success);
        }
    }

    /// <summary>
    /// YOURLS LIVE, against this machine's throwaway YOURLS 1.10 (scripts/yourls-test-server.ps1) - fail-not-skip without it
    /// (D46): a URL shortened with a keyword and a title, shortened again and answered as its existing short URL, its keyword
    /// refused for another URL, expanded and its stats read by keyword and by address; a wrong signature refused with YOURLS's own
    /// words.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class YourlsConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Signature => new() { ["api-key"] = YourlsTestEndpoint.Signature };

        private static HostConnectorEgress Egress => new(new[] { new Uri(YourlsTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new YourlsConnector(), Contexts.Extract(Egress, YourlsTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Signature, timeoutSeconds: 60));

        [Fact]
        public async Task A_test_reads_the_version_and_refuses_a_wrong_signature()
        {
            var tested = await new YourlsConnector().TestConnectionAsync(Contexts.Test(Egress, YourlsTestEndpoint.Endpoint, credentials: Signature, timeoutSeconds: 60));
            Assert.Equal((true, "YOURLS 1.10.6 answers."), (tested.Success, tested.Message));
            var wrong = await new YourlsConnector().TestConnectionAsync(Contexts.Test(Egress, YourlsTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = new string('a', 32) }, timeoutSeconds: 60));
            Assert.Equal((false, "YOURLS answered HTTP 403: Please log in. - check the signature token (YOURLS admin - Tools)."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_url_is_shortened_found_again_expanded_and_counted()
        {
            var marker = Guid.NewGuid().ToString("N")[..10];
            var url = $"https://kafe.example/menu?day={marker}";
            var keyword = "meny" + marker;
            var made = Assert.Single(await Run("shorten", new() { ["url"] = url, ["keyword"] = keyword, ["title"] = "Dagens meny" })).Fields;
            output.WriteLine($"short url {made["short_url"]}");
            Assert.Equal((keyword, url, "Dagens meny", false), ((string?)made["keyword"], (string?)made["url"], (string?)made["title"], (bool?)made["existing"]));
            Assert.Equal($"{YourlsTestEndpoint.Endpoint}/{keyword}", (string?)made["short_url"]);

            var again = Assert.Single(await Run("shorten", new() { ["url"] = url })).Fields;
            Assert.Equal((keyword, true), ((string?)again["keyword"], (bool?)again["existing"]));
            var taken = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("shorten", new() { ["url"] = url + "&other=1", ["keyword"] = keyword }));
            Assert.Equal($"YOURLS answered HTTP 400: Short URL {keyword} already exists in database or is reserved.", taken.Message);

            Assert.Equal(url, (string?)Assert.Single(await Run("expand", new() { ["short_url"] = keyword })).Fields["url"]);
            var stats = Assert.Single(await Run("stats", new() { ["short_url"] = (string)made["short_url"]! })).Fields;
            Assert.Equal((url, 0L), ((string?)stats["url"], (long?)stats["clicks"]));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("expand", new() { ["short_url"] = "nosuch" + marker }));
            Assert.Equal("YOURLS answered HTTP 404: Error: short URL not found.", missing.Message);
        }
    }
}
