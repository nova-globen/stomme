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
using Stomme.Modules.Connectors.MicrosoftOneDrive;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft OneDrive (n8n's Microsoft OneDrive node) and the Graph drive files it shares with SharePoint
    /// (<see cref="GraphDrive"/>): the app's client credentials for a Graph token; a user's drive or a drive by its id; items by id or
    /// by path; a search and a folder's items page by page, files or folders, Graph's next link kept to the collection; a download
    /// into the run's folder through Graph's redirect - followed once, to Microsoft's host alone, without the token - a folder and
    /// a file over the cap refused; an upload of the whole file, again after a throttle; an update, a copy said not followed, a
    /// move, a delete, a share link and a path of folders made one by one; what could go wrong refused before a token is asked
    /// for; Graph's code and message on a failure.
    /// </summary>
    public sealed class MicrosoftOneDriveConnectorTests : IDisposable
    {
        private const string Api = "https://graph.microsoft.com/v1.0";
        private const string Drive = Api + "/users/ada%40kafe.example.com/drive";
        private const string TokenUrl = "https://login.microsoftonline.com/kafe-tenant/oauth2/v2.0/token";
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = TokenUrl };
        private static readonly Dictionary<string, string> Settings = new() { ["user"] = "ada@kafe.example.com" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-onedrive-" + Guid.NewGuid().ToString("N"));

        public MicrosoftOneDriveConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(HttpMethod Method, string Url, string? Authorization, string? ContentType, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);
        }

        private sealed class Graph : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return sent.Url == TokenUrl ? Json(HttpStatusCode.OK, "{\"access_token\":\"eyJ.od\",\"expires_in\":3599,\"token_type\":\"Bearer\"}") : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

        private static IEnumerable<Sent> Calls(Graph graph) => graph.Requests.Where(sent => sent.Url != TokenUrl);

        private Task<List<ConnectorRecord>> Run(Graph graph, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, bool files = true, long cap = 1 << 20) =>
            Contexts.DrainAsync(new MicrosoftOneDriveConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(graph), "", operation, parameters, settings ?? Settings, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, bool files = true)
        {
            var graph = new Graph();
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(graph, operation, parameters, settings, files));
            Assert.Empty(graph.Requests); // refused before a token was asked for
            return refusal.Message;
        }

        private static string Page(string? next, params string[] names) =>
            "{" + (next is null ? "" : "\"@odata.nextLink\":\"" + next + "\",") + "\"value\":[" + string.Join(",", names.Select((name, i) => "{\"id\":\"01ITEM" + i + "\",\"name\":\"" + name + "\"," + (name.EndsWith('/') ? "\"folder\":{\"childCount\":2}" : "\"file\":{\"mimeType\":\"text/csv\"},\"size\":35") + ",\"@microsoft.graph.downloadUrl\":\"https://kafe-my.sharepoint.com/x\"}")) + "]}";

        [Fact]
        public async Task Items_by_id_or_by_path_a_search_and_a_folders_items_page_by_page()
        {
            var search = $"{Drive}/root/search(q='Kaf%C3%A9%27%27s')";
            var graph = new Graph { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("skiptoken", StringComparison.Ordinal) ? Page(null, "kvitto 3.csv") : Page(search + "?$skiptoken=2", "kvitto 1.csv", "Arkiv/", "kvitto 2.csv")) };
            var files = await Run(graph, "search", new() { ["query"] = "Kafé's", ["what"] = "files" });
            Assert.Equal(new[] { "kvitto 1.csv", "kvitto 2.csv", "kvitto 3.csv" }, files.Select(record => (string?)record.Fields["name"])); // files alone, over two pages
            Assert.Equal(("POST", TokenUrl, "grant_type=client_credentials&client_id=app-1&client_secret=secret-1&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default"), (graph.Requests[0].Method.Method, graph.Requests[0].Url, graph.Requests[0].Text));
            Assert.Equal(new[] { search, search + "?$skiptoken=2" }, Calls(graph).Select(sent => sent.Url)); // the query's quote doubled, as OData asks
            Assert.All(Calls(graph), sent => Assert.Equal("Bearer eyJ.od", sent.Authorization));
            Assert.False(files[0].Fields.ContainsKey("@microsoft.graph.downloadUrl")); // the pre-authenticated address never a record's

            var folders = new Graph { Answer = _ => Json(HttpStatusCode.OK, Page(null, "kvitto 1.csv", "Arkiv/")) };
            Assert.Equal("Arkiv/", (string?)Assert.Single(await Run(folders, "search", new() { ["query"] = "a", ["what"] = "folders" })).Fields["name"]);

            var children = new Graph { Answer = _ => Json(HttpStatusCode.OK, Page(null, "a.csv", "b.csv", "c.csv")) };
            Assert.Equal(2, (await Run(children, "children", new() { ["folder"] = "/Kvitton/2026 ä", ["max"] = "2" })).Count); // no further than asked
            Assert.Equal($"{Drive}/root:/Kvitton/2026%20%C3%A4:/children", Calls(children).Single().Url); // a path, each segment encoded
            await Run(children, "children", new());
            Assert.Equal($"{Drive}/root/children", Calls(children).Last().Url); // the drive's root
            await Run(children, "children", new() { ["folder"] = "/Kvitton/#1042?100%" });
            Assert.Equal($"{Drive}/root:/Kvitton/%231042%3F100%25:/children", Calls(children).Last().Url); // what would end the path or start a query, escaped

            var elsewhere = new Graph { Answer = _ => Json(HttpStatusCode.OK, Page("https://evil.example.com/v1.0/x", "a.csv")) };
            Assert.Equal("Microsoft Graph named a next page outside the collection - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elsewhere, "children", new()))).Message);

            var one = new Graph { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"a.csv\",\"size\":35,\"parentReference\":{\"id\":\"01P\"}}") };
            var item = Assert.Single(await Run(one, "get", new() { ["item"] = "01ABC!12" }, new() { ["drive"] = "b!kafeDrive_1" })).Fields;
            Assert.Equal($"{Api}/drives/b%21kafeDrive_1/items/01ABC%2112", Calls(one).Single().Url); // a drive by its id; an item by its id
            Assert.Equal(("a.csv", 35L, "{\"id\":\"01P\"}"), ((string?)item["name"], (long?)item["size"], (string?)item["parentReference"]));
        }

        [Fact]
        public async Task A_download_follows_graphs_redirect_once_to_microsofts_host_without_the_token()
        {
            var csv = Encoding.UTF8.GetBytes("id;kund;summa\n1042;Kafé Ada;99,50\n");
            const string Download = "https://kafe-my.sharepoint.com/personal/ada/_layouts/15/download.aspx?UniqueId=1&tempauth=v1.x";
            var graph = new Graph
            {
                Answer = sent => sent.Url.StartsWith("https://kafe-my.sharepoint.com/", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(csv) }
                    : sent.Url.EndsWith("/content", StringComparison.Ordinal) ? Redirect(Download)
                    : Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"kvitto 1042.csv\",\"size\":35,\"file\":{\"mimeType\":\"text/csv\"}}"),
            };
            var record = Assert.Single(await Run(graph, "download", new() { ["item"] = "/Kvitton/kvitto 1042.csv", ["file"] = "downloads/" })).Fields;
            var calls = Calls(graph).ToList();
            Assert.Equal(new[] { $"{Drive}/root:/Kvitton/kvitto%201042.csv:", $"{Drive}/root:/Kvitton/kvitto%201042.csv:/content", Download }, calls.Select(sent => sent.Url));
            Assert.Null(calls[2].Authorization); // the pre-authenticated address takes no token
            Assert.Equal(csv, File.ReadAllBytes(Path.Combine(_root, "downloads", "kvitto 1042.csv"))); // its own name, the very bytes
            Assert.Equal(("01ABC", "downloads/kvitto 1042.csv", 35L, "text/csv"), ((string?)record["id"], (string?)record["file"], (long?)record["size"], (string?)record["mimeType"]));

            foreach (var (to, refusal) in new[]
            {
                ("https://evil.example.com/steal?tempauth=x", "Microsoft OneDrive named a download address that is not Microsoft's (evil.example.com) - it is not followed."),
                ("http://kafe-my.sharepoint.com/x", "Microsoft OneDrive named a download address that is not Microsoft's (kafe-my.sharepoint.com) - it is not followed."),
                ("https://kafe-my.sharepoint.com.evil.example.com/x", "Microsoft OneDrive named a download address that is not Microsoft's (kafe-my.sharepoint.com.evil.example.com) - it is not followed."),
            })
            {
                var away = new Graph { Answer = sent => sent.Url.EndsWith("/content", StringComparison.Ordinal) ? Redirect(to) : Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"x.csv\",\"size\":3}") };
                Assert.Equal(refusal, (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(away, "download", new() { ["item"] = "01ABC", ["file"] = "x.csv" }))).Message);
                Assert.DoesNotContain(Calls(away), sent => sent.Url == to); // never asked
                Assert.False(File.Exists(Path.Combine(_root, "x.csv")));
            }

            var twice = new Graph { Answer = sent => sent.Url.EndsWith("/content", StringComparison.Ordinal) ? Redirect(Download) : sent.Url == Download ? Redirect("https://kafe-my.sharepoint.com/again") : Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"x.csv\"}") };
            Assert.Equal("Microsoft OneDrive's download address redirected again - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(twice, "download", new() { ["item"] = "01ABC", ["file"] = "x.csv" }))).Message);

            var folder = new Graph { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"01F\",\"name\":\"Arkiv\",\"folder\":{\"childCount\":3}}") };
            Assert.Equal("'Arkiv' is a folder; a folder is not downloaded.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(folder, "download", new() { ["item"] = "01F", ["file"] = "x" }))).Message);
            var big = new Graph { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"01B\",\"name\":\"stor.zip\",\"size\":100000000,\"file\":{}}") };
            Assert.Equal("'stor.zip' is 100000000 bytes, over the pipeline folder's 1048576; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["item"] = "01B", ["file"] = "x" }))).Message);
            Assert.DoesNotContain(Calls(big), sent => sent.Url.EndsWith("/content", StringComparison.Ordinal)); // refused by its size before its bytes were asked for
        }

        [Fact]
        public async Task An_upload_puts_the_whole_file_again_after_a_throttle_and_an_update_its_content_then_its_name()
        {
            var csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
            Directory.CreateDirectory(Path.Combine(_root, "reports"));
            File.WriteAllText(Path.Combine(_root, "reports", "juni.csv"), csv);
            var attempts = 0;
            var graph = new Graph
            {
                Answer = _ =>
                {
                    if (attempts++ == 0)
                    {
                        var slow = Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"serviceNotAvailable\",\"message\":\"Try later\"}}");
                        slow.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return slow;
                    }

                    return Json(HttpStatusCode.Created, "{\"id\":\"01NEW\",\"name\":\"juni.csv\",\"size\":35,\"@microsoft.graph.downloadUrl\":\"https://x.sharepoint.com/d\"}");
                },
            };
            var record = Assert.Single(await Run(graph, "upload", new() { ["file"] = "reports/juni.csv", ["folder"] = "/Kvitton" })).Fields;
            Assert.Equal(2, Calls(graph).Count());
            Assert.All(Calls(graph), sent =>
            {
                Assert.Equal((HttpMethod.Put, $"{Drive}/root:/Kvitton/juni.csv:/content?@microsoft.graph.conflictBehavior=replace"), (sent.Method, sent.Url)); // its own name, a name in use replaced
                Assert.Equal((csv, "text/csv"), (sent.Text, sent.ContentType)); // the whole file each time
            });
            Assert.Equal("01NEW", (string?)record["id"]);
            Assert.False(record.ContainsKey("@microsoft.graph.downloadUrl"));

            var text = new Graph { Answer = _ => Json(HttpStatusCode.Created, "{\"id\":\"01T\"}") };
            await Run(text, "upload", new() { ["name"] = "kassa.txt", ["text"] = " Kassan är nere \n", ["folder"] = "01FOLDER", ["conflict"] = "Rename" }, files: false);
            var sent = Calls(text).Single();
            Assert.Equal(($"{Drive}/items/01FOLDER:/kassa.txt:/content?@microsoft.graph.conflictBehavior=rename", " Kassan är nere \n", "text/plain; charset=utf-8"), (sent.Url, sent.Text, sent.ContentType)); // under a folder by its id; the text as given

            var update = new Graph { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"betald.csv\"}") };
            Assert.Equal("betald.csv", (string?)Assert.Single(await Run(update, "update", new() { ["item"] = "01ABC", ["file"] = "reports/juni.csv", ["name"] = "betald.csv" })).Fields["name"]);
            Assert.Equal(new[] { (HttpMethod.Put, $"{Drive}/items/01ABC/content"), (HttpMethod.Patch, $"{Drive}/items/01ABC") }, Calls(update).Select(call => (call.Method, call.Url))); // its content, then its name
            Assert.Equal("{\"name\":\"betald.csv\"}", Calls(update).Last().Text);
            await Run(update, "update", new() { ["item"] = "/a.csv", ["name"] = "b.csv" });
            Assert.Equal((HttpMethod.Patch, $"{Drive}/root:/a.csv:"), (Calls(update).Last().Method, Calls(update).Last().Url)); // a name alone, no content sent

            Assert.Equal("'reports/none.csv' is not a file in the site's pipeline folder.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Graph(), "upload", new() { ["file"] = "reports/none.csv" }))).Message);
        }

        [Fact]
        public async Task Copy_move_delete_share_and_a_path_of_folders()
        {
            var folders = 0;
            var graph = new Graph
            {
                Answer = sent => sent.Url.EndsWith("/copy", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { Location = new Uri("https://kafe-my.sharepoint.com/_api/v2.0/monitor/abc") } }
                    : sent.Url.EndsWith("/createLink", StringComparison.Ordinal) ? Json(HttpStatusCode.Created, "{\"id\":\"L1\",\"roles\":[\"read\"],\"link\":{\"type\":\"view\",\"scope\":\"anonymous\",\"webUrl\":\"https://1drv.ms/x\"}}")
                    : sent.Url.Contains("$select=id", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, "{\"id\":\"01ARKIV\"}")
                    : sent.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : sent.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, "{\"id\":\"01F" + ++folders + "\",\"name\":\"x\"}")
                    : Json(HttpStatusCode.OK, "{\"id\":\"01ABC\",\"name\":\"flyttad.csv\"}"),
            };
            var copied = Assert.Single(await Run(graph, "copy", new() { ["item"] = "01ABC", ["name"] = "kopia.csv", ["folder"] = "01ARKIV", ["drive"] = "b!other" })).Fields;
            Assert.Equal("{\"name\":\"kopia.csv\",\"parentReference\":{\"driveId\":\"b!other\",\"id\":\"01ARKIV\"}}", Calls(graph).Last().Text);
            Assert.Equal((202, "https://kafe-my.sharepoint.com/_api/v2.0/monitor/abc"), ((int?)copied["status"], (string?)copied["location"])); // the copy's monitor said, never fetched
            Assert.DoesNotContain(graph.Requests, sent => sent.Url.Contains("monitor", StringComparison.Ordinal));

            graph.Requests.Clear();
            await Run(graph, "move", new() { ["item"] = "01ABC", ["folder"] = "/Arkiv", ["name"] = "flyttad.csv" });
            Assert.Equal(new[] { (HttpMethod.Get, $"{Drive}/root:/Arkiv:?$select=id", ""), (HttpMethod.Patch, $"{Drive}/items/01ABC", "{\"parentReference\":{\"id\":\"01ARKIV\"},\"name\":\"flyttad.csv\"}") }, Calls(graph).Select(sent => (sent.Method, sent.Url, sent.Text))); // the folder's id found by its path

            Assert.True((bool?)Assert.Single(await Run(graph, "delete", new() { ["item"] = "01ABC" })).Fields["deleted"]);
            Assert.Equal((HttpMethod.Delete, $"{Drive}/items/01ABC"), (Calls(graph).Last().Method, Calls(graph).Last().Url));

            var link = Assert.Single(await Run(graph, "share", new() { ["item"] = "01ABC", ["type"] = "View", ["scope"] = "anonymous" })).Fields;
            Assert.Equal("{\"type\":\"view\",\"scope\":\"anonymous\"}", Calls(graph).Last().Text);
            Assert.Equal(("https://1drv.ms/x", "view", "anonymous"), ((string?)link["link"], (string?)link["type"], (string?)link["scope"]));

            graph.Requests.Clear();
            await Run(graph, "create_folder", new() { ["name"] = "Kvitton/2026", ["folder"] = "/Arkiv" });
            var made = Calls(graph).ToList();
            Assert.Equal(new[] { $"{Drive}/root:/Arkiv:/children", $"{Drive}/items/01F1/children" }, made.Select(sent => sent.Url)); // each in the one before it
            Assert.Equal(new[] { "{\"name\":\"Kvitton\",\"folder\":{},\"@microsoft.graph.conflictBehavior\":\"fail\"}", "{\"name\":\"2026\",\"folder\":{},\"@microsoft.graph.conflictBehavior\":\"fail\"}" }, made.Select(sent => sent.Text));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_failures_say_graphs_code()
        {
            Assert.StartsWith("Microsoft OneDrive has no operation 'sync' - search, children, get,", await Refused("sync", new()), StringComparison.Ordinal);
            Assert.Equal("Give the item - its id, or its path from the drive's root.", await Refused("get", new()));
            Assert.Equal("'01 ABC' is not an item - its id, or its path from the drive's root, /Kvitton/1042.pdf (item).", await Refused("get", new() { ["item"] = "01 ABC" }));
            Assert.Equal("'/Kvitton/../Hemligt' is not a path from the drive's root - /Kvitton/2026/1042.pdf (item).", await Refused("get", new() { ["item"] = "/Kvitton/../Hemligt" }));
            Assert.Equal("'/a//b' is not a path from the drive's root - /Kvitton/2026/1042.pdf (folder).", await Refused("children", new() { ["folder"] = "/a//b" }));
            Assert.Equal("'a/b.csv' is not a file's name - one name, no slash.", await Refused("upload", new() { ["name"] = "a/b.csv", ["text"] = "x" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["name"] = "a.csv" }));
            Assert.Equal("Give the file or text, not both.", await Refused("upload", new() { ["file"] = "a.csv", ["text"] = "x" }));
            Assert.Equal("Give the name - text has none of its own.", await Refused("upload", new() { ["text"] = "x" }));
            Assert.Equal("'keep' is not replace, rename or fail (conflict).", await Refused("upload", new() { ["name"] = "a.csv", ["text"] = "x", ["conflict"] = "keep" }));
            Assert.Equal("'pdf' is not a media type - application/pdf (content_type).", await Refused("upload", new() { ["name"] = "a.csv", ["text"] = "x", ["content_type"] = "pdf" }));
            Assert.Equal("Give a new name, or new content - a file or text.", await Refused("update", new() { ["item"] = "01ABC" }));
            Assert.Equal("'Arkiv/b.csv' is not a file's name - one name, no slash.", await Refused("update", new() { ["item"] = "01ABC", ["name"] = "Arkiv/b.csv" }));
            Assert.Equal("'..' is not a file's name - one name, no slash.", await Refused("move", new() { ["item"] = "01ABC", ["name"] = ".." }));
            Assert.Equal("Give the folder it moves to, or its new name.", await Refused("move", new() { ["item"] = "01ABC" }));
            Assert.Equal("'x/y' is not a folder's id (folder).", await Refused("copy", new() { ["item"] = "01ABC", ["folder"] = "x/y" }));
            Assert.Equal("Give the link - view, edit or embed.", await Refused("share", new() { ["item"] = "01ABC", ["scope"] = "anonymous" }));
            Assert.Equal("'everyone' is not anonymous or organization (scope).", await Refused("share", new() { ["item"] = "01ABC", ["type"] = "view", ["scope"] = "everyone" }));
            Assert.Equal("'some' is not all, files or folders (what).", await Refused("search", new() { ["query"] = "a", ["what"] = "some" }));
            Assert.Equal("Give the query - what to find.", await Refused("search", new()));
            Assert.Equal("'..' is not a file's name - one name, no slash.", await Refused("create_folder", new() { ["name"] = "Arkiv/.." }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("children", new() { ["max"] = "0" }));
            Assert.Equal("Give the file - where in the site's pipeline folder it is written.", await Refused("download", new() { ["item"] = "01ABC" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["item"] = "01ABC", ["file"] = "x" }, files: false));
            Assert.Equal("Give whose OneDrive - the user's address or id - or a drive's id.", await Refused("children", new(), new()));
            Assert.Equal("'ada' is not a user - an address or a user id.", await Refused("children", new(), new() { ["user"] = "ada" }));

            var missing = new Graph { Answer = _ => Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The resource could not be found.\"}}") };
            Assert.Equal("HTTP 404 itemNotFound: The resource could not be found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "get", new() { ["item"] = "01ABC" }))).Message);
            var signIn = new Graph { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") } };
            Assert.Equal("Microsoft OneDrive answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(signIn, "children", new()))).Message);

            var drive = new Graph { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"b!1\",\"driveType\":\"business\",\"owner\":{\"user\":{\"displayName\":\"Ada\"}}}") };
            var test = await new MicrosoftOneDriveConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(drive), "", Settings, Credentials));
            Assert.Equal((true, "OneDrive: Ada's drive answers."), (test.Success, test.Message));
            Assert.Equal($"{Drive}?$select=id,name,driveType,owner", Calls(drive).Single().Url);
        }
    }
}
