#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Ghost;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Ghost (n8n's Ghost node: a site's posts read, created, updated and deleted): every call the site's Admin API with
    /// a fresh token - the key's id, five minutes long, for the Admin API, signed with the key's secret - and the API version;
    /// posts and pages page by page, the page size fixed, with a filter, an order, what to include, the formats and the fields;
    /// a post got by its id or its slug; created as HTML or Lexical with a status and other fields; updated with Ghost's last
    /// change read first; deleted; the tags and members; the site's address from its admin's too; what could go wrong refused
    /// before anything is sent; Ghost's word on a failure; a test naming the posts seen.
    /// </summary>
    public sealed class GhostConnectorTests
    {
        private const string Site = "https://blog.example.test";

        private const string Api = Site + "/ghost/api/admin";

        private const string KeyId = "6489c1b2f1a2b3c4d5e6f708";

        private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string PostId = "64f0c0ffee0123456789abcd";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = KeyId + ":" + Secret };

        private sealed class Ghost : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Version, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Accept-Version", out var version) ? version.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Ghost ghost, string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new GhostConnector(), Contexts.Extract(new StubEgress(ghost), endpoint, operation, parameters, settings: new Dictionary<string, string>(), credentials: credentials ?? Credentials));

        private static string Posts(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"" + i.ToString("x24") + "\",\"title\":\"Café " + i + "\",\"featured\":true,\"reading_time\":3,\"tags\":[{\"name\":\"Café\"}],\"excerpt\":null}")) + "]";

        // A stand-in answers under the resource asked for, as Ghost does.
        private static string Kind(string url) => url.Contains("/pages/", StringComparison.Ordinal) ? "pages" : "posts";

        private static byte[] Decode(string text)
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
        }

        // Ghost's token as Ghost checks it: its key's id, five minutes from now, for the Admin API, signed with the secret.
        private static void AssertToken(string? authorization)
        {
            Assert.StartsWith("Ghost ", authorization, StringComparison.Ordinal);
            var parts = authorization!["Ghost ".Length..].Split('.');
            Assert.Equal(3, parts.Length);
            Assert.Equal("{\"alg\":\"HS256\",\"kid\":\"" + KeyId + "\",\"typ\":\"JWT\"}", Encoding.UTF8.GetString(Decode(parts[0])));
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Decode(parts[1])))!;
            var issued = payload["iat"]!.GetValue<long>();
            Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued, 0, 60);
            Assert.Equal((issued + 300, "/admin/"), (payload["exp"]!.GetValue<long>(), payload["aud"]!.GetValue<string>()));
            var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromHexString(Secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(signature, parts[2]);
        }

        [Fact]
        public async Task Posts_are_read_page_by_page_filtered_and_got_by_id_or_slug()
        {
            var ghost = new Ghost
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=2", StringComparison.Ordinal)
                    ? "{\"" + Kind(url) + "\":" + Posts(101, 50) + ",\"meta\":{\"pagination\":{\"page\":2,\"limit\":100,\"pages\":2,\"total\":150,\"next\":null,\"prev\":1}}}"
                    : "{\"" + Kind(url) + "\":" + Posts(1, 100) + ",\"meta\":{\"pagination\":{\"page\":1,\"limit\":100,\"pages\":2,\"total\":150,\"next\":2,\"prev\":null}}}"),
            };

            var posts = await Run(ghost, GhostConnector.PostsOperation, new() { ["filter"] = "status:published+tag:news", ["order"] = "published_at desc", ["include"] = "tags, authors", ["formats"] = "html,plaintext", ["fields"] = "id,title,url" });
            Assert.Equal(150, posts.Count); // a thousand at most by default
            Assert.Equal(new[]
            {
                $"{Api}/posts/?filter=status%3Apublished%2Btag%3Anews&order=published_at%20desc&include=tags,authors&formats=html,plaintext&fields=id,title,url&limit=100&page=1",
                $"{Api}/posts/?filter=status%3Apublished%2Btag%3Anews&order=published_at%20desc&include=tags,authors&formats=html,plaintext&fields=id,title,url&limit=100&page=2",
            }, ghost.Requests.Select(request => request.Url)); // Ghost's next page none the end
            Assert.All(ghost.Requests, request => { AssertToken(request.Authorization); Assert.Equal("v5.0", request.Version); });
            var first = posts[0].Fields;
            Assert.Equal((1.ToString("x24"), "Café 1", true, 3L, "[{\"name\":\"Café\"}]"),((string?)first["id"], (string?)first["title"], (bool?)first["featured"], (long?)first["reading_time"], (string?)first["tags"]));
            Assert.Null(first["excerpt"]);

            Assert.Equal(120, (await Run(ghost, GhostConnector.PostsOperation, new() { ["max"] = "120" })).Count);
            Assert.Equal($"{Api}/posts/?limit=100&page=2", ghost.Requests[^1].Url); // the page size fixed, no more than asked
            Assert.Equal(30, (await Run(ghost, GhostConnector.PagesOperation, new() { ["max"] = "30" }, endpoint: "https://blog.example.test/ghost/#/dashboard")).Count);
            Assert.Equal($"{Api}/pages/?limit=30&page=1", ghost.Requests[^1].Url); // the admin's address the site's, a page as small as asked

            ghost.Answer = (_, _) => (HttpStatusCode.OK, "{\"posts\":[{\"id\":\"" + PostId + "\",\"slug\":\"café-news\",\"title\":\"Café news\"}]}");
            var got = Assert.Single(await Run(ghost, GhostConnector.GetPostOperation, new() { ["id"] = PostId, ["slug"] = "ignored", ["include"] = "tags", ["formats"] = "lexical", ["filter"] = "x:1", ["fields"] = "id", ["order"] = "title asc" })).Fields;
            Assert.Equal($"{Api}/posts/{PostId}/?include=tags&formats=lexical", ghost.Requests[^1].Url); // no filter, order or fields on a get
            Assert.Equal("Café news", (string?)got["title"]);
            await Run(ghost, GhostConnector.GetPageOperation, new() { ["slug"] = "café-news" }, endpoint: "https://example.test/blog/");
            Assert.Equal("https://example.test/blog/ghost/api/admin/pages/slug/caf%C3%A9-news/", ghost.Requests[^1].Url); // a site under a path
        }

        [Fact]
        public async Task A_post_is_created_updated_and_deleted_and_the_tags_and_members_read()
        {
            var ghost = new Ghost
            {
                Answer = (method, url) => (method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK,
                    method == HttpMethod.Delete ? ""
                    : url.Contains("fields=id,updated_at", StringComparison.Ordinal) ? "{\"" + Kind(url) + "\":[{\"id\":\"" + PostId + "\",\"updated_at\":\"2026-10-13T08:00:00.000Z\"}]}"
                    : url.Contains("/tags/", StringComparison.Ordinal) ? "{\"tags\":[{\"id\":\"t1\",\"name\":\"News\",\"slug\":\"news\"}],\"meta\":{\"pagination\":{\"next\":null}}}"
                    : url.Contains("/members/", StringComparison.Ordinal) ? "{\"members\":[{\"id\":\"m1\",\"email\":\"ada@example.com\",\"status\":\"paid\"}],\"meta\":{\"pagination\":{\"next\":null}}}"
                    : "{\"posts\":[{\"id\":\"" + PostId + "\",\"title\":\"Café\",\"status\":\"draft\"}]}"),
            };
            (string, string, string?) Last() => (ghost.Requests[^1].Method.Method, ghost.Requests[^1].Url, ghost.Requests[^1].Body);

            var made = Assert.Single(await Run(ghost, GhostConnector.CreatePostOperation, new() { ["title"] = "Café", ["html"] = "<p>Hej</p>", ["status"] = "Published", ["fields"] = "{\"tags\": [{\"name\": \"News\"}], \"title\": \"overridden\"}" })).Fields;
            Assert.Equal(("POST", $"{Api}/posts/?source=html", (string?)"{\"posts\":[{\"tags\":[{\"name\":\"News\"}],\"title\":\"Café\",\"status\":\"published\",\"html\":\"<p>Hej</p>\"}]}"), Last()); // the title given over the fields'
            Assert.Equal((PostId, "draft"), ((string?)made["id"], (string?)made["status"]));
            await Run(ghost, GhostConnector.CreatePageOperation, new() { ["fields"] = "{\"title\": \"About\"}", ["lexical"] = "{\"root\": {\"children\": []}}" });
            Assert.Equal(("POST", $"{Api}/pages/", (string?)"{\"pages\":[{\"title\":\"About\",\"lexical\":\"{\\\"root\\\": {\\\"children\\\": []}}\"}]}"), Last()); // a title among the fields, Lexical as Ghost keeps it - its JSON as text

            var changed = Assert.Single(await Run(ghost, GhostConnector.UpdatePostOperation, new() { ["id"] = PostId, ["status"] = "scheduled", ["fields"] = "{\"published_at\": \"2026-10-14T08:00:00Z\"}" })).Fields;
            Assert.Equal(new[]
            {
                $"GET {Api}/posts/{PostId}/?fields=id,updated_at ",
                $"PUT {Api}/posts/{PostId}/ {{\"posts\":[{{\"published_at\":\"2026-10-14T08:00:00Z\",\"status\":\"scheduled\",\"updated_at\":\"2026-10-13T08:00:00.000Z\"}}]}}",
            }, ghost.Requests.TakeLast(2).Select(request => $"{request.Method} {request.Url} {request.Body}")); // Ghost's last change read first and carried
            Assert.Equal(PostId, (string?)changed["id"]);
            await Run(ghost, GhostConnector.UpdatePageOperation, new() { ["id"] = PostId, ["html"] = "<p>New</p>" });
            Assert.Equal($"{Api}/pages/{PostId}/?source=html", ghost.Requests[^1].Url);

            var gone = Assert.Single(await Run(ghost, GhostConnector.DeletePostOperation, new() { ["id"] = PostId })).Fields;
            Assert.Equal(("DELETE", $"{Api}/posts/{PostId}/", (string?)null), Last());
            Assert.Equal((PostId, true), ((string?)gone["id"], (bool?)gone["deleted"]));
            await Run(ghost, GhostConnector.DeletePageOperation, new() { ["id"] = PostId });
            Assert.Equal($"{Api}/pages/{PostId}/", ghost.Requests[^1].Url);

            Assert.Equal("News", (string?)Assert.Single(await Run(ghost, GhostConnector.TagsOperation, new() { ["filter"] = "visibility:public", ["include"] = "count.posts" })).Fields["name"]);
            Assert.Equal($"{Api}/tags/?filter=visibility%3Apublic&limit=100&page=1", ghost.Requests[^1].Url); // no include on tags
            Assert.Equal("paid", (string?)Assert.Single(await Run(ghost, GhostConnector.MembersOperation, new() { ["filter"] = "status:paid", ["order"] = "created_at desc" })).Fields["status"]);
            Assert.Equal($"{Api}/members/?filter=status%3Apaid&limit=100&page=1", ghost.Requests[^1].Url); // no order on members
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_ghosts_word_on_a_failure()
        {
            var ghost = new Ghost();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ghost, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Ghost has no operation 'users' - posts, get_post,", await Refused("users", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GhostConnector.PostsOperation, new() { ["max"] = "0" }));
            Assert.Equal("The filter is one line of up to 2,000 characters.", await Refused(GhostConnector.TagsOperation, new() { ["filter"] = new string('x', 2001) }));
            Assert.Equal("The filter is one line of up to 2,000 characters.", await Refused(GhostConnector.MembersOperation, new() { ["filter"] = "status:paid\u0001" }));
            Assert.Equal("'title; drop' is not an order - published_at desc.", await Refused(GhostConnector.PagesOperation, new() { ["order"] = "title; drop" }));
            Assert.Equal("'tags,../x' is not a list of fields (include).", await Refused(GhostConnector.PostsOperation, new() { ["include"] = "tags,../x" }));
            Assert.Equal("'html,pdf' is not a list of html, lexical, mobiledoc, plaintext (formats).", await Refused(GhostConnector.GetPostOperation, new() { ["id"] = PostId, ["formats"] = "html,pdf" }));
            Assert.Equal("'id,Title' is not a list of fields (fields).", await Refused(GhostConnector.PostsOperation, new() { ["fields"] = "id,Title" }));
            Assert.Equal("Give the post - its id or its slug.", await Refused(GhostConnector.GetPostOperation, new()));
            Assert.Equal("'42' is not a page's id - 24 hexadecimal digits.", await Refused(GhostConnector.GetPageOperation, new() { ["id"] = "42" }));
            Assert.Equal("'news/../x' is not a post's slug.", await Refused(GhostConnector.GetPostOperation, new() { ["slug"] = "news/../x" }));
            Assert.Equal("'..' is not a page's slug.", await Refused(GhostConnector.GetPageOperation, new() { ["slug"] = ".." }));
            Assert.Equal("Give the post - its id.", await Refused(GhostConnector.UpdatePostOperation, new() { ["title"] = "x" }));
            Assert.Equal("Give the page - its id.", await Refused(GhostConnector.DeletePageOperation, new()));
            Assert.Equal("'64F0C0FFEE0123456789ABCD' is not a post's id - 24 hexadecimal digits.", await Refused(GhostConnector.DeletePostOperation, new() { ["id"] = "64F0C0FFEE0123456789ABCD" }));
            Assert.Equal("Give the post's title.", await Refused(GhostConnector.CreatePostOperation, new() { ["html"] = "<p>x</p>" }));
            Assert.Equal("'live' is not draft, published or scheduled (status).", await Refused(GhostConnector.CreatePostOperation, new() { ["title"] = "x", ["status"] = "live" }));
            Assert.Equal("Give the content as HTML or as Lexical, not both.", await Refused(GhostConnector.CreatePageOperation, new() { ["title"] = "x", ["html"] = "<p>x</p>", ["lexical"] = "{}" }));
            Assert.Equal("The Lexical content is not a JSON object - Ghost's Lexical document.", await Refused(GhostConnector.CreatePostOperation, new() { ["title"] = "x", ["lexical"] = "[1]" }));
            Assert.Equal("The other fields are not a JSON object - {\"tags\": [{\"name\": \"News\"}]}.", await Refused(GhostConnector.CreatePostOperation, new() { ["title"] = "x", ["fields"] = "tags=News" }));
            Assert.Equal("Give what to change - a title, the content, a status or other fields.", await Refused(GhostConnector.UpdatePageOperation, new() { ["id"] = PostId }));
            Assert.Equal("'blog.example.test' is not a Ghost site's address - https://example.ghost.io.", await Refused(GhostConnector.PostsOperation, new(), endpoint: "blog.example.test"));
            Assert.Equal("'ftp://blog.example.test' is not a Ghost site's address - https://example.ghost.io.", await Refused(GhostConnector.PostsOperation, new(), endpoint: "ftp://blog.example.test"));
            Assert.Equal("The Ghost connection has no Admin API key.", await Refused(GhostConnector.PostsOperation, new(), credentials: new()));
            Assert.Equal("The Ghost connection's key is not an Admin API key - its id, a colon and its secret, as Ghost's integration shows it.", await Refused(GhostConnector.PostsOperation, new(), credentials: new() { ["api-key"] = "22444f78447824223cefc48062" }));
            Assert.Equal("The Ghost connection's key is not an Admin API key - its id, a colon and its secret, as Ghost's integration shows it.", await Refused(GhostConnector.PostsOperation, new(), credentials: new() { ["api-key"] = KeyId + ":not-hex" }));
            Assert.Equal("The Ghost connection's key is not an Admin API key - its id, a colon and its secret, as Ghost's integration shows it.", await Refused(GhostConnector.PostsOperation, new(), credentials: new() { ["api-key"] = "admin:" + Secret }));
            Assert.Empty(ghost.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Ghost { Answer = (_, _) => (status, answer) }, GhostConnector.PostsOperation, new()))).Message;
            Assert.Equal("HTTP 422: Validation error, cannot save post. Value in [posts.title] cannot be blank.", await Failed((HttpStatusCode)422, "{\"errors\":[{\"message\":\"Validation error, cannot save post.\",\"context\":\"Value in [posts.title] cannot be blank.\",\"type\":\"ValidationError\"}]}"));
            Assert.Equal("HTTP 404: Resource not found", await Failed(HttpStatusCode.NotFound, "{\"errors\":[{\"message\":\"Resource not found\",\"context\":\"\",\"type\":\"NotFoundError\"}]}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errors\":[{\"message\":\"" + new string('e', 400) + "\"}]}")); // a long word cut
            Assert.Equal("Ghost answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no posts
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));

            var stale = new Ghost { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("updated_at", StringComparison.Ordinal) ? "{\"posts\":[]}" : "{}") };
            Assert.Equal("Ghost did not say when the post last changed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stale, GhostConnector.UpdatePostOperation, new() { ["id"] = PostId, ["title"] = "x" }))).Message);
            Assert.Single(stale.Requests); // no edit without it
        }

        [Fact]
        public async Task A_test_names_the_posts_seen()
        {
            var ghost = new Ghost { Answer = (_, _) => (HttpStatusCode.OK, "{\"posts\":[{\"id\":\"" + PostId + "\"}],\"meta\":{\"pagination\":{\"total\":42}}}") };
            var tested = await new GhostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(ghost), Site + "/", credentials: Credentials));
            Assert.Equal((true, "Ghost: 42 posts seen."), (tested.Success, tested.Message));
            Assert.Equal($"{Api}/posts/?limit=1&fields=id", ghost.Requests[0].Url);
            AssertToken(ghost.Requests[0].Authorization);
            var one = new Ghost { Answer = (_, _) => (HttpStatusCode.OK, "{\"posts\":[],\"meta\":{\"pagination\":{\"total\":1}}}") };
            Assert.Equal("Ghost: one post seen.", (await new GhostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(one), Site, credentials: Credentials))).Message);
            var refused = new Ghost { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Invalid token: invalid signature\",\"type\":\"UnauthorizedError\"}]}") };
            var denied = await new GhostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), Site, credentials: Credentials));
            Assert.Equal((false, "HTTP 401: Invalid token: invalid signature"), (denied.Success, denied.Message));
            var nowhere = await new GhostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), "", credentials: Credentials));
            Assert.Equal((false, "'' is not a Ghost site's address - https://example.ghost.io."), (nowhere.Success, nowhere.Message));
            Assert.Equal((AuthenticationType.ApiKey, "ghost"), (new GhostConnector().AuthType, new GhostConnector().ConnectorType));
            Assert.Equal(12, new GhostConnector().Operations.Count);
        }
    }
}
