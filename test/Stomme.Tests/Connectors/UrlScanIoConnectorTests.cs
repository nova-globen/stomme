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
using Stomme.Modules.Connectors.UrlScanIo;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Urlscan.io (n8n's urlscan.io node) without a server: every URL, id, tag, choice and length held to urlscan.io's
    /// shape before anything is sent; the key in urlscan.io's API-Key header and in no address; a scan submitted private by
    /// default, and - beyond n8n's node - waited for until urlscan.io stops answering 404, its verdict, score, page and screenshot
    /// beside urlscan.io's own sections; a result read, asked again a few times while not finished; scans searched page by page
    /// by urlscan.io's search_after cursor up to the most; urlscan.io's message and description on a failure; a test reading the
    /// key's quotas.
    /// </summary>
    public sealed class UrlScanIoConnectorTests
    {
        private const string Api = "https://urlscan.io/api/v1";

        private const string Uuid = "0196c4c4-9a2b-7000-8000-0123456789ab";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "urlscan-kafe-key" };

        private sealed record Sent(string Method, string Url, string? Key, string? Body);

        private sealed class UrlScan : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public UrlScan(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("API-Key", out var key) ? key.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private const string Submitted = "{\"message\":\"Submission successful\",\"uuid\":\"" + Uuid + "\",\"result\":\"https://urlscan.io/result/" + Uuid + "/\",\"api\":\"https://urlscan.io/api/v1/result/" + Uuid + "/\",\"visibility\":\"private\",\"url\":\"https://kassa.example/login\",\"country\":\"se\"}";

        private const string Result = "{\"task\":{\"uuid\":\"" + Uuid + "\",\"url\":\"https://kassa.example/login\",\"screenshotURL\":\"https://urlscan.io/screenshots/" + Uuid + ".png\"},\"page\":{\"url\":\"https://kassa.example/login\",\"domain\":\"kassa.example\",\"ip\":\"203.0.113.7\",\"country\":\"SE\",\"server\":\"nginx\"},\"verdicts\":{\"overall\":{\"score\":100,\"malicious\":true,\"categories\":[\"phishing\"]}},\"lists\":{\"ips\":[\"203.0.113.7\"]},\"stats\":{\"malicious\":1}}";

        private const string Pending = "{\"message\":\"Scan is not finished yet\",\"status\":404}";

        private static Task<List<ConnectorRecord>> Run(UrlScan urlscan, string operation, Dictionary<string, string> parameters, string endpoint = "") =>
            Contexts.DrainAsync(new UrlScanIoConnector(TimeSpan.Zero), Contexts.Extract(new StubEgress(urlscan), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Found(int from, int count) => string.Join(",", Enumerable.Range(from, count).Select(n => $"{{\"_id\":\"0196c4c4-9a2b-7000-8000-{n:D12}\",\"task\":{{\"url\":\"https://k{n}.example/\"}},\"page\":{{\"domain\":\"k{n}.example\"}},\"sort\":[{1792000000000 - n},\"0196c4c4-9a2b-7000-8000-{n:D12}\"]}}"));

        [Fact]
        public void It_declares_itself_as_n8ns_urlscan_node()
        {
            IConnector connector = new UrlScanIoConnector();
            Assert.Equal(("urlscanio", "urlscan.io", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "scan", "result", "search" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_urlscans_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "")
            {
                var urlscan = new UrlScan(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(urlscan, operation, parameters, endpoint));
                Assert.Empty(urlscan.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.Equal("urlscan.io has no operation 'verdict' - scan, result or search.", await Refused("verdict", new()));
            Assert.Equal("Give the URL to scan - https://kassa.example/login (url).", await Refused("scan", new()));
            Assert.Equal("'kassa.example/login' is not a web address - https://kassa.example/login (url).", await Refused("scan", new() { ["url"] = "kassa.example/login" }));
            Assert.Equal("'javascript:alert(1)' is not a web address - https://kassa.example/login (url).", await Refused("scan", new() { ["url"] = "javascript:alert(1)" }));
            Assert.Equal("'https://ada:secret@kassa.example/' is not a web address - https://kassa.example/login (url).", await Refused("scan", new() { ["url"] = "https://ada:secret@kassa.example/" }));
            Assert.Equal("'secret' is not public, unlisted or private (visibility).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["visibility"] = "secret" }));
            Assert.Equal("A scan has at most 10 tags - these are 11 (tags).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["tags"] = string.Join(",", Enumerable.Range(1, 11).Select(n => "t" + n)) }));
            Assert.Equal("A tag is at most 100 characters, on one line (tags).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["tags"] = new string('k', 101) }));
            Assert.Equal("A user agent is at most 512 characters, on one line (custom_agent).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["custom_agent"] = "Kassa\nBot" }));
            Assert.Equal("'kafe.example' is not a web address - https://kassa.example/login (referer).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["referer"] = "kafe.example" }));
            Assert.Equal("'ja' is not true or false (override_safety).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["override_safety"] = "ja" }));
            Assert.Equal("'snart' is not true or false (wait).", await Refused("scan", new() { ["url"] = "https://kassa.example/", ["wait"] = "snart" }));
            Assert.Equal("Give the scan - its id, the uuid a scan answered (scan).", await Refused("result", new()));
            Assert.Equal("'../1' is not a scan's id - 0196c4c4-9a2b-7000-8000-0123456789ab (scan).", await Refused("result", new() { ["scan"] = "../1" }));
            Assert.Equal("A query is at most 2000 characters, on one line (query).", await Refused("search", new() { ["query"] = "domain:kafe.example\nAND page.ip:1" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("search", new() { ["max"] = "0" }));
            Assert.Equal("'https://urlscan.io/api/v2' is not urlscan.io's address - its own, https://urlscan.io.", await Refused("search", new(), "https://urlscan.io/api/v2"));
            Assert.Equal("The urlscan.io connection has no API key.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new UrlScanIoConnector(), Contexts.Extract(new StubEgress(new UrlScan(_ => (HttpStatusCode.OK, "{}"))), "", "search", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_scan_is_submitted_private_by_default_and_waited_for_when_asked()
        {
            var asked = 0;
            var urlscan = new UrlScan(sent => sent.Method == "POST" ? (HttpStatusCode.OK, Submitted) : ++asked < 3 ? (HttpStatusCode.NotFound, Pending) : (HttpStatusCode.OK, Result));
            var submitted = Assert.Single(await Run(urlscan, "scan", new() { ["url"] = "https://kassa.example/login" })).Fields;
            Assert.Equal((Uuid, "Submission successful", "private"), ((string?)submitted["id"], (string?)submitted["message"], (string?)submitted["visibility"]));
            var post = Assert.Single(urlscan.Requests);
            Assert.Equal(("POST", Api + "/scan/", "urlscan-kafe-key", "{\"url\":\"https://kassa.example/login\",\"visibility\":\"private\"}"), (post.Method, post.Url, post.Key, post.Body));

            urlscan.Requests.Clear();
            var result = Assert.Single(await Run(urlscan, "scan", new()
            {
                ["url"] = "https://kassa.example/login", ["visibility"] = "Unlisted", ["tags"] = "phishing, kafé, phishing", ["custom_agent"] = "KassaBot/1.0",
                ["referer"] = "https://kafe.example/", ["override_safety"] = "true", ["wait"] = "true",
            })).Fields;
            Assert.Equal("{\"url\":\"https://kassa.example/login\",\"visibility\":\"unlisted\",\"tags\":[\"phishing\",\"kafé\"],\"customagent\":\"KassaBot/1.0\",\"referer\":\"https://kafe.example/\",\"overrideSafety\":true}", urlscan.Requests[0].Body);
            Assert.Equal(new[] { "POST /scan/", $"GET /result/{Uuid}/", $"GET /result/{Uuid}/", $"GET /result/{Uuid}/" }, urlscan.Requests.Select(sent => sent.Method + " " + sent.Url[Api.Length..]));
            Assert.Equal((Uuid, true, 100L, "https://kassa.example/login", "kassa.example", "203.0.113.7", "SE"), ((string?)result["id"], (bool?)result["malicious"], (long?)result["score"], (string?)result["page_url"], (string?)result["page_domain"], (string?)result["page_ip"], (string?)result["page_country"]));
            Assert.Equal(($"https://urlscan.io/screenshots/{Uuid}.png", "{\"ips\":[\"203.0.113.7\"]}"), ((string?)result["screenshot"], (string?)result["lists"])); // urlscan.io's own sections as they are
        }

        [Fact]
        public async Task A_result_is_asked_again_a_few_times_and_then_said()
        {
            var urlscan = new UrlScan(_ => (HttpStatusCode.NotFound, Pending));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(urlscan, "result", new() { ["scan"] = Uuid.ToUpperInvariant() }));
            Assert.Equal($"urlscan.io had no result for the scan {Uuid} after 5 asks - Scan is not finished yet: read it later with 'result', or check its id.", refused.Message);
            Assert.Equal(5, urlscan.Requests.Count);
            var waited = new UrlScan(sent => sent.Method == "POST" ? (HttpStatusCode.OK, Submitted) : (HttpStatusCode.NotFound, Pending));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(waited, "scan", new() { ["url"] = "https://kassa.example/", ["wait"] = "true" }));
            Assert.Equal(31, waited.Requests.Count); // the scan, and thirty asks for its result
        }

        [Fact]
        public async Task Scans_are_searched_page_by_page_by_urlscans_cursor_up_to_the_most()
        {
            var urlscan = new UrlScan(sent => (HttpStatusCode.OK, sent.Url.Contains("search_after=", StringComparison.Ordinal)
                ? "{\"results\":[" + Found(101, 100) + "],\"total\":200,\"has_more\":false}"
                : "{\"results\":[" + Found(1, 100) + "],\"total\":250,\"has_more\":true}"));
            var found = await Run(urlscan, "search", new() { ["query"] = "domain:kafe.example AND page.country:SE", ["max"] = "150" });
            Assert.Equal(150, found.Count);
            Assert.Equal(new[]
            {
                Api + "/search/?size=100&q=domain%3Akafe.example%20AND%20page.country%3ASE",
                Api + "/search/?size=50&q=domain%3Akafe.example%20AND%20page.country%3ASE&search_after=1791999999900%2C0196c4c4-9a2b-7000-8000-000000000100",
            }, urlscan.Requests.Select(sent => sent.Url));
            Assert.Equal(("0196c4c4-9a2b-7000-8000-000000000001", "{\"domain\":\"k1.example\"}", false), ((string?)found[0].Fields["id"], (string?)found[0].Fields["page"], found[0].Fields.ContainsKey("_id")));

            var last = new UrlScan(_ => (HttpStatusCode.OK, "{\"results\":[" + Found(1, 2) + "],\"total\":2,\"has_more\":false}"));
            Assert.Equal(2, (await Run(last, "search", new())).Count);
            Assert.Equal(Api + "/search/?size=50", Assert.Single(last.Requests).Url); // no query: the key's own recent scans
        }

        [Fact]
        public async Task A_failure_is_urlscans_message_and_description()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "scan") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new UrlScan(_ => (status, body)), operation, new() { ["url"] = "https://kassa.example/", ["scan"] = Uuid }))).Message;

            Assert.Equal("urlscan.io answered HTTP 400: DNS Error - Could not resolve domain - The domain kassa.example could not be resolved to a valid IPv4/IPv6 address.",
                await Failed(HttpStatusCode.BadRequest, "{\"message\":\"DNS Error - Could not resolve domain\",\"description\":\"The domain kassa.example could not be resolved to a valid IPv4/IPv6 address.\",\"status\":400}"));
            Assert.Equal("urlscan.io answered HTTP 401: No API key supplied. - check the API key.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"No API key supplied\",\"status\":401}"));
            Assert.Equal("urlscan.io answered HTTP 500.", await Failed(HttpStatusCode.InternalServerError, "<html>fel</html>"));
            Assert.Equal("urlscan.io answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));
            Assert.Equal("urlscan.io answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]"));
            Assert.Equal("urlscan.io answered without the scan's id - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"message\":\"Submission successful\"}"));
            Assert.Equal("urlscan.io answered without its results - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{}", "search"));
            Assert.Equal("urlscan.io answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]", "search"));
        }

        [Fact]
        public async Task A_test_reads_the_keys_quotas()
        {
            var urlscan = new UrlScan(_ => (HttpStatusCode.OK, "{\"source\":\"api-key\",\"limits\":{\"public\":{\"day\":{\"limit\":5000,\"used\":12,\"remaining\":4988}},\"private\":{\"day\":{\"limit\":50,\"used\":0,\"remaining\":50}}}}"));
            var tested = await new UrlScanIoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(urlscan), "https://urlscan.io/api/v1/", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "urlscan.io answers - 4988 public scans left today."), (tested.Success, tested.Message));
            Assert.Equal(("GET", Api + "/user/quotas/", "urlscan-kafe-key"), (urlscan.Requests.Single().Method, urlscan.Requests.Single().Url, urlscan.Requests.Single().Key));
            async Task<ConnectionTestResult> Test(HttpStatusCode status, string body) =>
                await new UrlScanIoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new UrlScan(_ => (status, body))), "", new Dictionary<string, string>(), Credentials));

            var nothing = await Test(HttpStatusCode.OK, "{}");
            Assert.Equal((false, "urlscan.io answered without the key's quotas - check the address: a sign-in page or a proxy answers so."), (nothing.Success, nothing.Message));
            Assert.False((await Test(HttpStatusCode.OK, "<html>Sign in</html>")).Success);
            Assert.False((await Test(HttpStatusCode.Unauthorized, "{\"message\":\"No API key supplied\"}")).Success);
        }
    }
}
