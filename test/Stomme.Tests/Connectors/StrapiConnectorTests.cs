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
using Stomme.Modules.Connectors.Strapi;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Strapi (n8n's Strapi node: a content type's entries created, got, read, updated and deleted): every call a Strapi's
    /// REST API under /api with the API token as a bearer token, its admin's address too; entries page by page, the page size
    /// fixed and no bigger than asked, Strapi's page count the end, with its filters as its query string reads them, a sort, a
    /// status (Strapi 4's publication state), a locale, what to populate and the fields; Strapi 4's attributes taken out of their
    /// wrapper; an entry got, created, updated and deleted, a single type read and written; what could go wrong refused before
    /// anything is sent; Strapi's word on a failure; a test saying the token answers and naming the locales.
    /// </summary>
    public sealed class StrapiConnectorTests
    {
        private const string Site = "https://cms.example.test";

        private const string Api = Site + "/api";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "strapi-token-1" };

        private sealed class Strapi : HttpMessageHandler
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

        private static Task<List<ConnectorRecord>> Run(Strapi strapi, string operation, Dictionary<string, string> parameters, string? version = "5", string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new StrapiConnector(), Contexts.Extract(new StubEgress(strapi), endpoint, operation, parameters, settings: version is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["version"] = version }, credentials: credentials ?? Credentials));

        private static string Articles(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":" + i + ",\"documentId\":\"doc" + i + "\",\"title\":\"Café " + i + "\",\"views\":12,\"featured\":true,\"cover\":{\"url\":\"/uploads/café.jpg\"},\"summary\":null}")) + "]";

        [Fact]
        public async Task Entries_are_read_page_by_page_filtered_and_strapi_4s_unwrapped()
        {
            var strapi = new Strapi
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("pagination[page]=2", StringComparison.Ordinal)
                    ? "{\"data\":" + Articles(101, 50) + ",\"meta\":{\"pagination\":{\"page\":2,\"pageSize\":100,\"pageCount\":2,\"total\":150}}}"
                    : "{\"data\":" + Articles(1, 100) + ",\"meta\":{\"pagination\":{\"page\":1,\"pageSize\":100,\"pageCount\":2,\"total\":150}}}"),
            };

            var entries = await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "articles", ["filters"] = "{\"title\": {\"$containsi\": \"café\"}, \"$or\": [{\"views\": {\"$gt\": 10}}, {\"featured\": {\"$eq\": true}}]}", ["sort"] = "publishedAt:desc, title", ["status"] = "Draft", ["locale"] = "sv", ["populate"] = "author, cover", ["columns"] = "title, slug" });
            Assert.Equal(150, entries.Count); // a thousand at most by default
            const string Query = "filters[title][$containsi]=caf%C3%A9&filters[$or][0][views][$gt]=10&filters[$or][1][featured][$eq]=true&sort=publishedAt%3Adesc%2Ctitle&status=draft&locale=sv&populate=author%2Ccover&fields[0]=title&fields[1]=slug";
            Assert.Equal(new[] { $"{Api}/articles?{Query}&pagination[page]=1&pagination[pageSize]=100", $"{Api}/articles?{Query}&pagination[page]=2&pagination[pageSize]=100" }, strapi.Requests.Select(request => request.Url)); // Strapi's page count the end
            Assert.All(strapi.Requests, request => Assert.Equal("Bearer strapi-token-1", request.Authorization));
            var first = entries[0].Fields;
            Assert.Equal((1L, "doc1", "Café 1", 12L, true, "{\"url\":\"/uploads/café.jpg\"}"), ((long?)first["id"], (string?)first["documentId"], (string?)first["title"], (long?)first["views"], (bool?)first["featured"], (string?)first["cover"]));
            Assert.Null(first["summary"]);

            Assert.Equal(30, (await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "articles", ["max"] = "30" }, endpoint: "https://cms.example.test/admin/content-manager/collection-types")).Count);
            Assert.Equal($"{Api}/articles?pagination[page]=1&pagination[pageSize]=30", strapi.Requests[^1].Url); // the admin's address the Strapi's; a page as small as asked

            strapi.Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":[{\"id\":7,\"attributes\":{\"title\":\"Café\",\"author\":{\"data\":{\"id\":3}},\"publishedAt\":null}}],\"meta\":{\"pagination\":{\"page\":1,\"pageCount\":1}}}");
            var old = Assert.Single(await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "blog-posts", ["status"] = "draft" }, version: "4")).Fields;
            Assert.Equal($"{Api}/blog-posts?publicationState=preview&pagination[page]=1&pagination[pageSize]=100", strapi.Requests[^1].Url); // Strapi 4's own word for drafts
            Assert.Equal((7L, "Café", "{\"data\":{\"id\":3}}"), ((long?)old["id"], (string?)old["title"], (string?)old["author"])); // its attributes out of their wrapper
            Assert.Null(old["publishedAt"]);
            await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "blog-posts", ["status"] = "published" }, version: "v4");
            Assert.Equal($"{Api}/blog-posts?publicationState=live&pagination[page]=1&pagination[pageSize]=100", strapi.Requests[^1].Url);
            await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "blog-posts" }, version: "v5");
            Assert.Equal($"{Api}/blog-posts?pagination[page]=1&pagination[pageSize]=100", strapi.Requests[^1].Url); // a page count of one the end
            await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "blog-posts", ["status"] = "draft" }, version: null, endpoint: Site + "/api/");
            Assert.Equal($"{Api}/blog-posts?status=draft&pagination[page]=1&pagination[pageSize]=100", strapi.Requests[^1].Url); // Strapi 5 when not said; its API's address the Strapi's

            strapi.Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":[{\"id\":1}]}");
            var before = strapi.Requests.Count;
            Assert.Single(await Run(strapi, StrapiConnector.EntriesOperation, new() { ["content_type"] = "blog-posts" }));
            Assert.Equal(before + 1, strapi.Requests.Count); // no page count the end too
        }

        [Fact]
        public async Task An_entry_is_got_created_updated_and_deleted_and_a_single_type_read_and_written()
        {
            var strapi = new Strapi { Answer = (method, _) => (method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, method == HttpMethod.Delete ? "" : "{\"data\":{\"id\":42,\"documentId\":\"abc123\",\"title\":\"Café\"},\"meta\":{}}") };
            (string, string, string?) Last() => (strapi.Requests[^1].Method.Method, strapi.Requests[^1].Url, strapi.Requests[^1].Body);

            var got = Assert.Single(await Run(strapi, StrapiConnector.GetEntryOperation, new() { ["content_type"] = "articles", ["id"] = "abc123", ["status"] = "published", ["locale"] = "en-GB", ["populate"] = "*", ["filters"] = "{\"title\": {\"$eq\": \"x\"}}", ["sort"] = "title", ["columns"] = "title" })).Fields;
            Assert.Equal(("GET", $"{Api}/articles/abc123?status=published&locale=en-GB&populate=%2A", (string?)null), Last()); // no filters, sort or fields on a get
            Assert.Equal(("abc123", "Café"), ((string?)got["documentId"], (string?)got["title"]));

            var made = Assert.Single(await Run(strapi, StrapiConnector.CreateEntryOperation, new() { ["content_type"] = "articles", ["fields"] = "{\"title\": \"Café\", \"author\": 3}", ["status"] = "draft", ["locale"] = "sv", ["populate"] = "author" })).Fields;
            Assert.Equal(("POST", $"{Api}/articles?status=draft&locale=sv", (string?)"{\"data\":{\"title\":\"Café\",\"author\":3}}"), Last()); // nothing to populate on a create
            Assert.Equal(42L, (long?)made["id"]);
            await Run(strapi, StrapiConnector.UpdateEntryOperation, new() { ["content_type"] = "articles", ["id"] = "abc123", ["fields"] = "{\"title\": \"Kafé\"}", ["status"] = "draft" });
            Assert.Equal(("PUT", $"{Api}/articles/abc123", (string?)"{\"data\":{\"title\":\"Kafé\"}}"), Last()); // no status on an update
            var gone = Assert.Single(await Run(strapi, StrapiConnector.DeleteEntryOperation, new() { ["content_type"] = "articles", ["id"] = "abc123", ["locale"] = "sv" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/articles/abc123?locale=sv", (string?)null), Last());
            Assert.Equal(("articles", "abc123", true), ((string?)gone["content_type"], (string?)gone["id"], (bool?)gone["deleted"]));

            Assert.Equal("Café", (string?)Assert.Single(await Run(strapi, StrapiConnector.SingleOperation, new() { ["content_type"] = "homepage", ["locale"] = "sv", ["populate"] = "hero" })).Fields["title"]);
            Assert.Equal(("GET", $"{Api}/homepage?locale=sv&populate=hero", (string?)null), Last());
            await Run(strapi, StrapiConnector.UpdateSingleOperation, new() { ["content_type"] = "homepage", ["fields"] = "{\"title\": \"Välkommen\"}" });
            Assert.Equal(("PUT", $"{Api}/homepage", (string?)"{\"data\":{\"title\":\"Välkommen\"}}"), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_strapis_word_on_a_failure()
        {
            var strapi = new Strapi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string version = "5", string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(strapi, operation, parameters, version, endpoint, credentials))).Message;
            Dictionary<string, string> At(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["content_type"] = "articles" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("Strapi has no operation 'users' - entries, get_entry,", await Refused("users", At()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(StrapiConnector.EntriesOperation, At(("max", "0"))));
            Assert.Equal("Give the content type - its API id.", await Refused(StrapiConnector.EntriesOperation, new()));
            Assert.Equal("'Articles' is not a content type's API id - lowercase letters, digits and -.", await Refused(StrapiConnector.EntriesOperation, new() { ["content_type"] = "Articles" }));
            Assert.Equal("'../admin' is not a content type's API id - lowercase letters, digits and -.", await Refused(StrapiConnector.SingleOperation, new() { ["content_type"] = "../admin" }));
            Assert.Equal("Give the entry - its id.", await Refused(StrapiConnector.GetEntryOperation, At()));
            Assert.Equal("'a/b' is not an entry's id - letters and digits.", await Refused(StrapiConnector.DeleteEntryOperation, At(("id", "a/b"))));
            Assert.Equal("The filters are not a JSON object - {\"title\": {\"$contains\": \"news\"}}.", await Refused(StrapiConnector.EntriesOperation, At(("filters", "[1]"))));
            Assert.Equal("'title&x' is not a field or an operator ($eq, $contains) in the filters.", await Refused(StrapiConnector.EntriesOperation, At(("filters", "{\"title&x\": {\"$eq\": 1}}"))));
            Assert.Equal("'title;drop' is not a sort - publishedAt:desc,title.", await Refused(StrapiConnector.EntriesOperation, At(("sort", "title;drop"))));
            Assert.Equal("'title:DESC' is not a sort - publishedAt:desc,title.", await Refused(StrapiConnector.EntriesOperation, At(("sort", "title:DESC")))); // Strapi's own word, lowercase
            Assert.Equal("'live' is not published or draft (status).", await Refused(StrapiConnector.EntriesOperation, At(("status", "live"))));
            Assert.Equal("'sv_SE' is not a locale's code - sv, en-GB.", await Refused(StrapiConnector.EntriesOperation, At(("locale", "sv_SE"))));
            Assert.Equal("'author;cover' is not a list of relations - author,cover - or *.", await Refused(StrapiConnector.GetEntryOperation, At(("id", "abc"), ("populate", "author;cover"))));
            Assert.Equal("'title,*' is not a list of fields - title,slug.", await Refused(StrapiConnector.EntriesOperation, At(("columns", "title,*"))));
            Assert.Equal("Give the fields - a JSON object.", await Refused(StrapiConnector.CreateEntryOperation, At()));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"title\": \"Hello\"}.", await Refused(StrapiConnector.UpdateEntryOperation, At(("id", "abc"), ("fields", "{}"))));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"title\": \"Hello\"}.", await Refused(StrapiConnector.UpdateSingleOperation, new() { ["content_type"] = "homepage", ["fields"] = "title=x" }));
            Assert.Equal("'3' is not a Strapi version this connector speaks - 5 or 4.", await Refused(StrapiConnector.EntriesOperation, At(), version: "3"));
            Assert.Equal("'cms.example.test' is not a Strapi's address - https://cms.example.com.", await Refused(StrapiConnector.EntriesOperation, At(), endpoint: "cms.example.test"));
            Assert.Equal("The Strapi connection has no API token.", await Refused(StrapiConnector.EntriesOperation, At(), credentials: new()));
            Assert.Empty(strapi.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Strapi { Answer = (_, _) => (status, answer) }, StrapiConnector.EntriesOperation, At()))).Message;
            Assert.Equal("HTTP 404: Not Found", await Failed(HttpStatusCode.NotFound, "{\"data\":null,\"error\":{\"status\":404,\"name\":\"NotFoundError\",\"message\":\"Not Found\",\"details\":{}}}"));
            Assert.Equal("HTTP 403: Forbidden", await Failed(HttpStatusCode.Forbidden, "{\"data\":null,\"error\":{\"status\":403,\"name\":\"ForbiddenError\",\"message\":\"Forbidden\"}}")); // a refusal a failure outside the test
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"" + new string('e', 400) + "\"}}")); // a long word cut
            Assert.Equal("Strapi answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no entries
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_says_the_token_answers_and_names_the_locales()
        {
            Task<ConnectionTestResult> Test(Strapi strapi, string version = "5", string endpoint = Site) =>
                new StrapiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(strapi), endpoint, new Dictionary<string, string> { ["version"] = version }, Credentials));
            var two = new Strapi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":1,\"code\":\"sv\"},{\"id\":2,\"code\":\"en\"}]") };
            var tested = await Test(two);
            Assert.Equal((true, "Strapi 5: the token answers - 2 locales."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/i18n/locales", "Bearer strapi-token-1"), (two.Requests[0].Url, two.Requests[0].Authorization));
            Assert.Equal("Strapi 4: the token answers - 1 locale.", (await Test(new Strapi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":1,\"code\":\"sv\"}]") }, "4")).Message);
            var forbidden = await Test(new Strapi { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"Forbidden\"}}") });
            Assert.Equal((true, "Strapi 5: the token answers."), (forbidden.Success, forbidden.Message)); // a token it knows, the locales not its to read
            var none = await Test(new Strapi { Answer = (_, _) => (HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Not Found\"}}") });
            Assert.Equal((true, "Strapi 5: the token answers."), (none.Success, none.Message)); // a Strapi without locales
            var empty = await Test(new Strapi { Answer = (_, _) => (HttpStatusCode.OK, "[]") });
            Assert.Equal("Strapi 5: the token answers.", empty.Message);
            var refused = await Test(new Strapi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"data\":null,\"error\":{\"status\":401,\"name\":\"UnauthorizedError\",\"message\":\"Missing or invalid credentials\"}}") });
            Assert.Equal((false, "HTTP 401: Missing or invalid credentials"), (refused.Success, refused.Message));
            var nowhere = await Test(new Strapi(), endpoint: "");
            Assert.Equal((false, "'' is not a Strapi's address - https://cms.example.com."), (nowhere.Success, nowhere.Message));
            Assert.Equal((AuthenticationType.Bearer, "strapi"), (new StrapiConnector().AuthType, new StrapiConnector().ConnectorType));
            Assert.Equal(7, new StrapiConnector().Operations.Count);
        }
    }
}
