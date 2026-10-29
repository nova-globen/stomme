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
using Stomme.Modules.Connectors.FacebookGraph;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Facebook Graph API (n8n's Facebook Graph API node: a node or its edge read, posted to and deleted): every
    /// call the Graph - at the step's version, the connection's or none, or the address given - with the token as a bearer
    /// token, never in an address; a node read whole with its fields, an edge's list page by page by the Graph's cursor, the
    /// limit fixed and no bigger than asked, an empty page or no next the end, with more of the Graph's parameters; raw, the
    /// answer as one record; a post with its fields and a delete; each record's fields as themselves or their JSON; what could
    /// go wrong refused before anything is sent; the Graph's type, code and message on a failure; a test naming whose the
    /// token is.
    /// </summary>
    public sealed class FacebookGraphConnectorTests
    {
        private const string Graph = "https://graph.facebook.com";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "graph-token-1" };

        private sealed class Meta : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Meta meta, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new FacebookGraphConnector(), Contexts.Extract(new StubEgress(meta), endpoint, operation, parameters, settings: settings ?? new Dictionary<string, string>(), credentials: credentials ?? Credentials));

        private static string Posts(int from, int count, string after, string next) =>
            "{\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"123_" + i + "\",\"message\":\"Fika " + i + " på café\",\"likes\":{\"summary\":{\"total_count\":" + i + "}},\"is_hidden\":false,\"shares\":null}")) + "],\"paging\":{\"cursors\":{\"before\":\"b\",\"after\":\"" + after + "\"},\"next\":\"" + next + "\"}}";

        [Fact]
        public async Task A_node_is_read_whole_and_an_edges_list_page_by_page_with_the_graphs_parameters()
        {
            var meta = new Meta
            {
                Answer = (_, url) => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("after=c2", StringComparison.Ordinal) => Posts(101, 50, "c3", ""),
                    _ when url.Contains("/feed", StringComparison.Ordinal) => Posts(1, 100, "c2/ä", "https://graph.facebook.com/next"),
                    _ => "{\"id\":\"123\",\"name\":\"Kafé\",\"fan_count\":42,\"is_verified\":true,\"location\":{\"city\":\"Malmö\"}}",
                }),
            };

            var page = Assert.Single(await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "123", ["fields"] = "id, name, fan_count, location{city}", ["max"] = "5" }, new() { ["version"] = "V21.0" })).Fields;
            Assert.Equal(($"{Graph}/v21.0/123?fields=id%2Cname%2Cfan_count%2Clocation%7Bcity%7D", "Bearer graph-token-1"), (meta.Requests[^1].Url, meta.Requests[^1].Authorization)); // a node whole, no limit; the token never in the address
            Assert.Equal(("123", "Kafé", 42L, true, "{\"city\":\"Malmö\"}"), ((string?)page["id"], (string?)page["name"], (long?)page["fan_count"], (bool?)page["is_verified"], (string?)page["location"]));

            meta.Requests.Clear();
            var posts = await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "123", ["edge"] = "feed", ["fields"] = "message", ["query"] = "{\"since\": \"2026-10-01\", \"show_expired\": true, \"filter\": \"app_2392950137\", \"ids\": [1, 2]}", ["version"] = "v22.0" }, new() { ["version"] = "v21.0" });
            Assert.Equal(150, posts.Count); // a thousand at most by default
            const string Query = "fields=message&since=2026-10-01&show_expired=true&filter=app_2392950137&ids=%5B1%2C2%5D&limit=100";
            Assert.Equal(new[] { $"{Graph}/v22.0/123/feed?{Query}", $"{Graph}/v22.0/123/feed?{Query}&after=c2%2F%C3%A4" }, meta.Requests.Select(request => request.Url)); // the step's version; the Graph's cursor, escaped; no next the end
            var first = posts[0].Fields;
            Assert.Equal(("123_1", "Fika 1 på café", "{\"summary\":{\"total_count\":1}}", false), ((string?)first["id"], (string?)first["message"], (string?)first["likes"], (bool?)first["is_hidden"]));
            Assert.Null(first["shares"]);

            meta.Requests.Clear();
            Assert.Equal(30, (await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "act_123/feed", ["max"] = "30" }, endpoint: "https://graph-video.facebook.com/")).Count);
            Assert.Equal("https://graph-video.facebook.com/act_123/feed?limit=30", Assert.Single(meta.Requests).Url); // no version, another address; a page as small as asked
            meta.Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":[],\"paging\":{\"cursors\":{\"after\":\"c9\"},\"next\":\"https://next\"}}");
            Assert.Empty(await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "me", ["edge"] = "accounts" })); // an empty page the end
            meta.Answer = (_, _) => (HttpStatusCode.OK, Posts(1, 100, "", "https://next"));
            Assert.Equal(100, (await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "me", ["edge"] = "posts" })).Count); // no cursor the end
            meta.Answer = (_, _) => (HttpStatusCode.OK, Posts(1, 2, "c2", "https://next"));
            var raw = Assert.Single(await Run(meta, FacebookGraphConnector.GetOperation, new() { ["node"] = "me", ["edge"] = "posts", ["raw"] = "true" })).Fields;
            Assert.StartsWith("[{\"id\":\"123_1\",\"message\":\"Fika 1 på café\"", (string?)raw["data"]); // the answer as the Graph gives it
            Assert.Equal("{\"cursors\":{\"before\":\"b\",\"after\":\"c2\"},\"next\":\"https://next\"}", (string?)raw["paging"]);
        }

        [Fact]
        public async Task A_post_and_a_delete_are_sent()
        {
            var meta = new Meta { Answer = (method, _) => (HttpStatusCode.OK, method == HttpMethod.Post ? "{\"id\":\"123_9\"}" : "{\"success\":true}") };

            var posted = Assert.Single(await Run(meta, FacebookGraphConnector.PostOperation, new() { ["node"] = "123", ["edge"] = "feed", ["fields"] = "{\"message\": \"Fika på café\", \"published\": false}", ["query"] = "{\"x\": 1}", ["max"] = "1" })).Fields;
            Assert.Equal((HttpMethod.Post, $"{Graph}/123/feed", "{\"message\":\"Fika på café\",\"published\":false}"), (meta.Requests[^1].Method, meta.Requests[^1].Url, meta.Requests[^1].Body)); // no parameters on a post
            Assert.Equal("123_9", (string?)posted["id"]);
            await Run(meta, FacebookGraphConnector.PostOperation, new() { ["node"] = "123_9", ["edge"] = "likes" });
            Assert.Equal((HttpMethod.Post, $"{Graph}/123_9/likes", "{}"), (meta.Requests[^1].Method, meta.Requests[^1].Url, meta.Requests[^1].Body)); // nothing to post but the post itself
            var gone = Assert.Single(await Run(meta, FacebookGraphConnector.DeleteOperation, new() { ["node"] = "123_9", ["fields"] = "{\"x\": 1}" })).Fields;
            Assert.Equal((HttpMethod.Delete, $"{Graph}/123_9", (string?)null), (meta.Requests[^1].Method, meta.Requests[^1].Url, meta.Requests[^1].Body));
            Assert.True((bool?)gone["success"]);
            meta.Answer = (_, _) => (HttpStatusCode.OK, "true");
            Assert.True((bool?)Assert.Single(await Run(meta, FacebookGraphConnector.DeleteOperation, new() { ["node"] = "123_9" })).Fields["success"]); // an older Graph's bare true
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_the_graphs_word_on_a_failure()
        {
            var meta = new Meta();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(meta, operation, parameters, settings, endpoint, credentials))).Message;
            const string Get = FacebookGraphConnector.GetOperation;

            Assert.Equal("The Facebook Graph API has no operation 'put' - get, post or delete.", await Refused("put", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(Get, new() { ["node"] = "me", ["max"] = "0" }));
            Assert.Equal("Give the node - me, or an id.", await Refused(Get, new()));
            Assert.Equal("'me?x=1' is not a node - me, 1234567890, act_123.", await Refused(Get, new() { ["node"] = "me?x=1" }));
            Assert.Equal("'../me' is not a node - me, 1234567890, act_123.", await Refused(FacebookGraphConnector.DeleteOperation, new() { ["node"] = "../me" })); // never a dot segment
            Assert.Equal("'me/../act_1' is not a node - me, 1234567890, act_123.", await Refused(Get, new() { ["node"] = "me/../act_1" }));
            Assert.Equal("'Feed' is not an edge - feed, posts, comments.", await Refused(Get, new() { ["node"] = "me", ["edge"] = "Feed" }));
            Assert.Equal("'id;name' is not a list of fields - id,name,posts.limit(5){message}.", await Refused(Get, new() { ["node"] = "me", ["fields"] = "id;name" }));
            Assert.Equal("The parameters are not a JSON object - {\"since\": \"2026-10-01\"}.", await Refused(Get, new() { ["node"] = "me", ["query"] = "since=2026" }));
            Assert.Equal("'access_token' is not a parameter this step sends - the token travels in its header, and after, before, limit are its paging's.", await Refused(Get, new() { ["node"] = "me", ["query"] = "{\"access_token\": \"x\"}" }));
            Assert.Equal("'Since' is not a parameter this step sends - the token travels in its header, and after, before, limit are its paging's.", await Refused(Get, new() { ["node"] = "me", ["query"] = "{\"Since\": \"x\"}" }));
            Assert.Equal("'limit' is not a parameter this step sends - the token travels in its header, and after, before, limit are its paging's.", await Refused(Get, new() { ["node"] = "me", ["query"] = "{\"limit\": 5}" }));
            Assert.Equal("'since' in the parameters has no value.", await Refused(Get, new() { ["node"] = "me", ["query"] = "{\"since\": null}" }));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"message\": \"Hello\"}.", await Refused(FacebookGraphConnector.PostOperation, new() { ["node"] = "me", ["edge"] = "feed", ["fields"] = "{}" }));
            Assert.Equal("'21' is not a Graph version - v21.0.", await Refused(Get, new() { ["node"] = "me" }, new() { ["version"] = "21" }));
            Assert.Equal("'v21' is not a Graph version - v21.0.", await Refused(Get, new() { ["node"] = "me", ["version"] = "v21" }));
            Assert.Equal("The Facebook Graph API connection has no access token.", await Refused(Get, new() { ["node"] = "me" }, credentials: new()));
            Assert.Equal("The endpoint 'graph.facebook.com' is not an absolute http(s) URL.", await Refused(Get, new() { ["node"] = "me" }, endpoint: "graph.facebook.com"));
            Assert.Empty(meta.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Meta { Answer = (_, _) => (status, answer) }, Get, new() { ["node"] = "me" }))).Message;
            Assert.Equal("HTTP 400 (OAuthException 190): Error validating access token: Session has expired.", await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Error validating access token: Session has expired.\",\"type\":\"OAuthException\",\"code\":190,\"error_subcode\":463,\"fbtrace_id\":\"Abc\"}}"));
            Assert.Equal("HTTP 403 (10): Permission denied", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"Permission denied\",\"code\":10}}"));
            Assert.Equal("HTTP 400 (GraphMethodException): Unsupported get request.", await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported get request.\",\"type\":\"GraphMethodException\"}}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"" + new string('e', 400) + "\"}}")); // a long word cut
            Assert.Equal("The Facebook Graph API answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Meta meta, Dictionary<string, string>? settings = null) =>
                new FacebookGraphConnector().TestConnectionAsync(Contexts.Test(new StubEgress(meta), "", settings, Credentials));
            var page = new Meta { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"123\",\"name\":\"Kafé\"}") };
            var tested = await Test(page, new() { ["version"] = "v21.0" });
            Assert.Equal((true, "Facebook Graph API v21.0: the token is Kafé's (123)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Graph}/v21.0/me?fields=id,name", "Bearer graph-token-1"), (page.Requests[0].Url, page.Requests[0].Authorization));
            Assert.Equal("Facebook Graph API: the token is a user's.", (await Test(new Meta())).Message);
            var refused = await Test(new Meta { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid OAuth access token.\",\"type\":\"OAuthException\",\"code\":190}}") });
            Assert.Equal((false, "HTTP 400 (OAuthException 190): Invalid OAuth access token."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "facebookgraph"), (new FacebookGraphConnector().AuthType, new FacebookGraphConnector().ConnectorType));
            Assert.Equal(3, new FacebookGraphConnector().Operations.Count);
        }
    }
}
