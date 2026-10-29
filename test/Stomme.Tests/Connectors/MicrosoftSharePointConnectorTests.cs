#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.MicrosoftSharePoint;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Microsoft SharePoint (n8n's Microsoft SharePoint node: a site's lists and their items): the app's client
    /// credentials at the tenant's token endpoint, Graph's sites API with the token as a bearer; a site by its address, id or
    /// host; lists read and one got with its columns; items read page by page by Graph's next link - followed only while it
    /// stays on the list - no further than asked, by a filter asked even of columns not indexed; an item got, created, updated
    /// and deleted, an item created or updated by a column's value - more than one match refused; what could go wrong refused
    /// before a token is asked for; Graph's code and message on a failure; a test naming the site; a file of the site's
    /// documents downloaded into the run's folder through Graph's redirect without the token, uploaded and updated.
    /// </summary>
    public sealed class MicrosoftSharePointConnectorTests
    {
        private const string Api = "https://graph.microsoft.com/v1.0";

        private const string TokenUrl = "https://login.microsoftonline.com/kafe-tenant/oauth2/v2.0/token";

        private const string Site = "kafe.sharepoint.com:/sites/Kassa:";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = TokenUrl };

        private sealed class Graph : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string? Prefer)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"value\":[]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body, request.Headers.TryGetValues("Prefer", out var prefer) ? string.Join(",", prefer) : null));
                var (status, answer) = url == TokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"eyJ.sp\",\"expires_in\":3599,\"token_type\":\"Bearer\"}") : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Graph graph, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MicrosoftSharePointConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(graph), "", operation, parameters, settings, credentials ?? Credentials));

        private static string Items(int first, int count, string? next) =>
            "{\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"@odata.etag\":\"\\\"" + i + ",1\\\"\",\"id\":\"" + i + "\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa/Lists/Orders/" + i + "_.000\",\"createdDateTime\":\"2026-10-14T08:00:00Z\",\"lastModifiedDateTime\":\"2026-10-14T09:00:00Z\",\"fields\":{\"@odata.etag\":\"x\",\"id\":\"" + i + "\",\"Title\":\"Order " + i + "\",\"Total\":129.5,\"Paid\":true,\"Tags\":[\"fika\"]}}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";

        [Fact]
        public async Task Items_are_read_page_by_page_by_graphs_next_link_no_further_than_asked()
        {
            var list = $"{Api}/sites/{Site}/lists/Orders/items";
            var graph = new Graph
            {
                Answer = (_, url) => url.Contains("skiptoken=p1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Items(201, 200, null)) : (HttpStatusCode.OK, Items(1, 200, list + "?$expand=fields&$skiptoken=p1")),
            };

            var items = await Run(graph, "items", new() { ["site"] = "kafe.sharepoint.com:/sites/Kassa", ["list"] = "Orders", ["filter"] = "fields/Status eq 'Öppen'", ["max"] = "250" });
            Assert.Equal(250, items.Count); // no further than asked
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=app-1&client_secret=secret-1&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default"), (graph.Requests[0].Method.Method, graph.Requests[0].Url, graph.Requests[0].Body));
            Assert.Equal(new[] { $"{list}?$expand=fields&$top=200&$filter=fields%2FStatus%20eq%20%27%C3%96ppen%27", $"{list}?$expand=fields&$skiptoken=p1" }, graph.Requests.Skip(1).Select(request => request.Url));
            Assert.All(graph.Requests.Skip(1), request => Assert.Equal(("Bearer eyJ.sp", (string?)"HonorNonIndexedQueriesWarningMayFailRandomly"), (request.Authorization, request.Prefer)));
            var first = items[0].Fields;
            Assert.Equal(("1", "Order 1", 129.5, true, "[\"fika\"]", "https://kafe.sharepoint.com/sites/Kassa/Lists/Orders/1_.000", "2026-10-14T09:00:00Z"), ((string?)first["id"], (string?)first["Title"], (double?)first["Total"], (bool?)first["Paid"], (string?)first["Tags"], (string?)first["webUrl"], (string?)first["lastModifiedDateTime"]));
            Assert.False(first.Keys.Any(key => key.StartsWith("@odata.", StringComparison.Ordinal)));

            var connection = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Items(1, 3, null)) };
            Assert.Equal(3, (await Run(connection, null!, new() { ["list"] = "Orders" }, new() { ["site"] = "kafe.sharepoint.com" })).Count); // no operation: the items; the connection's site - its root
            Assert.Equal(($"{Api}/sites/kafe.sharepoint.com/lists/Orders/items?$expand=fields&$top=200", (string?)null), (connection.Requests[1].Url, connection.Requests[1].Prefer)); // no filter: nothing asked of unindexed columns
            var byId = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Items(1, 3, null)) };
            await Run(byId, "items", new() { ["site"] = "kafe.sharepoint.com,0a1b2c3d-1111-2222-3333-444455556666,9f8e7d6c-1111-2222-3333-444455556666", ["list"] = "5b6c1e2a-1111-2222-3333-444455556666", ["max"] = "2" });
            Assert.Equal($"{Api}/sites/kafe.sharepoint.com,0a1b2c3d-1111-2222-3333-444455556666,9f8e7d6c-1111-2222-3333-444455556666/lists/5b6c1e2a-1111-2222-3333-444455556666/items?$expand=fields&$top=2", byId.Requests[1].Url); // a page no larger than asked
            var spaced = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Items(1, 1, null)) };
            await Run(spaced, "items", new() { ["site"] = Site, ["list"] = "Kafé orders" });
            Assert.StartsWith($"{Api}/sites/{Site}/lists/Kaf%C3%A9%20orders/items?", spaced.Requests[1].Url); // a list's name escaped
            await Run(spaced, "items", new() { ["site"] = Site, ["list"] = "Fika & bröd (2026)" });
            Assert.StartsWith($"{Api}/sites/{Site}/lists/Fika%20%26%20br%C3%B6d%20%282026%29/items?", spaced.Requests[^1].Url); // every mark in it
            var endless = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Items(1, 200, list + "?$skiptoken=" + Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, "items", new() { ["site"] = Site, ["list"] = "Orders" })).Count); // 1,000 at most when no max is given

            foreach (var link in new[] { "https://elsewhere.example.test/v1.0/sites/x/lists/Orders/items?$skiptoken=p1", $"{Api}/sites/{Site}/lists/Other/items?$skiptoken=p1", list + "/7?$skiptoken=p1", list + "?$expand=fields&$top=200" })
            {
                var stray = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Items(1, 2, link)) };
                Assert.Equal("Microsoft Graph named a next page outside the list - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stray, "items", new() { ["site"] = Site, ["list"] = "Orders" }))).Message);
                Assert.Equal(2, stray.Requests.Count); // the token, one page: nothing sent there
            }

            var lists = new Graph { Answer = (_, _) => (HttpStatusCode.OK, "{\"value\":[{\"@odata.etag\":\"x\",\"id\":\"5b6c1e2a\",\"name\":\"Orders\",\"displayName\":\"Orders\",\"list\":{\"template\":\"genericList\"}}]}") };
            var found = Assert.Single(await Run(lists, "lists", new() { ["site"] = Site })).Fields;
            Assert.Equal($"{Api}/sites/{Site}/lists?$select=id,name,displayName,description,webUrl,list,createdDateTime,lastModifiedDateTime", lists.Requests[1].Url);
            Assert.Equal(("Orders", "{\"template\":\"genericList\"}"), ((string?)found["displayName"], (string?)found["list"]));
            var one = new Graph { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"5b6c1e2a\",\"displayName\":\"Orders\",\"columns\":[{\"name\":\"Title\"}]}") };
            Assert.Equal("[{\"name\":\"Title\"}]", (string?)Assert.Single(await Run(one, "list", new() { ["site"] = Site, ["list"] = "Orders" })).Fields["columns"]);
            Assert.Equal($"{Api}/sites/{Site}/lists/Orders?$expand=columns($select=name,displayName,required,readOnly)", one.Requests[1].Url);
        }

        [Fact]
        public async Task An_item_is_got_created_updated_and_deleted_and_created_or_updated_by_a_columns_value()
        {
            var items = $"{Api}/sites/{Site}/lists/Orders/items";
            var matches = "{\"value\":[]}";
            var graph = new Graph
            {
                Answer = (method, url) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : method == HttpMethod.Patch ? (HttpStatusCode.OK, "{\"@odata.etag\":\"x\",\"Title\":\"Order 7\",\"Status\":\"Betald\"}")
                    : method == HttpMethod.Post ? (HttpStatusCode.Created, "{\"id\":\"8\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa/Lists/Orders/8_.000\",\"fields\":{\"Title\":\"Order 8\",\"Status\":\"Öppen\"}}")
                    : url.Contains("$filter=", StringComparison.Ordinal) ? (HttpStatusCode.OK, matches)
                    : (HttpStatusCode.OK, "{\"id\":\"7\",\"fields\":{\"Title\":\"Order 7\",\"Status\":\"Öppen\"}}"),
            };
            (string, string, string?, string?) Last() => (graph.Requests[^1].Method.Method, graph.Requests[^1].Url, graph.Requests[^1].Body, graph.Requests[^1].Prefer);

            var item = Assert.Single(await Run(graph, "item", new() { ["site"] = Site, ["list"] = "Orders", ["id"] = "7" })).Fields;
            Assert.Equal(("GET", $"{items}/7?$expand=fields", (string?)null, (string?)null), Last());
            Assert.Equal(("7", "Öppen"), ((string?)item["id"], (string?)item["Status"]));

            var created = Assert.Single(await Run(graph, "create_item", new() { ["site"] = Site, ["list"] = "Orders", ["values"] = "{\"Title\": \"Order 8\", \"Status\": \"Öppen\", \"Total\": 42}" })).Fields;
            Assert.Equal(("POST", items, (string?)"{\"fields\":{\"Title\":\"Order 8\",\"Status\":\"Öppen\",\"Total\":42}}", (string?)null), Last());
            Assert.Equal(("8", "Order 8", "https://kafe.sharepoint.com/sites/Kassa/Lists/Orders/8_.000"), ((string?)created["id"], (string?)created["Title"], (string?)created["webUrl"]));

            var updated = Assert.Single(await Run(graph, "update_item", new() { ["site"] = Site, ["list"] = "Orders", ["id"] = "7", ["values"] = "{\"Status\": \"Betald\"}" })).Fields;
            Assert.Equal(("PATCH", $"{items}/7/fields", (string?)"{\"Status\":\"Betald\"}", (string?)null), Last());
            Assert.Equal(("7", "Betald"), ((string?)updated["id"], (string?)updated["Status"]));

            var deleted = Assert.Single(await Run(graph, "Delete_Item", new() { ["site"] = Site, ["list"] = "Orders", ["id"] = "7" })).Fields;
            Assert.Equal(("DELETE", $"{items}/7", (string?)null, (string?)null), Last());
            Assert.Equal(((object?)"7", (object?)true), (deleted["id"], deleted["deleted"]));

            graph.Requests.Clear();
            var none = Assert.Single(await Run(graph, "upsert_item", new() { ["site"] = Site, ["list"] = "Orders", ["match"] = "Title", ["values"] = "{\"Title\": \"Kafé's order\", \"Status\": \"Öppen\"}" })).Fields;
            Assert.Equal(("GET", $"{items}?$expand=fields&$top=2&$filter=fields%2FTitle%20eq%20%27Kaf%C3%A9%27%27s%20order%27", (string?)null, (string?)"HonorNonIndexedQueriesWarningMayFailRandomly"), (graph.Requests[1].Method.Method, graph.Requests[1].Url, graph.Requests[1].Body, graph.Requests[1].Prefer));
            Assert.Equal(("POST", items), (Last().Item1, Last().Item2));
            Assert.Equal((object?)true, none["created"]); // none had it: created
            matches = "{\"value\":[{\"id\":\"7\",\"fields\":{\"Title\":\"Order 7\"}}]}";
            var one = Assert.Single(await Run(graph, "upsert_item", new() { ["site"] = Site, ["list"] = "Orders", ["match"] = "Title", ["values"] = "{\"Title\": \"Order 7\", \"Status\": \"Betald\"}" })).Fields;
            Assert.Equal(("PATCH", $"{items}/7/fields", (string?)"{\"Title\":\"Order 7\",\"Status\":\"Betald\"}", (string?)null), Last());
            Assert.Equal((object?)false, one["created"]); // one had it: updated
            await Run(graph, "upsert_item", new() { ["site"] = Site, ["list"] = "Orders", ["match"] = "Number", ["values"] = "{\"Number\": 1042, \"Status\": \"Betald\"}" });
            Assert.Contains("$filter=fields%2FNumber%20eq%201042", graph.Requests.Last(request => request.Method == HttpMethod.Get).Url, StringComparison.Ordinal); // a number matched as a number
            matches = "{\"value\":[{\"id\":\"7\"},{\"id\":\"9\"}]}";
            graph.Requests.Clear();
            Assert.Equal("More than one item has Title 'Order 7' - nothing is written; match on a column whose values are unique.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(graph, "upsert_item", new() { ["site"] = Site, ["list"] = "Orders", ["match"] = "Title", ["values"] = "{\"Title\": \"Order 7\"}" }))).Message);
            Assert.Equal(2, graph.Requests.Count); // the token, the search: nothing written
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_graphs_words_on_a_failure()
        {
            var graph = new Graph();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(graph, operation, parameters, credentials: credentials))).Message;
            Dictionary<string, string> Order(params (string Name, string Value)[] more) => new[] { ("site", Site), ("list", "Orders"), ("id", "7") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);

            Assert.Equal("Microsoft SharePoint has no operation 'files' - lists, list, items, item, create_item, update_item, upsert_item, delete_item, download_file, upload_file or update_file.", await Refused("files", new()));
            Assert.Equal("Give the site - kafe.sharepoint.com:/sites/Kassa, or its id (site), or the connection's.", await Refused("lists", new()));
            foreach (var site in new[] { "https://kafe.sharepoint.com/sites/Kassa", "kafe.sharepoint.com:/sites/Kassa/../Ekonomi", "elsewhere.example.test:/sites/Kassa", "elsewhere.example.test", "kafe.sharepoint.com,1,2" })
            {
                Assert.Equal($"'{site}' is not a site - kafe.sharepoint.com:/sites/Kassa, or its id (site).", await Refused("lists", new() { ["site"] = site }));
            }

            Assert.Equal("Give the list - its name or id (list).", await Refused("items", new() { ["site"] = Site }));
            Assert.Equal("'Orders/../Secrets' is not a list's name or id (list).", await Refused("items", new() { ["site"] = Site, ["list"] = "Orders/../Secrets" }));
            Assert.Equal("'..' is not a list's name or id (list).", await Refused("items", new() { ["site"] = Site, ["list"] = ".." }));
            Assert.Equal("Give the item's id (id).", await Refused("item", new() { ["site"] = Site, ["list"] = "Orders" }));
            Assert.Equal("'7/fields' is not an item's id - a number (id).", await Refused("delete_item", Order(("id", "7/fields"))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("items", Order(("max", "0"))));
            Assert.Equal("'Status eq 1\u0001' is not a filter without control characters (filter).", await Refused("items", Order(("filter", "Status eq 1\u0001"))));
            Assert.Equal("Give the values - a JSON object of columns and values (values).", await Refused("create_item", Order()));
            const string NotAnObject = "The values are not a JSON object of columns and values - {\"Title\": \"Kassan\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_item", Order(("values", "Title = Kassan"))));
            Assert.Equal(NotAnObject, await Refused("update_item", Order(("values", "{}"))));
            Assert.Equal("'Due date' is not a column's internal name - Title, Status, Due_x0020_date (values).", await Refused("update_item", Order(("values", "{\"Due date\": \"2026-10-31\"}"))));
            Assert.Equal("The value of 'Title' has a control character (values).", await Refused("update_item", Order(("values", "{\"Title\": \"Kassan\\u0001\"}"))));
            Assert.Equal("Give the column to match on (match).", await Refused("upsert_item", Order(("values", "{\"Title\": \"x\"}"))));
            Assert.Equal("'Order title' is not a column's internal name (match).", await Refused("upsert_item", Order(("match", "Order title"), ("values", "{\"Title\": \"x\"}"))));
            Assert.Equal("Give the value of 'Title' to match on, a text or a number, among the values (values).", await Refused("upsert_item", Order(("match", "Title"), ("values", "{\"Status\": \"x\"}"))));
            Assert.Equal("Give the value of 'Title' to match on, a text or a number, among the values (values).", await Refused("upsert_item", Order(("match", "Title"), ("values", "{\"Title\": [\"x\"]}"))));
            Assert.Equal("Give the value of 'Paid' to match on, a text or a number, among the values (values).", await Refused("upsert_item", Order(("match", "Paid"), ("values", "{\"Paid\": true}"))));
            Assert.Equal("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token.", await Refused("lists", new() { ["site"] = Site }, new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1" }));
            Assert.Empty(graph.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "item") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Graph { Answer = (_, _) => (status, answer) }, operation, Order(("values", "{\"Title\": \"x\"}"))))).Message;
            Assert.Equal("HTTP 404 itemNotFound: The resource could not be found.", await Failed(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The resource could not be found.\"}}"));
            Assert.Equal("HTTP 403 accessDenied", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\"}}", "update_item"));
            Assert.Equal("HTTP 400 invalidRequest: " + new string('e', 300 - "invalidRequest: ".Length), await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"invalidRequest\",\"message\":\"" + new string('e', 400) + "\"}}", "create_item")); // a long one cut
            Assert.Equal("Microsoft Graph answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "delete_item"));
            Assert.Equal("Microsoft SharePoint answered with no item.", await Failed(HttpStatusCode.OK, "", "item"));
            Assert.Equal("Microsoft SharePoint answered with no columns.", await Failed(HttpStatusCode.OK, "", "update_item"));
        }

        private sealed class Documents : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, byte[] Body)> Requests { get; } = new();

            public Func<HttpMethod, string, HttpResponseMessage> Answer { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
                return url == TokenUrl ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"eyJ.sp\",\"expires_in\":3599}", Encoding.UTF8, "application/json") } : Answer(request.Method, url);
            }
        }

        [Fact]
        public async Task A_file_of_the_sites_documents_is_downloaded_uploaded_and_updated_through_the_runs_folder()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-sharepoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var documents = $"{Api}/sites/{Site}/drive";
                var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x00, 0xFF };
                const string Download = "https://kafe.sharepoint.com/sites/Kassa/_layouts/15/download.aspx?UniqueId=1&tempauth=v1.x";
                HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                var site = new Documents
                {
                    Answer = (method, url) => url == Download ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) }
                        : method == HttpMethod.Get && url.EndsWith("/content", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Download) } }
                        : method == HttpMethod.Get ? Json(HttpStatusCode.OK, "{\"id\":\"01PDF\",\"name\":\"1042.pdf\",\"size\":8,\"file\":{\"mimeType\":\"application/pdf\"}}")
                        : Json(method == HttpMethod.Put && url.Contains("conflictBehavior", StringComparison.Ordinal) ? HttpStatusCode.Created : HttpStatusCode.OK, "{\"id\":\"01PDF\",\"name\":\"juni.csv\",\"@microsoft.graph.downloadUrl\":\"https://kafe.sharepoint.com/d\"}"),
                };
                Task<List<ConnectorRecord>> Files(string operation, Dictionary<string, string> parameters, bool folder = true) =>
                    Contexts.DrainAsync(new MicrosoftSharePointConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(site), "", operation, parameters, new Dictionary<string, string> { ["site"] = Site }, Credentials) with
                    {
                        Files = folder ? PipelineConnectorFiles.Under(root, 1 << 20) : null,
                    });

                var downloaded = Assert.Single(await Files("download_file", new() { ["item"] = "/Kvitton/1042.pdf", ["file"] = "kvitton/" })).Fields; // no list: a file of the site's documents
                Assert.Equal(new[] { $"{documents}/root:/Kvitton/1042.pdf:", $"{documents}/root:/Kvitton/1042.pdf:/content", Download }, site.Requests.Skip(1).Select(request => request.Url));
                Assert.Equal(new[] { "Bearer eyJ.sp", "Bearer eyJ.sp", null }, site.Requests.Skip(1).Select(request => request.Authorization)); // the download address takes no token
                Assert.Equal(pdf, File.ReadAllBytes(Path.Combine(root, "kvitton", "1042.pdf")));
                Assert.Equal(("01PDF", "kvitton/1042.pdf", 8L, "application/pdf"), ((string?)downloaded["id"], (string?)downloaded["file"], (long?)downloaded["size"], (string?)downloaded["mimeType"]));

                File.WriteAllText(Path.Combine(root, "juni.csv"), "id;summa\n1042;99,50\n");
                site.Requests.Clear();
                var uploaded = Assert.Single(await Files("upload_file", new() { ["file"] = "juni.csv", ["folder"] = "/Rapporter/2026", ["conflict"] = "fail" })).Fields;
                var put = site.Requests.Single(request => request.Url != TokenUrl);
                Assert.Equal((HttpMethod.Put, $"{documents}/root:/Rapporter/2026/juni.csv:/content?@microsoft.graph.conflictBehavior=fail", "id;summa\n1042;99,50\n"), (put.Method, put.Url, Encoding.UTF8.GetString(put.Body)));
                Assert.Equal(("01PDF", false), ((string?)uploaded["id"], uploaded.ContainsKey("@microsoft.graph.downloadUrl")));
                await Files("upload_file", new() { ["name"] = "kassa.txt", ["text"] = "Kassan är nere" }, folder: false);
                Assert.Equal($"{documents}/root:/kassa.txt:/content?@microsoft.graph.conflictBehavior=replace", site.Requests.Last().Url); // the documents' root; a name in use replaced

                site.Requests.Clear();
                await Files("update_file", new() { ["item"] = "01PDF", ["text"] = "id;summa\n", ["name"] = "betald.csv" }, folder: false);
                Assert.Equal(new[] { (HttpMethod.Put, $"{documents}/items/01PDF/content", "id;summa\n"), (HttpMethod.Patch, $"{documents}/items/01PDF", "{\"name\":\"betald.csv\"}") }, site.Requests.Skip(1).Select(request => (request.Method, request.Url, Encoding.UTF8.GetString(request.Body))));

                site.Requests.Clear();
                foreach (var (operation, parameters, refusal) in new[]
                {
                    ("download_file", new Dictionary<string, string> { ["file"] = "x" }, "Give the file - its id, or its path from the documents' root (item)."),
                    ("download_file", new() { ["item"] = "/Kvitton/../Lön" , ["file"] = "x" }, "'/Kvitton/../Lön' is not a path from the drive's root - /Kvitton/2026/1042.pdf (item)."),
                    ("download_file", new() { ["item"] = "01PDF" }, "Give the file - where in the site's pipeline folder it is written."),
                    ("upload_file", new() { ["name"] = "a.csv" }, "Give the file in the site's pipeline folder to store, or text."),
                    ("upload_file", new() { ["text"] = "x", ["name"] = "a\\b.csv" }, "'a\\b.csv' is not a file's name - one name, no slash."),
                    ("upload_file", new() { ["text"] = "x", ["name"] = "a.csv", ["conflict"] = "keep" }, "'keep' is not replace, rename or fail (conflict)."),
                    ("update_file", new() { ["item"] = "01PDF" }, "Give a new name, or new content - a file or text."),
                    ("update_file", new() { ["item"] = "01PDF", ["name"] = "Arkiv/b.csv" }, "'Arkiv/b.csv' is not a file's name - one name, no slash."),
                    ("update_file", new() { ["item"] = "01PDF", ["file"] = "a.csv", ["text"] = "x" }, "Give the file or text, not both."),
                })
                {
                    Assert.Equal(refusal, (await Assert.ThrowsAsync<InvalidOperationException>(() => Files(operation, parameters))).Message);
                }

                Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Files("download_file", new() { ["item"] = "01PDF", ["file"] = "x" }, folder: false))).Message);
                Assert.Empty(site.Requests); // each refused before a token was asked for

                site.Answer = (method, url) => method == HttpMethod.Get && url.EndsWith("/content", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://kafe.example.com/x") } } : Json(HttpStatusCode.OK, "{\"id\":\"01PDF\",\"name\":\"1042.pdf\"}");
                Assert.Equal("Microsoft SharePoint named a download address that is not Microsoft's (kafe.example.com) - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Files("download_file", new() { ["item"] = "01PDF", ["file"] = "y.pdf" }))).Message);
                Assert.False(File.Exists(Path.Combine(root, "y.pdf")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task A_test_names_the_site()
        {
            Task<ConnectionTestResult> Test(Graph graph, Dictionary<string, string>? settings = null) =>
                new MicrosoftSharePointConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(graph), "", settings, Credentials));
            var root = new Graph { Answer = (_, _) => (HttpStatusCode.OK, "{\"displayName\":\"Kafé\",\"webUrl\":\"https://kafe.sharepoint.com\"}") };
            Assert.Equal((true, "Microsoft SharePoint: signed in - the site Kafé at https://kafe.sharepoint.com."), ((await Test(root)).Success, (await Test(root)).Message));
            Assert.Equal($"{Api}/sites/root?$select=displayName,webUrl", root.Requests[1].Url);
            var kassa = new Graph { Answer = (_, _) => (HttpStatusCode.OK, "{\"displayName\":\"Kassa\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa\"}") };
            Assert.Equal("Microsoft SharePoint: signed in - the site Kassa at https://kafe.sharepoint.com/sites/Kassa.", (await Test(kassa, new() { ["site"] = "kafe.sharepoint.com:/sites/Kassa" })).Message);
            Assert.Equal($"{Api}/sites/{Site}?$select=displayName,webUrl", kassa.Requests[1].Url);
            var refused = await Test(new Graph { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Either scp or roles claim need to be present in the token.\"}}") });
            Assert.Equal((false, "HTTP 403 accessDenied: Either scp or roles claim need to be present in the token."), (refused.Success, refused.Message));
            var connector = new MicrosoftSharePointConnector();
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "microsoftsharepoint", 11), (connector.AuthType, connector.ConnectorType, connector.Operations.Count));
        }
    }
}
