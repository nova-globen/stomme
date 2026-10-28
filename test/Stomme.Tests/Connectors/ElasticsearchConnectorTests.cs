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
using Stomme.Modules.Connectors.Elasticsearch;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Elasticsearch (n8n's Elasticsearch node: an index's documents searched, got, created, updated and deleted, its
    /// indices kept): every call the cluster's REST API with a user's name and password, or an API key when no user is given; a
    /// search page by page by from and size, the size fixed and no bigger than asked, a short page, its total or Elasticsearch's
    /// window of 10,000 the end, with a search body, a sort, the source fields and a routing; a document its id and index, then
    /// its source, or raw; a count; a document got, created with its id or Elasticsearch's, updated and deleted, refreshed when
    /// asked; the indices with their health and size, an index's settings, mappings and aliases, an index created and deleted;
    /// what could go wrong refused before anything is sent; Elasticsearch's error type and reason on a failure, a missing
    /// document said; a test naming the cluster and its version, OpenSearch's too.
    /// </summary>
    public sealed class ElasticsearchConnectorTests
    {
        private const string Cluster = "https://search.example.test:9200";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "elastic", ["password"] = "elastic-password-1" };

        private sealed class Elastic : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Elastic elastic, string operation, Dictionary<string, string> parameters, string endpoint = Cluster, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new ElasticsearchConnector(), Contexts.Extract(new StubEgress(elastic), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Hits(int from, int count, string total = "{\"value\":150,\"relation\":\"eq\"}") =>
            "{\"took\":3,\"hits\":{\"total\":" + total + ",\"hits\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"_index\":\"posts\",\"_id\":\"p" + i + "\",\"_score\":1.5,\"_source\":{\"title\":\"Café " + i + "\",\"views\":12,\"rating\":4.5,\"published\":true,\"tags\":[\"kaffe\",\"bröd\"],\"author\":{\"name\":\"Ada\"},\"summary\":null}}")) + "]}}";

        [Fact]
        public async Task A_search_is_read_page_by_page_and_each_document_its_id_index_and_source()
        {
            var elastic = new Elastic { Answer = (_, _, body) => (HttpStatusCode.OK, body!.Contains("\"from\":100", StringComparison.Ordinal) ? Hits(101, 50) : Hits(1, 100)) };

            var found = await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts,archive-*", ["query"] = "{\"query\": {\"match\": {\"title\": \"café\"}}}", ["sort"] = "@timestamp:desc, title", ["includes"] = "title, author.*", ["excludes"] = "summary", ["routing"] = "sv/1" });
            Assert.Equal(150, found.Count); // a thousand at most by default
            const string Url = Cluster + "/posts,archive-*/_search?routing=sv%2F1&_source_includes=title,author.*&_source_excludes=summary&sort=%40timestamp%3Adesc%2Ctitle";
            Assert.Equal(new[] { Url, Url }, elastic.Requests.Select(request => request.Url)); // its total the end
            Assert.Equal(new[] { "{\"query\":{\"match\":{\"title\":\"café\"}},\"from\":0,\"size\":100}", "{\"query\":{\"match\":{\"title\":\"café\"}},\"from\":100,\"size\":100}" }, elastic.Requests.Select(request => request.Body));
            Assert.All(elastic.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("elastic:elastic-password-1")), request.Authorization));
            var first = found[0].Fields;
            Assert.Equal(("p1", "posts", "Café 1", 12L, 4.5, true), ((string?)first["_id"], (string?)first["_index"], (string?)first["title"], (long?)first["views"], (double?)first["rating"], (bool?)first["published"]));
            Assert.Equal(("[\"kaffe\",\"bröd\"]", "{\"name\":\"Ada\"}"), ((string?)first["tags"], (string?)first["author"])); // a list and an object as their JSON
            Assert.Null(first["summary"]);
            Assert.False(first.ContainsKey("_score"));

            Assert.Equal(30, (await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts", ["max"] = "30" })).Count);
            Assert.Equal(($"{Cluster}/posts/_search", "{\"from\":0,\"size\":30}"), (elastic.Requests[^1].Url, elastic.Requests[^1].Body)); // every document; a page as small as asked

            elastic.Answer = (_, _, body) => (HttpStatusCode.OK, body!.Contains("\"from\":0", StringComparison.Ordinal) ? Hits(1, 100, "{\"value\":100,\"relation\":\"gte\"}") : Hits(101, 7, "{\"value\":100,\"relation\":\"gte\"}"));
            Assert.Equal(107, (await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts" })).Count); // a short page the end, a counted total only a floor
            elastic.Answer = (_, _, _) => (HttpStatusCode.OK, Hits(1, 100, "{\"value\":100,\"relation\":\"eq\"}"));
            elastic.Requests.Clear();
            Assert.Equal(100, (await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts" })).Count);
            Assert.Single(elastic.Requests); // an exact total the end, the page full
            elastic.Answer = (_, _, _) => (HttpStatusCode.OK, Hits(1, 100, "3"));
            Assert.Equal(100, (await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts", ["max"] = "10000" })).Count); // an older cluster's plain total
            elastic.Answer = (_, _, _) => (HttpStatusCode.OK, Hits(1, 100, "{\"value\":20000,\"relation\":\"eq\"}"));
            elastic.Requests.Clear();
            Assert.Equal(10000, (await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts", ["max"] = "10000" })).Count);
            Assert.Equal(100, elastic.Requests.Count);
            Assert.EndsWith("\"from\":9900,\"size\":100}", elastic.Requests[^1].Body); // never past the window

            elastic.Answer = (_, _, _) => (HttpStatusCode.OK, Hits(1, 1));
            var raw = Assert.Single(await Run(elastic, ElasticsearchConnector.SearchOperation, new() { ["index"] = "posts", ["max"] = "1", ["raw"] = "true" }, credentials: new() { ["password"] = "ZWxhc3RpYzprZXk=" })).Fields;
            Assert.Equal("ApiKey ZWxhc3RpYzprZXk=", elastic.Requests[^1].Authorization); // no user: an API key
            Assert.Equal(("p1", 1.5, "{\"title\":\"Café 1\",\"views\":12,\"rating\":4.5,\"published\":true,\"tags\":[\"kaffe\",\"bröd\"],\"author\":{\"name\":\"Ada\"},\"summary\":null}"), ((string?)raw["_id"], (double?)raw["_score"], (string?)raw["_source"]));
        }

        [Fact]
        public async Task A_document_is_counted_got_created_updated_and_deleted_and_the_indices_kept()
        {
            var elastic = new Elastic
            {
                Answer = (method, url, _) => (HttpStatusCode.OK, (method.Method, url) switch
                {
                    (_, var u) when u.Contains("/_count", StringComparison.Ordinal) => "{\"count\":42,\"_shards\":{\"total\":1}}",
                    ("GET", var u) when u.Contains("/_doc/", StringComparison.Ordinal) => "{\"_index\":\"posts\",\"_id\":\"a b/1\",\"_version\":3,\"found\":true,\"_source\":{\"title\":\"Café\"}}",
                    ("GET", var u) when u.Contains("/_cat/indices", StringComparison.Ordinal) => "[{\"health\":\"green\",\"status\":\"open\",\"index\":\"posts\",\"pri\":\"1\",\"rep\":\"0\",\"docs.count\":\"42\",\"docs.deleted\":\"2\",\"store.size\":\"10240\"},{\"health\":\"yellow\",\"status\":\"open\",\"index\":\"logs\",\"pri\":\"1\",\"rep\":\"1\",\"docs.count\":\"7\",\"docs.deleted\":\"0\",\"store.size\":\"512\"}]",
                    ("GET", _) => "{\"posts\":{\"aliases\":{\"blog\":{}},\"mappings\":{\"properties\":{\"title\":{\"type\":\"text\"}}},\"settings\":{\"index\":{\"number_of_shards\":\"1\"}}}}",
                    ("PUT", var u) when !u.Contains("/_doc", StringComparison.Ordinal) => "{\"acknowledged\":true,\"shards_acknowledged\":true,\"index\":\"posts-2\"}",
                    ("DELETE", var u) when !u.Contains("/_doc", StringComparison.Ordinal) => "{\"acknowledged\":true}",
                    ("POST", var u) when u.Contains("/_update/", StringComparison.Ordinal) => "{\"_index\":\"posts\",\"_id\":\"p1\",\"_version\":2,\"result\":\"updated\"}",
                    ("DELETE", _) => "{\"_index\":\"posts\",\"_id\":\"p1\",\"_version\":3,\"result\":\"deleted\"}",
                    _ => "{\"_index\":\"posts\",\"_id\":\"Xy12\",\"_version\":1,\"result\":\"created\"}",
                }),
            };
            (string, string, string?) Last() => (elastic.Requests[^1].Method.Method, elastic.Requests[^1].Url, elastic.Requests[^1].Body);

            Assert.Equal(42L, (long?)Assert.Single(await Run(elastic, ElasticsearchConnector.CountOperation, new() { ["index"] = "posts", ["query"] = "{\"query\": {\"term\": {\"published\": true}}}", ["sort"] = "title", ["includes"] = "title", ["max"] = "5", ["refresh"] = "true", ["pipeline"] = "enrich-1" })).Fields["count"]);
            Assert.Equal(("POST", $"{Cluster}/posts/_count", (string?)"{\"query\":{\"term\":{\"published\":true}}}"), Last()); // no sort, source, paging, refresh or pipeline on a count
            await Run(elastic, ElasticsearchConnector.CountOperation, new() { ["index"] = "logs-*" });
            Assert.Equal(("POST", $"{Cluster}/logs-*/_count", (string?)"{}"), Last());

            var got = Assert.Single(await Run(elastic, ElasticsearchConnector.GetOperation, new() { ["index"] = "posts", ["id"] = "a b/1", ["includes"] = "title", ["routing"] = "sv", ["sort"] = "title", ["query"] = "title:café" })).Fields;
            Assert.Equal(("GET", $"{Cluster}/posts/_doc/a%20b%2F1?routing=sv&_source_includes=title", (string?)null), Last()); // its id escaped; no sort or search on a get
            Assert.Equal(("a b/1", "posts", "Café"), ((string?)got["_id"], (string?)got["_index"], (string?)got["title"]));

            var made = Assert.Single(await Run(elastic, ElasticsearchConnector.CreateOperation, new() { ["index"] = "posts", ["fields"] = "{\"title\": \"Café\", \"views\": 3}", ["pipeline"] = "enrich-1", ["refresh"] = "Wait_For", ["includes"] = "title" })).Fields;
            Assert.Equal(("POST", $"{Cluster}/posts/_doc?pipeline=enrich-1&refresh=wait_for", (string?)"{\"title\":\"Café\",\"views\":3}"), Last()); // Elasticsearch's id
            Assert.Equal(("Xy12", "posts", 1L, "created"), ((string?)made["_id"], (string?)made["_index"], (long?)made["_version"], (string?)made["result"]));
            await Run(elastic, ElasticsearchConnector.CreateOperation, new() { ["index"] = "posts", ["id"] = "p 1", ["fields"] = "{\"title\": \"Café\"}", ["routing"] = "sv" });
            Assert.Equal(("PUT", $"{Cluster}/posts/_doc/p%201?routing=sv", (string?)"{\"title\":\"Café\"}"), Last()); // its own id
            Assert.Equal("updated", (string?)Assert.Single(await Run(elastic, ElasticsearchConnector.UpdateOperation, new() { ["index"] = "posts", ["id"] = "p1", ["fields"] = "{\"title\": \"Kafé\"}", ["refresh"] = "true", ["pipeline"] = "enrich-1" })).Fields["result"]);
            Assert.Equal(("POST", $"{Cluster}/posts/_update/p1?refresh=true", (string?)"{\"doc\":{\"title\":\"Kafé\"}}"), Last()); // no pipeline on an update
            Assert.Equal("deleted", (string?)Assert.Single(await Run(elastic, ElasticsearchConnector.DeleteOperation, new() { ["index"] = "posts", ["id"] = "p1", ["refresh"] = "false" })).Fields["result"]);
            Assert.Equal(("DELETE", $"{Cluster}/posts/_doc/p1?refresh=false", (string?)null), Last());

            var indices = await Run(elastic, ElasticsearchConnector.IndicesOperation, new() { ["routing"] = "sv" });
            Assert.Equal(("GET", $"{Cluster}/_cat/indices?format=json&bytes=b&s=index", (string?)null), Last());
            Assert.Equal(new[] { ("posts", "green", "open", 42L, 2L, 10240L, 1L, 0L), ("logs", "yellow", "open", 7L, 0L, 512L, 1L, 1L) }, indices.Select(index => ((string)index.Fields["index"]!, (string)index.Fields["health"]!, (string)index.Fields["status"]!, (long)index.Fields["documents"]!, (long)index.Fields["deleted"]!, (long)index.Fields["bytes"]!, (long)index.Fields["primaries"]!, (long)index.Fields["replicas"]!)));
            Assert.Single(await Run(elastic, ElasticsearchConnector.IndicesOperation, new() { ["max"] = "1" }));
            Assert.Equal(2, (await Run(elastic, ElasticsearchConnector.IndicesOperation, new() { ["max"] = "20000" })).Count); // the window a search's alone

            var index = Assert.Single(await Run(elastic, ElasticsearchConnector.IndexOperation, new() { ["index"] = "posts" })).Fields;
            Assert.Equal(("GET", $"{Cluster}/posts", (string?)null), Last());
            Assert.Equal(("posts", "{\"blog\":{}}", "{\"properties\":{\"title\":{\"type\":\"text\"}}}", "{\"index\":{\"number_of_shards\":\"1\"}}"), ((string?)index["index"], (string?)index["aliases"], (string?)index["mappings"], (string?)index["settings"]));
            var created = Assert.Single(await Run(elastic, ElasticsearchConnector.CreateIndexOperation, new() { ["index"] = "posts-2", ["settings"] = "{\"number_of_shards\": 1}", ["mappings"] = "{\"properties\": {\"title\": {\"type\": \"text\"}}}", ["aliases"] = "{\"blog\": {}}", ["refresh"] = "true" })).Fields;
            Assert.Equal(("PUT", $"{Cluster}/posts-2", (string?)"{\"settings\":{\"number_of_shards\":1},\"mappings\":{\"properties\":{\"title\":{\"type\":\"text\"}}},\"aliases\":{\"blog\":{}}}"), Last());
            Assert.Equal(("posts-2", true), ((string?)created["index"], (bool?)created["acknowledged"]));
            await Run(elastic, ElasticsearchConnector.CreateIndexOperation, new() { ["index"] = "posts-3" });
            Assert.Equal(("PUT", $"{Cluster}/posts-3", (string?)"{}"), Last());
            var dropped = Assert.Single(await Run(elastic, ElasticsearchConnector.DeleteIndexOperation, new() { ["index"] = "posts-2", ["routing"] = "sv" })).Fields;
            Assert.Equal(("posts-2", true), ((string?)dropped["index"], (bool?)dropped["acknowledged"]));
            Assert.Equal(("DELETE", $"{Cluster}/posts-2", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_elasticsearchs_word_on_a_failure()
        {
            var elastic = new Elastic();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Cluster, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elastic, operation, parameters, endpoint, credentials))).Message;
            const string Search = ElasticsearchConnector.SearchOperation;

            Assert.StartsWith("Elasticsearch has no operation 'reindex' - search, count, get,", await Refused("reindex", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(Search, new() { ["index"] = "posts", ["max"] = "0" }));
            Assert.Equal("'10001' is more than Elasticsearch pages through by from and size - 10,000 at the most (max).", await Refused(Search, new() { ["index"] = "posts", ["max"] = "10001" }));
            Assert.Equal("Give the index.", await Refused(Search, new()));
            Assert.Equal("'Posts' is not an index's name - lowercase letters, digits, ., _ and -, * and a comma between several.", await Refused(Search, new() { ["index"] = "Posts" }));
            Assert.Equal("'_all/x' is not an index's name - lowercase letters, digits, ., _ and -, * and a comma between several.", await Refused(ElasticsearchConnector.CountOperation, new() { ["index"] = "_all/x" }));
            Assert.Equal("'logs-*' is not an index's name - lowercase letters, digits, ., _ and -.", await Refused(ElasticsearchConnector.DeleteIndexOperation, new() { ["index"] = "logs-*" }));
            Assert.Equal("Give the document's id.", await Refused(ElasticsearchConnector.GetOperation, new() { ["index"] = "posts" }));
            Assert.Equal("The document's id is longer than Elasticsearch's 512.", await Refused(ElasticsearchConnector.DeleteOperation, new() { ["index"] = "posts", ["id"] = new string('x', 513) }));
            Assert.Equal("'title;x' is not a list of fields - title,author.* (includes).", await Refused(Search, new() { ["index"] = "posts", ["includes"] = "title;x" }));
            Assert.Equal("'a;b' is not a list of fields - title,author.* (excludes).", await Refused(ElasticsearchConnector.GetOperation, new() { ["index"] = "posts", ["id"] = "1", ["excludes"] = "a;b" }));
            Assert.Equal("'title:DESC' is not a sort - @timestamp:desc,title.", await Refused(Search, new() { ["index"] = "posts", ["sort"] = "title:DESC" })); // Elasticsearch's own word, lowercase
            Assert.Equal("'enrich 1' is not a pipeline's id.", await Refused(ElasticsearchConnector.CreateOperation, new() { ["index"] = "posts", ["fields"] = "{\"a\": 1}", ["pipeline"] = "enrich 1" }));
            Assert.Equal("'soon' is not true, wait_for or false (refresh).", await Refused(ElasticsearchConnector.UpdateOperation, new() { ["index"] = "posts", ["id"] = "1", ["fields"] = "{\"a\": 1}", ["refresh"] = "soon" }));
            Assert.Equal("The search is not a JSON object - {\"query\": {\"match\": {\"title\": \"news\"}}}.", await Refused(Search, new() { ["index"] = "posts", ["query"] = "title:news" }));
            Assert.Equal("The search pages itself - leave from, size and search_after out of it, and give max.", await Refused(Search, new() { ["index"] = "posts", ["query"] = "{\"size\": 5}" }));
            Assert.Equal("Give the fields - a JSON object.", await Refused(ElasticsearchConnector.CreateOperation, new() { ["index"] = "posts" }));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"title\": \"Hello\"}.", await Refused(ElasticsearchConnector.UpdateOperation, new() { ["index"] = "posts", ["id"] = "1", ["fields"] = "{}" }));
            Assert.Equal("The mappings are not a JSON object.", await Refused(ElasticsearchConnector.CreateIndexOperation, new() { ["index"] = "posts", ["mappings"] = "[1]" }));
            Assert.Equal("Give the cluster's address - https://search.example.com:9200.", await Refused(Search, new() { ["index"] = "posts" }, endpoint: ""));
            Assert.Equal("The endpoint 'search.example.test' is not an absolute http(s) URL.", await Refused(Search, new() { ["index"] = "posts" }, endpoint: "search.example.test"));
            Assert.Equal("The Elasticsearch connection has no password - or API key.", await Refused(Search, new() { ["index"] = "posts" }, credentials: new() { ["username"] = "elastic" }));
            Assert.Empty(elastic.Requests); // nothing sent

            async Task<string> Failed(string operation, HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Elastic { Answer = (_, _, _) => (status, answer) }, operation, new() { ["index"] = "posts", ["id"] = "p1", ["fields"] = "{\"a\": 1}" }))).Message;
            Assert.Equal("HTTP 404 (index_not_found_exception): no such index [posts]", await Failed(Search, HttpStatusCode.NotFound, "{\"error\":{\"root_cause\":[],\"type\":\"index_not_found_exception\",\"reason\":\"no such index [posts]\"},\"status\":404}"));
            Assert.Equal("Elasticsearch has no document 'p1' in 'posts'.", await Failed(ElasticsearchConnector.GetOperation, HttpStatusCode.NotFound, "{\"_index\":\"posts\",\"_id\":\"p1\",\"found\":false}"));
            Assert.Equal("Elasticsearch has no document 'p1' in 'posts'.", await Failed(ElasticsearchConnector.DeleteOperation, HttpStatusCode.NotFound, "{\"_index\":\"posts\",\"_id\":\"p1\",\"result\":\"not_found\"}"));
            Assert.Equal("HTTP 404.", await Failed(ElasticsearchConnector.CreateOperation, HttpStatusCode.NotFound, "{}")); // a create never misses a document
            Assert.Equal("HTTP 404 (document_missing_exception): [p1]: document missing", await Failed(ElasticsearchConnector.UpdateOperation, HttpStatusCode.NotFound, "{\"error\":{\"type\":\"document_missing_exception\",\"reason\":\"[p1]: document missing\"},\"status\":404}"));
            Assert.Equal("HTTP 400 (parsing_exception): " + new string('e', 300), await Failed(Search, HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"parsing_exception\",\"reason\":\"" + new string('e', 400) + "\"}}")); // a long reason cut
            Assert.Equal("HTTP 401: Unauthorized", await Failed(Search, HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\"}")); // an error as a word
            Assert.Equal("Elasticsearch answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(Search, HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no documents
            Assert.Equal("HTTP 502.", await Failed(Search, HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_cluster_and_its_version()
        {
            Task<ConnectionTestResult> Test(Elastic elastic, string endpoint = Cluster) =>
                new ElasticsearchConnector().TestConnectionAsync(Contexts.Test(new StubEgress(elastic), endpoint, null, Credentials));
            var elastic = new Elastic { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"name\":\"node-1\",\"cluster_name\":\"shop\",\"version\":{\"number\":\"8.15.2\",\"build_flavor\":\"default\"},\"tagline\":\"You Know, for Search\"}") };
            var tested = await Test(elastic, Cluster + "/");
            Assert.Equal((true, "Elasticsearch 8.15.2: the cluster 'shop' answers."), (tested.Success, tested.Message));
            Assert.Equal(("GET", $"{Cluster}/"), (elastic.Requests[0].Method.Method, elastic.Requests[0].Url));
            Assert.Equal("OpenSearch 2.17.0: the cluster 'logs' answers.", (await Test(new Elastic { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"cluster_name\":\"logs\",\"version\":{\"distribution\":\"opensearch\",\"number\":\"2.17.0\"}}") })).Message);
            Assert.Equal("Elasticsearch: the cluster '?' answers.", (await Test(new Elastic { Answer = (_, _, _) => (HttpStatusCode.OK, "{}") })).Message);
            var refused = await Test(new Elastic { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"error\":{\"type\":\"security_exception\",\"reason\":\"unable to authenticate user [elastic] for REST request [/]\"},\"status\":401}") });
            Assert.Equal((false, "HTTP 401 (security_exception): unable to authenticate user [elastic] for REST request [/]"), (refused.Success, refused.Message));
            var nowhere = await Test(new Elastic(), "");
            Assert.Equal((false, "Give the cluster's address - https://search.example.com:9200."), (nowhere.Success, nowhere.Message));
            Assert.Equal((AuthenticationType.Basic, "elasticsearch"), (new ElasticsearchConnector().AuthType, new ElasticsearchConnector().ConnectorType));
            Assert.Equal(10, new ElasticsearchConnector().Operations.Count);
        }
    }
}
