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
using Stomme.Modules.Connectors.Contentful;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Contentful (n8n's Contentful node: a space's entries, assets, content types and locales read): every call
    /// Contentful's Delivery API - its Preview API when the connection says so, another address when given - with the token as a
    /// bearer token, never in the address; a list page by page by skip, the limit fixed and no bigger than asked, Contentful's
    /// total the end, with a content type, its search parameters, the fields to select, an order, a full-text search and a
    /// locale; an entry or an asset its id, content type, dates and locale, then its fields, or raw; an entry, an asset and a
    /// content type got in an environment; the space and its locales; what could go wrong refused before anything is sent;
    /// Contentful's error id and word on a failure; a test naming the space and its environment's locales.
    /// </summary>
    public sealed class ContentfulConnectorTests
    {
        private const string Delivery = "https://cdn.contentful.com/spaces/shop1/environments";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "contentful-token-1" };

        private sealed class Contentful : HttpMessageHandler
        {
            public List<(string Url, string? Authorization)> Requests { get; } = new();

            public Func<string, (HttpStatusCode, string)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((url, request.Headers.Authorization?.ToString()));
                var (status, answer) = Answer(url);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
            }
        }

        private static Dictionary<string, string> Settings(string space = "shop1", string? environment = null, string? api = null)
        {
            var settings = new Dictionary<string, string> { ["space"] = space };
            if (environment is not null)
            {
                settings["environment"] = environment;
            }

            if (api is not null)
            {
                settings["api"] = api;
            }

            return settings;
        }

        private static Task<List<ConnectorRecord>> Run(Contentful contentful, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new ContentfulConnector(), Contexts.Extract(new StubEgress(contentful), endpoint, operation, parameters, settings: settings ?? Settings(), credentials: credentials ?? Credentials));

        private static string Entries(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"metadata\":{\"tags\":[]},\"sys\":{\"id\":\"e" + i + "\",\"type\":\"Entry\",\"createdAt\":\"2026-10-01T08:00:00.000Z\",\"updatedAt\":\"2026-10-02T09:00:00.000Z\",\"locale\":\"sv-SE\",\"contentType\":{\"sys\":{\"type\":\"Link\",\"linkType\":\"ContentType\",\"id\":\"blogPost\"}}},\"fields\":{\"title\":\"Café " + i + "\",\"views\":12,\"rating\":4.5,\"featured\":true,\"author\":{\"sys\":{\"type\":\"Link\",\"linkType\":\"Entry\",\"id\":\"a1\"}},\"tags\":[\"kaffe\",\"bröd\"]}}")) + "]";

        [Fact]
        public async Task Entries_are_read_page_by_page_searched_and_each_its_id_type_dates_and_fields()
        {
            var contentful = new Contentful
            {
                Answer = url => (HttpStatusCode.OK, url.Contains("skip=100", StringComparison.Ordinal)
                    ? "{\"sys\":{\"type\":\"Array\"},\"total\":150,\"skip\":100,\"limit\":100,\"items\":" + Entries(101, 50) + "}"
                    : "{\"sys\":{\"type\":\"Array\"},\"total\":150,\"skip\":0,\"limit\":100,\"items\":" + Entries(1, 100) + "}"),
            };

            var entries = await Run(contentful, ContentfulConnector.EntriesOperation, new() { ["content_type"] = "blogPost", ["filters"] = "{\"fields.title[match]\": \"café\", \"fields.tags[in]\": [\"kaffe\", \"bröd\"], \"fields.views[gte]\": 10, \"fields.featured\": true, \"metadata.tags.sys.id[all]\": \"news\"}", ["select"] = "sys.id, fields.title", ["order"] = "-sys.createdAt, fields.title", ["query"] = "kaffe & bröd", ["locale"] = "sv-SE" });
            Assert.Equal(150, entries.Count); // a thousand at most by default
            const string Query = "content_type=blogPost&fields.title[match]=caf%C3%A9&fields.tags[in]=kaffe%2Cbr%C3%B6d&fields.views[gte]=10&fields.featured=true&metadata.tags.sys.id[all]=news&select=sys.id,fields.title&order=-sys.createdAt,fields.title&query=kaffe%20%26%20br%C3%B6d&locale=sv-SE";
            Assert.Equal(new[] { $"{Delivery}/master/entries?{Query}&skip=0&limit=100", $"{Delivery}/master/entries?{Query}&skip=100&limit=100" }, contentful.Requests.Select(request => request.Url)); // Contentful's total the end
            Assert.All(contentful.Requests, request => Assert.Equal("Bearer contentful-token-1", request.Authorization)); // never in the address
            var first = entries[0].Fields;
            Assert.Equal(("e1", "blogPost", "2026-10-01T08:00:00.000Z", "2026-10-02T09:00:00.000Z", "sv-SE"), ((string?)first["id"], (string?)first["content_type"], (string?)first["created_at"], (string?)first["updated_at"], (string?)first["locale"]));
            Assert.Equal(("Café 1", 12L, 4.5, true), ((string?)first["title"], (long?)first["views"], (double?)first["rating"], (bool?)first["featured"]));
            Assert.Equal(("{\"sys\":{\"type\":\"Link\",\"linkType\":\"Entry\",\"id\":\"a1\"}}", "[\"kaffe\",\"bröd\"]"), ((string?)first["author"], (string?)first["tags"])); // a link and a list as their JSON
            Assert.False(first.ContainsKey("metadata"));

            Assert.Equal(30, (await Run(contentful, ContentfulConnector.EntriesOperation, new() { ["max"] = "30", ["environment"] = "staging" }, Settings(environment: "release-1"))).Count);
            Assert.Equal($"{Delivery}/staging/entries?skip=0&limit=30", contentful.Requests[^1].Url); // a page as small as asked; the step's environment the connection's own
            await Run(contentful, ContentfulConnector.AssetsOperation, new() { ["filters"] = "{\"mimetype_group\": \"image\", \"fields.title\": \"Logo\"}", ["locale"] = "*" }, Settings(environment: "release-1"));
            Assert.Equal($"{Delivery}/release-1/assets?mimetype_group=image&fields.title=Logo&locale=%2A&skip=0&limit=100", contentful.Requests[^2].Url); // the connection's environment
            Assert.Equal($"{Delivery}/release-1/assets?mimetype_group=image&fields.title=Logo&locale=%2A&skip=100&limit=100", contentful.Requests[^1].Url);

            contentful.Answer = url => (HttpStatusCode.OK, url.Contains("skip=0", StringComparison.Ordinal) ? "{\"total\":2,\"items\":[{\"sys\":{\"id\":\"p1\",\"type\":\"Entry\"},\"fields\":{\"id\":\"own\",\"title\":\"Draft\"}}]}" : "{\"total\":2,\"items\":[]}");
            var draft = Assert.Single(await Run(contentful, ContentfulConnector.EntriesOperation, new() { ["content_type"] = "blogPost" }, Settings(api: "Preview")));
            const string Preview = "https://preview.contentful.com/spaces/shop1/environments/master/entries?content_type=blogPost";
            Assert.Equal(new[] { $"{Preview}&skip=0&limit=100", $"{Preview}&skip=1&limit=100" }, contentful.Requests.TakeLast(2).Select(request => request.Url)); // the Preview API; an empty page the end
            Assert.Equal(("own", "Draft"), ((string?)draft.Fields["id"], (string?)draft.Fields["title"])); // a field of the same name wins
            contentful.Answer = _ => (HttpStatusCode.OK, "{\"items\":[{\"sys\":{\"id\":\"p1\"}}]}");
            Assert.Single(await Run(contentful, ContentfulConnector.LocalesOperation, new(), endpoint: "https://cdn.eu.contentful.com/"));
            Assert.Equal("https://cdn.eu.contentful.com/spaces/shop1/environments/master/locales?skip=0&limit=100", contentful.Requests[^1].Url); // another address; no total the end
        }

        [Fact]
        public async Task An_entry_an_asset_and_a_content_type_are_got_raw_or_not_and_the_space_its_types_and_locales_read()
        {
            const string Entry = "{\"metadata\":{\"tags\":[]},\"sys\":{\"id\":\"e1\",\"type\":\"Entry\",\"contentType\":{\"sys\":{\"id\":\"blogPost\"}}},\"fields\":{\"title\":\"Café\",\"body\":null}}";
            const string Type = "{\"sys\":{\"id\":\"blogPost\"},\"name\":\"Blog post\",\"description\":\"A post\",\"displayField\":\"title\",\"fields\":[{\"id\":\"title\",\"name\":\"Titel\",\"type\":\"Symbol\"}]}";
            var contentful = new Contentful
            {
                Answer = url => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("/content_types/", StringComparison.Ordinal) => Type,
                    _ when url.Contains("/content_types?", StringComparison.Ordinal) => "{\"total\":1,\"items\":[" + Type + "]}",
                    _ when url.Contains("/locales", StringComparison.Ordinal) => "{\"total\":2,\"items\":[{\"code\":\"sv-SE\",\"name\":\"Svenska\",\"default\":true,\"fallbackCode\":null},{\"code\":\"en-US\",\"name\":\"English\",\"default\":false,\"fallbackCode\":\"sv-SE\"}]}",
                    _ when url.EndsWith("/spaces/shop1", StringComparison.Ordinal) => "{\"sys\":{\"type\":\"Space\",\"id\":\"shop1\"},\"name\":\"Kafé\",\"locales\":[{\"code\":\"sv-SE\",\"default\":true}]}",
                    _ => Entry,
                }),
            };
            string Last() => contentful.Requests[^1].Url;

            var entry = Assert.Single(await Run(contentful, ContentfulConnector.EntryOperation, new() { ["id"] = "e1", ["locale"] = "sv-SE", ["filters"] = "{\"fields.title\": \"x\"}", ["select"] = "sys.id", ["order"] = "sys.id", ["query"] = "x", ["content_type"] = "blogPost", ["max"] = "5" })).Fields;
            Assert.Equal($"{Delivery}/master/entries/e1?locale=sv-SE", Last()); // no search on a get
            Assert.Equal(("e1", "blogPost", "Café"), ((string?)entry["id"], (string?)entry["content_type"], (string?)entry["title"]));
            Assert.Null(entry["body"]);
            Assert.False(entry.ContainsKey("locale")); // none given, none said
            var raw = Assert.Single(await Run(contentful, ContentfulConnector.EntryOperation, new() { ["id"] = "e1", ["raw"] = "true" })).Fields;
            Assert.Equal(("{\"tags\":[]}", "{\"title\":\"Café\",\"body\":null}"), ((string?)raw["metadata"], (string?)raw["fields"]));
            Assert.Equal("{\"id\":\"e1\",\"type\":\"Entry\",\"contentType\":{\"sys\":{\"id\":\"blogPost\"}}}", (string?)raw["sys"]);

            await Run(contentful, ContentfulConnector.AssetOperation, new() { ["id"] = "logo-1.v2", ["environment"] = "staging" });
            Assert.Equal($"{Delivery}/staging/assets/logo-1.v2", Last());
            var type = Assert.Single(await Run(contentful, ContentfulConnector.ContentTypeOperation, new() { ["id"] = "blogPost", ["locale"] = "sv-SE" })).Fields;
            Assert.Equal($"{Delivery}/master/content_types/blogPost", Last()); // no locale on a content type
            Assert.Equal(("blogPost", "Blog post", "A post", "title", "[{\"id\":\"title\",\"name\":\"Titel\",\"type\":\"Symbol\"}]"), ((string?)type["id"], (string?)type["name"], (string?)type["description"], (string?)type["display_field"], (string?)type["fields"]));
            Assert.Equal("Blog post", (string?)Assert.Single(await Run(contentful, ContentfulConnector.ContentTypesOperation, new() { ["locale"] = "sv-SE", ["query"] = "x" })).Fields["name"]);
            Assert.Equal($"{Delivery}/master/content_types?skip=0&limit=100", Last());

            var locales = await Run(contentful, ContentfulConnector.LocalesOperation, new());
            Assert.Equal(new[] { ("sv-SE", "Svenska", true, (string?)null), ("en-US", "English", false, "sv-SE") }, locales.Select(locale => ((string?)locale.Fields["code"], (string?)locale.Fields["name"], (bool)locale.Fields["default"]!, (string?)locale.Fields["fallback_code"])));
            var space = Assert.Single(await Run(contentful, ContentfulConnector.SpaceOperation, new() { ["environment"] = "staging" })).Fields;
            Assert.Equal("https://cdn.contentful.com/spaces/shop1", Last());
            Assert.Equal(("shop1", "Kafé", "[{\"code\":\"sv-SE\",\"default\":true}]"), ((string?)space["id"], (string?)space["name"], (string?)space["locales"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_contentfuls_word_on_a_failure()
        {
            var contentful = new Contentful();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(contentful, operation, parameters, settings, endpoint, credentials))).Message;
            const string Entries = ContentfulConnector.EntriesOperation;

            Assert.StartsWith("Contentful has no operation 'users' - entries, entry,", await Refused("users", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(Entries, new() { ["max"] = "0" }));
            Assert.Equal("'../x' is not an environment's id.", await Refused(Entries, new() { ["environment"] = "../x" }));
            Assert.Equal("Give the id.", await Refused(ContentfulConnector.ContentTypeOperation, new()));
            Assert.Equal("'e/1' is not an id - letters, digits, _, - and .", await Refused(ContentfulConnector.AssetOperation, new() { ["id"] = "e/1" }));
            Assert.Equal("'blog post' is not a content type's id - blogPost.", await Refused(Entries, new() { ["content_type"] = "blog post" }));
            Assert.Equal("The filters are not a JSON object - {\"fields.title\": \"Hello\"}.", await Refused(Entries, new() { ["filters"] = "fields.title=x" }));
            Assert.Equal("'limit' is not a search parameter - fields.title, sys.id[in], metadata.tags.sys.id[all].", await Refused(Entries, new() { ["filters"] = "{\"limit\": 5}" }));
            Assert.Equal("'fields.title[ne]&x' is not a search parameter - fields.title, sys.id[in], metadata.tags.sys.id[all].", await Refused(ContentfulConnector.AssetsOperation, new() { ["filters"] = "{\"fields.title[ne]&x\": \"a\"}" }));
            Assert.Equal("'fields.author' in the filters is not a text, a number, a flag or a list of them.", await Refused(Entries, new() { ["filters"] = "{\"fields.author\": {\"sys\": 1}}" }));
            Assert.Equal("'fields.tags[in]' in the filters is not a text, a number, a flag or a list of them.", await Refused(Entries, new() { ["filters"] = "{\"fields.tags[in]\": [[1]]}" }));
            Assert.Equal("'sys.id;x' is not a list of fields - sys.id,fields.title.", await Refused(Entries, new() { ["select"] = "sys.id;x" }));
            Assert.Equal("'sys.createdAt:desc' is not an order - -sys.createdAt,fields.title.", await Refused(Entries, new() { ["order"] = "sys.createdAt:desc" }));
            Assert.Equal("'sv_SE' is not a locale's code - sv-SE - or *.", await Refused(ContentfulConnector.EntryOperation, new() { ["id"] = "e1", ["locale"] = "sv_SE" }));
            Assert.Equal("'management' is not one of Contentful's APIs this connector reads - delivery or preview.", await Refused(Entries, new(), Settings(api: "management")));
            Assert.Equal("'Shop 1' is not a space's id - lowercase letters and digits.", await Refused(Entries, new(), Settings(space: "Shop 1")));
            Assert.Equal("The Contentful connection has no space.", await Refused(Entries, new(), new Dictionary<string, string>()));
            Assert.Equal("'a b' is not an environment's id.", await Refused(Entries, new(), Settings(environment: "a b")));
            Assert.Equal("The Contentful connection has no access token.", await Refused(Entries, new(), credentials: new()));
            Assert.Equal("The endpoint 'cdn.contentful.com' is not an absolute http(s) URL.", await Refused(Entries, new(), endpoint: "cdn.contentful.com"));
            Assert.Empty(contentful.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Contentful { Answer = _ => (status, answer) }, Entries, new()))).Message;
            Assert.Equal("HTTP 401 (AccessTokenInvalid): The access token you sent could not be found or is invalid.", await Failed(HttpStatusCode.Unauthorized, "{\"sys\":{\"type\":\"Error\",\"id\":\"AccessTokenInvalid\"},\"message\":\"The access token you sent could not be found or is invalid.\",\"requestId\":\"r1\"}"));
            Assert.Equal("HTTP 400 (InvalidQuery): " + new string('e', 300), await Failed(HttpStatusCode.BadRequest, "{\"sys\":{\"type\":\"Error\",\"id\":\"InvalidQuery\"},\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Contentful answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no entries
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_space_and_its_environments_locales()
        {
            Task<ConnectionTestResult> Test(Contentful contentful, Dictionary<string, string>? settings = null) =>
                new ContentfulConnector().TestConnectionAsync(Contexts.Test(new StubEgress(contentful), "", settings ?? Settings(), Credentials));
            var two = new Contentful { Answer = url => (HttpStatusCode.OK, url.EndsWith("/locales", StringComparison.Ordinal) ? "{\"total\":2,\"items\":[{\"code\":\"sv-SE\"},{\"code\":\"en-US\"}]}" : "{\"sys\":{\"id\":\"shop1\"},\"name\":\"Kafé\"}") };
            var tested = await Test(two);
            Assert.Equal((true, "Contentful's delivery API: the space 'Kafé' answers - 2 locales in master."), (tested.Success, tested.Message));
            Assert.Equal(new[] { "https://cdn.contentful.com/spaces/shop1 Bearer contentful-token-1", $"{Delivery}/master/locales Bearer contentful-token-1" }, two.Requests.Select(request => request.Url + " " + request.Authorization));
            var one = new Contentful { Answer = url => (HttpStatusCode.OK, url.EndsWith("/locales", StringComparison.Ordinal) ? "{\"total\":1,\"items\":[{\"code\":\"sv-SE\"}]}" : "{\"sys\":{\"id\":\"shop1\"}}") };
            Assert.Equal("Contentful's preview API: the space 'shop1' answers - 1 locale in staging.", (await Test(one, Settings(environment: "staging", api: "preview"))).Message);
            Assert.Equal("https://preview.contentful.com/spaces/shop1/environments/staging/locales", one.Requests[^1].Url);
            var refused = await Test(new Contentful { Answer = _ => (HttpStatusCode.Unauthorized, "{\"sys\":{\"type\":\"Error\",\"id\":\"AccessTokenInvalid\"},\"message\":\"The access token you sent could not be found or is invalid.\"}") });
            Assert.Equal((false, "HTTP 401 (AccessTokenInvalid): The access token you sent could not be found or is invalid."), (refused.Success, refused.Message));
            var nowhere = await Test(new Contentful { Answer = url => url.EndsWith("/locales", StringComparison.Ordinal) ? (HttpStatusCode.NotFound, "{\"sys\":{\"type\":\"Error\",\"id\":\"NotFound\"},\"message\":\"The resource could not be found.\"}") : (HttpStatusCode.OK, "{\"name\":\"Kafé\"}") }, Settings(environment: "gone"));
            Assert.Equal((false, "HTTP 404 (NotFound): The resource could not be found."), (nowhere.Success, nowhere.Message)); // an environment the token cannot read
            Assert.Equal((AuthenticationType.Bearer, "contentful"), (new ContentfulConnector().AuthType, new ContentfulConnector().ConnectorType));
            Assert.Equal(8, new ContentfulConnector().Operations.Count);
        }
    }
}
