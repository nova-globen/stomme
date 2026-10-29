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
using Stomme.Modules.Connectors.LinkedIn;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// LinkedIn (n8n's LinkedIn node: a post made as a member or an organization): the token as a bearer, LinkedIn's
    /// version and Rest.li's protocol on its versioned calls; the signed-in member read; a post made as the member (its URN read
    /// first), another person or an organization - its text with LinkedIn's reserved characters escaped, # left for hashtags; an
    /// article; a reshare; resharing kept off - its URN and address said; a post's text changed and a post deleted by Rest.li's
    /// methods; an author's posts page by page, newest first; what could go wrong refused before anything is sent; LinkedIn's
    /// errors in its words; a test naming the member.
    /// </summary>
    public sealed class LinkedInConnectorTests
    {
        private const string Api = "https://api.linkedin.example.test";

        private const string Post = "urn:li:share:6844785523593134080";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "AQV-token-1" };

        private sealed class LinkedIn : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Version, string? Protocol, string? Method2, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, HttpResponseMessage> Answer { get; set; } = (_, _, _) => Json("{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), Header("LinkedIn-Version"), Header("X-Restli-Protocol-Version"), Header("X-RestLi-Method"), body));
                return Answer(request.Method, url, body);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Made(string id)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("", Encoding.UTF8, "application/json") };
            response.Headers.Add("x-restli-id", id);
            return response;
        }

        private const string Me = """{"sub":"782bbtaQ","name":"Ada Lovelace","given_name":"Ada","family_name":"Lovelace","email":"ada@example.com","email_verified":true,"picture":"https://media.example/ada.jpg","locale":{"country":"SE","language":"sv"}}""";

        private static ConnectorExtractContext Context(LinkedIn linkedIn, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(linkedIn), Api, operation, parameters, settings: settings ?? new Dictionary<string, string>(), credentials: Credentials);

        private static string PostJson(int n) =>
            "{\"id\":\"urn:li:share:" + (6844785523593134000L + n) + "\",\"author\":\"urn:li:organization:2414183\",\"commentary\":\"Fika \\\\(daily\\\\) {hashtag|\\\\#|fika}\",\"visibility\":\"PUBLIC\",\"lifecycleState\":\"PUBLISHED\","
            + "\"createdAt\":1634817394721,\"publishedAt\":1634817394721,\"lastModifiedAt\":1634817395768,\"content\":{\"article\":{\"source\":\"https://example.com/fika\",\"title\":\"Fika\"}},\"reshareContext\":{\"parent\":\"urn:li:share:1\"}}";

        [Fact]
        public async Task A_post_is_made_as_the_member_a_person_or_an_organization_with_its_text_as_written()
        {
            var linkedIn = new LinkedIn { Answer = (method, url, _) => url.EndsWith("/v2/userinfo", StringComparison.Ordinal) ? Json(Me) : Made(Post) };
            var made = Assert.Single(await Contexts.DrainAsync(new LinkedInConnector(), Context(linkedIn, LinkedInConnector.CreatePostOperation, new() { ["text"] = "Fika (daily) at 3 - *new* [menu] {café} <b> @all | ~_ \\ #fika" })));

            Assert.Equal(2, linkedIn.Requests.Count);
            Assert.Equal(("GET", $"{Api}/v2/userinfo", "Bearer AQV-token-1", (string?)null, (string?)null), (linkedIn.Requests[0].Method.Method, linkedIn.Requests[0].Url, linkedIn.Requests[0].Authorization, linkedIn.Requests[0].Version, linkedIn.Requests[0].Protocol)); // the member's URN read first
            var created = linkedIn.Requests[1];
            Assert.Equal(("POST", $"{Api}/rest/posts", "Bearer AQV-token-1", "202604", "2.0.0", (string?)null), (created.Method.Method, created.Url, created.Authorization, created.Version, created.Protocol, created.Method2));
            Assert.Equal("{\"author\":\"urn:li:person:782bbtaQ\",\"commentary\":\"Fika \\\\(daily\\\\) at 3 - \\\\*new\\\\* \\\\[menu\\\\] \\\\{café\\\\} \\\\<b\\\\> \\\\@all \\\\| \\\\~\\\\_ \\\\\\\\ #fika\",\"visibility\":\"PUBLIC\","
                + "\"distribution\":{\"feedDistribution\":\"MAIN_FEED\",\"targetEntities\":[],\"thirdPartyDistributionChannels\":[]},\"lifecycleState\":\"PUBLISHED\",\"isReshareDisabledByAuthor\":false}", created.Body); // reserved characters escaped, # left for hashtags
            Assert.Equal((Post, "urn:li:person:782bbtaQ", "PUBLIC", $"https://www.linkedin.com/feed/update/{Post}/"), ((string?)made.Fields["id"], (string?)made.Fields["author"], (string?)made.Fields["visibility"], (string?)made.Fields["url"]));

            var asOrg = new LinkedIn { Answer = (_, _, _) => Made("urn:li:ugcPost:99") };
            await Contexts.DrainAsync(new LinkedInConnector(), Context(asOrg, LinkedInConnector.CreatePostOperation, new()
            {
                ["author"] = "organization:2414183", ["text"] = "Line one\nLine two", ["article_url"] = "https://example.com/fika", ["article_title"] = "Fika & bullar", ["article_description"] = "Why we stop at three", ["reshare_disabled"] = "TRUE",
            }, new() { ["version"] = "202609" }));
            var org = Assert.Single(asOrg.Requests); // no member to look up
            Assert.Equal("202609", org.Version);
            Assert.Equal("{\"author\":\"urn:li:organization:2414183\",\"commentary\":\"Line one\\nLine two\",\"visibility\":\"PUBLIC\",\"distribution\":{\"feedDistribution\":\"MAIN_FEED\",\"targetEntities\":[],\"thirdPartyDistributionChannels\":[]},"
                + "\"lifecycleState\":\"PUBLISHED\",\"isReshareDisabledByAuthor\":true,\"content\":{\"article\":{\"source\":\"https://example.com/fika\",\"title\":\"Fika & bullar\",\"description\":\"Why we stop at three\"}}}", org.Body);

            var person = new LinkedIn { Answer = (_, _, _) => Made(Post) };
            await Contexts.DrainAsync(new LinkedInConnector(), Context(person, LinkedInConnector.CreatePostOperation, new() { ["author"] = "urn:li:person:Ab-12_cd", ["text"] = "Hi", ["visibility"] = "connections", ["reshare"] = "urn:li:ugcPost:12345" }));
            Assert.Equal("{\"author\":\"urn:li:person:Ab-12_cd\",\"commentary\":\"Hi\",\"visibility\":\"CONNECTIONS\",\"distribution\":{\"feedDistribution\":\"MAIN_FEED\",\"targetEntities\":[],\"thirdPartyDistributionChannels\":[]},"
                + "\"lifecycleState\":\"PUBLISHED\",\"isReshareDisabledByAuthor\":false,\"reshareContext\":{\"parent\":\"urn:li:ugcPost:12345\"}}", Assert.Single(person.Requests).Body);
            await Contexts.DrainAsync(new LinkedInConnector(), Context(person, LinkedInConnector.CreatePostOperation, new() { ["author"] = "person:Ab-12_cd", ["text"] = "Hi" }));
            Assert.StartsWith("{\"author\":\"urn:li:person:Ab-12_cd\",", person.Requests[^1].Body, StringComparison.Ordinal);

            var unnamed = new LinkedIn { Answer = (_, url, _) => url.EndsWith("/v2/userinfo", StringComparison.Ordinal) ? Json(Me) : Json("", HttpStatusCode.Created) };
            Assert.Equal("LinkedIn made the post but did not name it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(unnamed, LinkedInConnector.CreatePostOperation, new() { ["text"] = "Hi" })))).Message);
            var nameless = new LinkedIn { Answer = (_, _, _) => Json("""{"name":"Ada"}""") };
            Assert.Equal("LinkedIn did not name the signed-in member - is the openid scope granted?", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(nameless, LinkedInConnector.CreatePostOperation, new() { ["text"] = "Hi" })))).Message);
            Assert.Single(nameless.Requests); // nothing posted
            var malformed = new LinkedIn { Answer = (_, _, _) => Json("""{"sub":"a b/c"}""") };
            Assert.Equal("LinkedIn did not name the signed-in member - is the openid scope granted?", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(malformed, LinkedInConnector.CreatePostOperation, new() { ["text"] = "Hi" })))).Message);
        }

        [Fact]
        public async Task The_member_is_read_a_post_changed_and_deleted_and_an_authors_posts_read_page_by_page()
        {
            var linkedIn = new LinkedIn
            {
                Answer = (method, url, _) =>
                    url.EndsWith("/v2/userinfo", StringComparison.Ordinal) ? Json(Me)
                    : method == HttpMethod.Get ? Json("{\"elements\":[" + string.Join(",", Enumerable.Range(url.Contains("start=0", StringComparison.Ordinal) ? 1 : 101, url.Contains("start=0", StringComparison.Ordinal) ? 100 : 30).Select(PostJson)) + "],"
                        + "\"paging\":{\"start\":0,\"count\":100,\"links\":" + (url.Contains("start=0", StringComparison.Ordinal) ? "[{\"rel\":\"next\",\"href\":\"/rest/posts?start=100\"}]" : "[]") + "}}")
                    : Json("", HttpStatusCode.NoContent),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new LinkedInConnector(), Context(linkedIn, operation, parameters));

            var me = Assert.Single(await Run(LinkedInConnector.ProfileOperation, new())).Fields;
            Assert.Equal(("782bbtaQ", "urn:li:person:782bbtaQ", "Ada Lovelace", "Ada", "Lovelace", "ada@example.com", true, "https://media.example/ada.jpg", "sv"),
                ((string?)me["id"], (string?)me["urn"], (string?)me["name"], (string?)me["given_name"], (string?)me["family_name"], (string?)me["email"], (bool?)me["email_verified"], (string?)me["picture"], (string?)me["locale"]));

            await Run(LinkedInConnector.UpdatePostOperation, new() { ["post"] = Post, ["text"] = " Fika (now)\n" });
            var changed = linkedIn.Requests[^1];
            Assert.Equal(("POST", $"{Api}/rest/posts/urn%3Ali%3Ashare%3A6844785523593134080", "PARTIAL_UPDATE", "202604", "{\"patch\":{\"$set\":{\"commentary\":\" Fika \\\\(now\\\\)\\n\"}}}"), (changed.Method.Method, changed.Url, changed.Method2, changed.Version, changed.Body));
            var gone = Assert.Single(await Run(LinkedInConnector.DeletePostOperation, new() { ["post"] = " urn:li:ugcPost:12345 " }));
            Assert.Equal(("DELETE", $"{Api}/rest/posts/urn%3Ali%3AugcPost%3A12345", "DELETE", (string?)null), (linkedIn.Requests[^1].Method.Method, linkedIn.Requests[^1].Url, linkedIn.Requests[^1].Method2, linkedIn.Requests[^1].Body));
            Assert.Equal(("urn:li:ugcPost:12345", true), ((string?)gone.Fields["post"], (bool?)gone.Fields["done"]));

            var before = linkedIn.Requests.Count;
            var posts = await Run(LinkedInConnector.PostsOperation, new() { ["author"] = "organization:2414183" });
            Assert.Equal(130, posts.Count);
            Assert.Equal(new[]
            {
                ($"{Api}/rest/posts?author=urn%3Ali%3Aorganization%3A2414183&q=author&count=100&start=0&sortBy=LAST_MODIFIED", (string?)"FINDER"),
                ($"{Api}/rest/posts?author=urn%3Ali%3Aorganization%3A2414183&q=author&count=100&start=100&sortBy=LAST_MODIFIED", "FINDER"),
            }, linkedIn.Requests.Skip(before).Select(request => (request.Url, request.Method2))); // no next link: the end
            var first = posts[0].Fields;
            Assert.Equal(("urn:li:share:6844785523593134001", "urn:li:organization:2414183", "Fika \\(daily\\) {hashtag|\\#|fika}", "PUBLIC", "PUBLISHED", "2021-10-21T11:56:34.721Z", "2021-10-21T11:56:35.768Z"),
                ((string?)first["id"], (string?)first["author"], (string?)first["text"], (string?)first["visibility"], (string?)first["state"], (string?)first["created_at"], (string?)first["modified_at"]));
            Assert.Equal(("https://example.com/fika", "Fika", "urn:li:share:1", "https://www.linkedin.com/feed/update/urn:li:share:6844785523593134001/"), ((string?)first["article_url"], (string?)first["article_title"], (string?)first["reshare_of"], (string?)first["url"]));

            before = linkedIn.Requests.Count;
            Assert.Equal(130, (await Run(LinkedInConnector.PostsOperation, new() { ["sort_by"] = "created", ["max"] = "150" })).Count); // no next link: the end
            Assert.Equal(new[] { $"{Api}/v2/userinfo", $"{Api}/rest/posts?author=urn%3Ali%3Aperson%3A782bbtaQ&q=author&count=100&start=0&sortBy=CREATED", $"{Api}/rest/posts?author=urn%3Ali%3Aperson%3A782bbtaQ&q=author&count=50&start=100&sortBy=CREATED" },
                linkedIn.Requests.Skip(before).Select(request => request.Url)); // the member's own by default; a page no bigger than what is left
            Assert.Equal(120, (await Run(LinkedInConnector.PostsOperation, new() { ["author"] = "organization:2414183", ["max"] = "120" })).Count); // no more than asked, though LinkedIn gave more
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_linkedins_errors_are_its_words()
        {
            var linkedIn = new LinkedIn();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(linkedIn, operation, parameters, settings)))).Message;

            Assert.Equal("Give the post's text.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "  " }));
            Assert.Equal("The text is longer than 3,000 characters.", await Refused(LinkedInConnector.UpdatePostOperation, new() { ["post"] = Post, ["text"] = new string('t', 3001) }));
            Assert.Equal("'acme' is not an author - me, urn:li:person:..., urn:li:organization:123 or organization:123.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["author"] = "acme", ["text"] = "x" }));
            Assert.Equal("'organization:acme' is not an author - me, urn:li:person:..., urn:li:organization:123 or organization:123.", await Refused(LinkedInConnector.PostsOperation, new() { ["author"] = "organization:acme" }));
            Assert.Equal("'urn:li:person:a b' is not an author - me, urn:li:person:..., urn:li:organization:123 or organization:123.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["author"] = "urn:li:person:a b", ["text"] = "x" }));
            Assert.Equal("'person:Ab cd12' is not an author - me, urn:li:person:..., urn:li:organization:123 or organization:123.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["author"] = "person:Ab cd12", ["text"] = "x" }));
            Assert.Equal("'friends' is not a visibility - PUBLIC, CONNECTIONS or LOGGED_IN.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["visibility"] = "friends" }));
            Assert.Equal("An organization posts publicly - not CONNECTIONS.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["author"] = "organization:1", ["text"] = "x", ["visibility"] = "connections" }));
            Assert.Equal("Give the article's address.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_title"] = "Fika" }));
            Assert.Equal("'example.com/fika' is not an article's address - https://example.com/post.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "example.com/fika", ["article_title"] = "Fika" }));
            Assert.Equal("'ftp://example.com/fika' is not an article's address - https://example.com/post.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "ftp://example.com/fika", ["article_title"] = "Fika" }));
            Assert.Equal("Give the article's title.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "https://example.com/fika" }));
            Assert.Equal("The article's title has a line break - one line only.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "https://example.com/fika", ["article_title"] = "a\nb" }));
            Assert.Equal("The article's title is longer than 400 characters.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "https://example.com/fika", ["article_title"] = new string('t', 401) }));
            Assert.Equal("The article's description is longer than 4,086 characters.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "https://example.com/fika", ["article_title"] = "t", ["article_description"] = new string('d', 4087) }));
            Assert.Equal("Share an article or reshare a post - not both.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["article_url"] = "https://example.com/fika", ["article_title"] = "t", ["reshare"] = Post }));
            Assert.Equal("'share:1' is not a post's URN - urn:li:share:6844785523593134080.", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["reshare"] = "share:1" }));
            Assert.Equal("'yes' is not true or false (no resharing).", await Refused(LinkedInConnector.CreatePostOperation, new() { ["text"] = "x", ["reshare_disabled"] = "yes" }));
            Assert.Equal("Give the post - its URN.", await Refused(LinkedInConnector.DeletePostOperation, new()));
            Assert.Equal("'urn:li:activity:1' is not a post's URN - urn:li:share:6844785523593134080.", await Refused(LinkedInConnector.UpdatePostOperation, new() { ["post"] = "urn:li:activity:1", ["text"] = "x" }));
            Assert.Equal("'oldest' is not a sort - LAST_MODIFIED or CREATED.", await Refused(LinkedInConnector.PostsOperation, new() { ["sort_by"] = "oldest" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(LinkedInConnector.PostsOperation, new() { ["max"] = "0" }));
            Assert.Equal("'2026-04' is not an API version - YYYYMM, 202604.", await Refused(LinkedInConnector.ProfileOperation, new(), new() { ["version"] = "2026-04" }));
            Assert.Equal("'202613' is not an API version - YYYYMM, 202604.", await Refused(LinkedInConnector.ProfileOperation, new(), new() { ["version"] = "202613" }));
            Assert.StartsWith("LinkedIn has no operation 'comments' - profile, create_post,", await Refused("comments", new()));
            Assert.Empty(linkedIn.Requests); // nothing sent

            var denied = new LinkedIn { Answer = (_, _, _) => Json("""{"status":403,"serviceErrorCode":100,"code":"ACCESS_DENIED","message":"Not enough permissions to access: posts.CREATE"}""", HttpStatusCode.Forbidden) };
            Assert.Equal("HTTP 403 ACCESS_DENIED: Not enough permissions to access: posts.CREATE", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(denied, LinkedInConnector.CreatePostOperation, new() { ["author"] = "organization:1", ["text"] = "x" })))).Message);
            var silent = new LinkedIn { Answer = (_, _, _) => Json("", HttpStatusCode.Unauthorized) };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LinkedInConnector(), Context(silent, LinkedInConnector.ProfileOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_member()
        {
            var linkedIn = new LinkedIn { Answer = (_, _, _) => Json(Me) };
            var tested = await new LinkedInConnector().TestConnectionAsync(Contexts.Test(new StubEgress(linkedIn), Api, settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/v2/userinfo", "Bearer AQV-token-1"), (linkedIn.Requests[0].Url, linkedIn.Requests[0].Authorization));
            var noMail = new LinkedIn { Answer = (_, _, _) => Json("""{"sub":"782bbtaQ","name":"Ada Lovelace"}""") };
            Assert.Equal("Signed in as Ada Lovelace.", (await new LinkedInConnector().TestConnectionAsync(Contexts.Test(new StubEgress(noMail), Api, settings: new Dictionary<string, string>(), credentials: Credentials))).Message);
            var keyless = await new LinkedInConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new LinkedIn()), Api, settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The LinkedIn connection has no access token."), (keyless.Success, keyless.Message));
            Assert.Equal((AuthenticationType.Bearer, "linkedin"), (new LinkedInConnector().AuthType, new LinkedInConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing }, new LinkedInConnector().Categories);
            Assert.Equal(5, new LinkedInConnector().Operations.Count);
        }
    }
}
