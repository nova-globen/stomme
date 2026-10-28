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
using Stomme.Modules.Connectors.Dropbox;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Dropbox (n8n's Dropbox node): the app's key and secret and a refresh token for a token; a folder listed and a
    /// search page by page through Dropbox's cursor, their entries named as n8n's node names them; a file's or a folder's
    /// metadata, a copy, a move, a folder made and a delete as Dropbox answers them; a download into the run's folder and an
    /// upload from it - or of text - their arguments in the content host's header as ASCII JSON, a file over the folder's cap or
    /// Dropbox's upload limit refused; a team's space read from its root; what could go wrong refused before a token is asked
    /// for; Dropbox's refusals said with its own summary.
    /// </summary>
    public sealed class DropboxConnectorTests : IDisposable
    {
        private const string TokenUrl = DropboxConnector.DefaultTokenUrl;
        private const string Api = "https://api.dropboxapi.com/2/";
        private const string Content = "https://content.dropboxapi.com/2/";
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-key", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-dropbox-" + Guid.NewGuid().ToString("N"));

        public DropboxConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(string Url, string? Authorization, string? Arg, string? PathRoot, string? ContentType, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);
        }

        private sealed class Dropbox : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Post, request.Method); // Dropbox's every call
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), Header("Dropbox-API-Arg"), Header("Dropbox-API-Path-Root"), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return sent.Url == TokenUrl ? Json(HttpStatusCode.OK, "{\"access_token\":\"db-token\",\"expires_in\":14400,\"token_type\":\"bearer\"}") : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static IEnumerable<Sent> Calls(Dropbox dropbox) => dropbox.Requests.Where(sent => sent.Url != TokenUrl);

        private Task<List<ConnectorRecord>> Run(Dropbox dropbox, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, bool files = true, long cap = 1 << 20) =>
            Contexts.DrainAsync(new DropboxConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(dropbox), "", operation, parameters, settings ?? new(), Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, bool files = true, string endpoint = "")
        {
            var dropbox = new Dropbox();
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new DropboxConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(dropbox), endpoint, operation, parameters, settings ?? new(), Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, 1 << 20) : null,
            }));
            Assert.Empty(dropbox.Requests); // refused before a token was asked for
            return refusal.Message;
        }

        private static string Entry(string name, bool folder = false) => folder
            ? "{\".tag\":\"folder\",\"name\":\"" + name + "\",\"path_lower\":\"/kvitton/" + name.ToLowerInvariant() + "\",\"path_display\":\"/Kvitton/" + name + "\",\"id\":\"id:F" + name.Length + "\"}"
            : "{\".tag\":\"file\",\"name\":\"" + name + "\",\"path_lower\":\"/kvitton/" + name.ToLowerInvariant() + "\",\"path_display\":\"/Kvitton/" + name + "\",\"id\":\"id:A" + name.Length + "\",\"client_modified\":\"2026-10-01T08:00:00Z\",\"server_modified\":\"2026-10-01T08:00:05Z\",\"rev\":\"015f\",\"size\":35,\"is_downloadable\":true,\"content_hash\":\"e3b0\"}";

        [Fact]
        public async Task A_folder_is_listed_page_by_page_its_entries_named_as_n8n_names_them()
        {
            var dropbox = new Dropbox
            {
                Answer = sent => Json(HttpStatusCode.OK, sent.Url.EndsWith("/continue", StringComparison.Ordinal)
                    ? "{\"entries\":[" + Entry("kvitto 3.csv") + "],\"cursor\":\"c2\",\"has_more\":false}"
                    : "{\"entries\":[" + Entry("kvitto 1.csv") + "," + Entry("Arkiv", folder: true) + "],\"cursor\":\"c1\",\"has_more\":true}"),
            };
            var records = await Run(dropbox, "list", new() { ["path"] = "Kvitton/", ["recursive"] = "true" });
            Assert.Equal(new[] { "kvitto 1.csv", "Arkiv", "kvitto 3.csv" }, records.Select(record => (string?)record.Fields["name"])); // over two pages
            Assert.Equal((TokenUrl, "grant_type=refresh_token&refresh_token=kafe-refresh&client_id=kafe-key&client_secret=kafe-secret"), (dropbox.Requests[0].Url, dropbox.Requests[0].Text));
            var calls = Calls(dropbox).ToList();
            Assert.Equal((Api + "files/list_folder", "{\"path\":\"/Kvitton\",\"recursive\":true,\"include_deleted\":false,\"include_mounted_folders\":true,\"include_non_downloadable_files\":true,\"include_has_explicit_shared_members\":false,\"limit\":1000}"), (calls[0].Url, calls[0].Text)); // a leading slash added, a trailing one dropped
            Assert.Equal((Api + "files/list_folder/continue", "{\"cursor\":\"c1\"}"), (calls[1].Url, calls[1].Text));
            Assert.All(calls, sent => Assert.Equal(("Bearer db-token", "application/json; charset=utf-8", null), (sent.Authorization, sent.ContentType, sent.PathRoot)));

            var file = records[0].Fields;
            Assert.Equal(("file", "/Kvitton/kvitto 1.csv", "/kvitton/kvitto 1.csv", 35L, "2026-10-01T08:00:05Z", "2026-10-01T08:00:00Z", "015f", "e3b0", true, "id:A12"),
                ((string?)file["type"], (string?)file["pathDisplay"], (string?)file["pathLower"], Convert.ToInt64(file["contentSize"]), (string?)file["lastModifiedServer"], (string?)file["lastModifiedClient"], (string?)file["rev"], (string?)file["contentHash"], (bool?)file["isDownloadable"], (string?)file["id"]));
            Assert.Equal(new[] { "id", "name", "pathDisplay", "pathLower", "type" }, records[1].Fields.Keys.OrderBy(key => key, StringComparer.Ordinal)); // only what Dropbox gave

            var few = new Dropbox { Answer = dropbox.Answer };
            Assert.Equal(2, (await Run(few, "list", new() { ["max"] = "2", ["deleted"] = "true", ["mounted"] = "false", ["non_downloadable"] = "false", ["shared_members"] = "true" })).Count);
            Assert.Equal("{\"path\":\"\",\"recursive\":false,\"include_deleted\":true,\"include_mounted_folders\":false,\"include_non_downloadable_files\":false,\"include_has_explicit_shared_members\":true,\"limit\":2}", Calls(few).Single().Text); // the root; no further than asked

            var one = new Dropbox { Answer = dropbox.Answer };
            Assert.Single(await Run(one, "list", new() { ["max"] = "1" })); // no further than asked within a page, though Dropbox gave two

            var stuck = new Dropbox { Answer = _ => Json(HttpStatusCode.OK, "{\"entries\":[" + Entry("a.csv") + "],\"has_more\":true}") };
            Assert.Single(await Run(stuck, "list", new() { ["path"] = "/" }));
            Assert.Single(Calls(stuck)); // more said but no cursor named: never the same call again
        }

        [Fact]
        public async Task A_search_by_name_category_and_extension_page_by_page()
        {
            static string Match(string name) => "{\"match_type\":{\".tag\":\"filename\"},\"metadata\":{\".tag\":\"metadata\",\"metadata\":" + Entry(name) + "}}";
            var dropbox = new Dropbox
            {
                Answer = sent => Json(HttpStatusCode.OK, sent.Url.EndsWith("continue_v2", StringComparison.Ordinal)
                    ? "{\"matches\":[" + Match("kvitto 2.pdf") + "],\"has_more\":false}"
                    : "{\"matches\":[" + Match("kvitto 1.pdf") + ",{\"match_type\":{\".tag\":\"filename\"},\"metadata\":{\".tag\":\"other\"}}],\"cursor\":\"s1\",\"has_more\":true}"),
            };
            var records = await Run(dropbox, "search", new() { ["query"] = "kvitto å", ["path"] = "/Kvitton", ["categories"] = "PDF, spreadsheet", ["extensions"] = ".pdf, csv" });
            Assert.Equal(new[] { "/Kvitton/kvitto 1.pdf", "/Kvitton/kvitto 2.pdf" }, records.Select(record => (string?)record.Fields["pathDisplay"])); // a match with no metadata left out
            var calls = Calls(dropbox).ToList();
            Assert.Equal((Api + "files/search_v2", "{\"query\":\"kvitto å\",\"options\":{\"max_results\":1000,\"file_status\":\"active\",\"filename_only\":true,\"path\":\"/Kvitton\",\"file_categories\":[\"pdf\",\"spreadsheet\"],\"file_extensions\":[\"pdf\",\"csv\"]}}"), (calls[0].Url, calls[0].Text));
            Assert.Equal((Api + "files/search/continue_v2", "{\"cursor\":\"s1\"}"), (calls[1].Url, calls[1].Text));

            var deleted = new Dropbox { Answer = _ => Json(HttpStatusCode.OK, "{\"matches\":[],\"has_more\":false}") };
            Assert.Empty(await Run(deleted, "search", new() { ["query"] = "x", ["deleted"] = "true", ["contents"] = "true", ["max"] = "5" }));
            Assert.Equal("{\"query\":\"x\",\"options\":{\"max_results\":5,\"file_status\":\"deleted\",\"filename_only\":false}}", Calls(deleted).Single().Text);
        }

        [Fact]
        public async Task Get_copy_move_a_new_folder_and_a_delete_answer_dropboxs_metadata()
        {
            var dropbox = new Dropbox
            {
                Answer = sent => sent.Url switch
                {
                    Api + "files/get_metadata" => Json(HttpStatusCode.OK, Entry("1042.pdf")),
                    Api + "files/create_folder_v2" => Json(HttpStatusCode.OK, "{\"metadata\":{\"name\":\"2026\",\"path_display\":\"/Kvitton/2026\",\"id\":\"id:F2026\"}}"),
                    Api + "files/delete_v2" => Json(HttpStatusCode.OK, "{\"metadata\":" + Entry("old.pdf") + "}"),
                    _ => Json(HttpStatusCode.OK, "{\"metadata\":" + Entry("1042 (1).pdf") + "}"),
                },
            };
            var got = Assert.Single(await Run(dropbox, "get", new() { ["path"] = "id:a4ayc_80_OEAAAAAAAAAXw" })).Fields;
            Assert.Equal(("file", "1042.pdf", "/Kvitton/1042.pdf", 35L), ((string?)got["type"], (string?)got["name"], (string?)got["path_display"], Convert.ToInt64(got["size"]))); // Dropbox's own names
            Assert.Equal("{\"path\":\"id:a4ayc_80_OEAAAAAAAAAXw\"}", Calls(dropbox).Last().Text); // an id as it is

            var copied = Assert.Single(await Run(dropbox, "copy", new() { ["from"] = "/Kvitton/1042.pdf", ["to"] = "/Arkiv/1042.pdf" })).Fields;
            Assert.Equal((Api + "files/copy_v2", "{\"from_path\":\"/Kvitton/1042.pdf\",\"to_path\":\"/Arkiv/1042.pdf\",\"autorename\":false}"), (Calls(dropbox).Last().Url, Calls(dropbox).Last().Text));
            Assert.Equal("1042 (1).pdf", copied["name"]);
            await Run(dropbox, "move", new() { ["from"] = "/Kvitton/1042.pdf", ["to"] = "Arkiv/1042.pdf", ["autorename"] = "true" });
            Assert.Equal((Api + "files/move_v2", "{\"from_path\":\"/Kvitton/1042.pdf\",\"to_path\":\"/Arkiv/1042.pdf\",\"autorename\":true}"), (Calls(dropbox).Last().Url, Calls(dropbox).Last().Text));

            var folder = Assert.Single(await Run(dropbox, "create_folder", new() { ["path"] = "/Kvitton/2026" })).Fields;
            Assert.Equal((Api + "files/create_folder_v2", "{\"path\":\"/Kvitton/2026\",\"autorename\":false}"), (Calls(dropbox).Last().Url, Calls(dropbox).Last().Text));
            Assert.Equal(("folder", "/Kvitton/2026"), ((string?)folder["type"], (string?)folder["path_display"])); // a new folder's kind said, Dropbox naming none

            var deleted = Assert.Single(await Run(dropbox, "delete", new() { ["path"] = "/Kvitton/old.pdf" })).Fields;
            Assert.Equal((Api + "files/delete_v2", "{\"path\":\"/Kvitton/old.pdf\"}"), (Calls(dropbox).Last().Url, Calls(dropbox).Last().Text));
            Assert.Equal((true, "old.pdf"), ((bool?)deleted["deleted"], (string?)deleted["name"]));
        }

        [Fact]
        public async Task A_download_is_written_into_the_run_folder_its_argument_in_ascii_json()
        {
            var bytes = Encoding.UTF8.GetBytes("nummer;summa\n1042;99,50\n");
            var dropbox = new Dropbox
            {
                Answer = _ =>
                {
                    var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    answer.Headers.TryAddWithoutValidation("Dropbox-API-Result", "{\"name\":\"kvitto \\u00e5:1.csv\",\"path_display\":\"/Kvitton/kvitto \\u00e5:1.csv\",\"id\":\"id:K1\",\"size\":" + bytes.Length + ",\"rev\":\"01\"}");
                    return answer;
                },
            };
            var record = Assert.Single(await Run(dropbox, "download", new() { ["path"] = "/Kvitton/kvitto å:1.csv", ["file"] = "downloads/" })).Fields;
            var sent = Calls(dropbox).Single();
            Assert.Equal((Content + "files/download", "{\"path\":\"/Kvitton/kvitto \\u00E5:1.csv\"}", 0), (sent.Url, sent.Arg, sent.Body.Length)); // the argument in its header, ASCII alone; no body
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "downloads", "kvitto å_1.csv"))); // its own name, made safe; the very bytes
            Assert.Equal(("downloads/kvitto å_1.csv", (long)bytes.Length, "file", "id:K1"), ((string?)record["file"], Convert.ToInt64(record["size"]), (string?)record["type"], (string?)record["id"]));

            await Run(dropbox, "download", new() { ["path"] = "/a.csv", ["file"] = "in/kassa.csv" });
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "in", "kassa.csv")));

            var big = new Dropbox
            {
                Answer = _ =>
                {
                    var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    answer.Headers.TryAddWithoutValidation("Dropbox-API-Result", "{\"name\":\"stor.zip\",\"size\":2048}");
                    return answer;
                },
            };
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["path"] = "/stor.zip", ["file"] = "stor.zip" }, cap: 1024));
            Assert.Equal("'stor.zip' is 2048 bytes, over the pipeline folder's 1024; it is not fetched.", refused.Message);
            Assert.False(File.Exists(Path.Combine(_root, "stor.zip")));
        }

        [Fact]
        public async Task An_upload_of_a_file_or_of_text_overwrites_as_n8ns_node_does_or_is_added_beside()
        {
            const string csv = "nummer;summa\n1042;99,50\n";
            Directory.CreateDirectory(Path.Combine(_root, "reports"));
            File.WriteAllText(Path.Combine(_root, "reports", "juni.csv"), csv);
            var attempts = 0;
            var dropbox = new Dropbox
            {
                Answer = _ =>
                {
                    if (attempts++ == 0)
                    {
                        var slow = Json(HttpStatusCode.TooManyRequests, "{\"error_summary\":\"too_many_write_operations/\"}");
                        slow.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return slow;
                    }

                    return Json(HttpStatusCode.OK, "{\"name\":\"juni.csv\",\"path_display\":\"/Rapporter/juni.csv\",\"id\":\"id:J\",\"size\":24}");
                },
            };
            var record = Assert.Single(await Run(dropbox, "upload", new() { ["file"] = "reports/juni.csv", ["path"] = "/Rapporter/", ["mode"] = "Add", ["autorename"] = "true" })).Fields;
            Assert.Equal(2, Calls(dropbox).Count());
            Assert.All(Calls(dropbox), sent =>
            {
                Assert.Equal((Content + "files/upload", "{\"path\":\"/Rapporter/juni.csv\",\"mode\":\"add\",\"autorename\":true,\"mute\":false}"), (sent.Url, sent.Arg)); // its own name in the folder
                Assert.Equal((csv, "application/octet-stream"), (sent.Text, sent.ContentType)); // the whole file each time
            });
            Assert.Equal(("file", "/Rapporter/juni.csv"), ((string?)record["type"], (string?)record["path_display"])); // a stored file's kind said

            var text = new Dropbox { Answer = _ => Json(HttpStatusCode.OK, "{\"name\":\"hej.txt\"}") };
            await Run(text, "upload", new() { ["path"] = "Anteckningar/hej på dig.txt", ["text"] = " Kassan är nere \n" }, files: false);
            var sent = Calls(text).Single();
            Assert.Equal(("{\"path\":\"/Anteckningar/hej p\\u00E5 dig.txt\",\"mode\":\"overwrite\",\"autorename\":false,\"mute\":false}", " Kassan är nere \n"), (sent.Arg, sent.Text)); // n8n's overwrite; the text as given
        }

        [Fact]
        public async Task A_teams_space_is_read_from_its_root_and_a_test_names_the_account()
        {
            var dropbox = new Dropbox
            {
                Answer = sent => sent.Url == Api + "users/get_current_account"
                    ? Json(HttpStatusCode.OK, "{\"account_id\":\"dbid:1\",\"name\":{\"display_name\":\"Ada Lovelace\"},\"root_info\":{\".tag\":\"team\",\"root_namespace_id\":\"3235641\",\"home_namespace_id\":\"3235642\"}}")
                    : Json(HttpStatusCode.OK, "{\"entries\":[],\"has_more\":false}"),
            };
            await Run(dropbox, "list", new() { ["path"] = "/Delat" }, new() { ["root"] = "Team" });
            var calls = Calls(dropbox).ToList();
            Assert.Equal((Api + "users/get_current_account", 0, null, null), (calls[0].Url, calls[0].Body.Length, calls[0].ContentType, calls[0].PathRoot)); // no arguments, no body
            Assert.Equal((Api + "files/list_folder", "{\".tag\":\"root\",\"root\":\"3235641\"}"), (calls[1].Url, calls[1].PathRoot)); // the team's root named on every call after

            var test = await new DropboxConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(dropbox), "", settings: new Dictionary<string, string> { ["root"] = "team" }, credentials: Credentials));
            Assert.Equal((true, "Dropbox: Ada Lovelace's account answers."), (test.Success, test.Message));
            Assert.Single(Calls(dropbox).Skip(calls.Count)); // the account read once

            var home = new Dropbox { Answer = dropbox.Answer };
            Assert.True((await new DropboxConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(home), "", credentials: Credentials))).Success);
            Assert.All(Calls(home), sent => Assert.Null(sent.PathRoot)); // the account's own folders: no root named

            var expired = new Dropbox { Answer = _ => Json(HttpStatusCode.Unauthorized, "{\"error_summary\":\"expired_access_token/\",\"error\":{\".tag\":\"expired_access_token\"}}") };
            var failed = await new DropboxConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(expired), "", credentials: Credentials));
            Assert.Equal((false, "Dropbox answered 401: expired_access_token/"), (failed.Success, failed.Message));
        }

        [Fact]
        public async Task Dropboxs_refusals_are_said_with_its_own_summary()
        {
            var missing = new Dropbox { Answer = _ => Json(HttpStatusCode.Conflict, "{\"error_summary\":\"path/not_found/..\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}") };
            Assert.Equal("Dropbox answered 409: path/not_found/..", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "get", new() { ["path"] = "/nej.pdf" }))).Message);
            var bad = new Dropbox { Answer = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Error in call to API function \"files/get_metadata\": request body: path: 'nej' did not match pattern\nmore") } };
            Assert.Equal("Dropbox answered 400: Error in call to API function \"files/get_metadata\": request body: path: 'nej' did not match pattern", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(bad, "get", new() { ["path"] = "/nej" }))).Message);
            var html = new Dropbox { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>") } };
            Assert.Equal("Dropbox answered 200 with something that is not JSON - check the connection's address.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(html, "get", new() { ["path"] = "/a" }))).Message);
            var download = new Dropbox { Answer = _ => Json(HttpStatusCode.Conflict, "{\"error_summary\":\"path/not_file/\"}") };
            Assert.Equal("Dropbox answered 409: path/not_file/", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(download, "download", new() { ["path"] = "/Kvitton", ["file"] = "k" }))).Message);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for()
        {
            Assert.Equal("Dropbox has no operation 'share' - list, search, get, download, upload, create_folder, copy, move or delete.", await Refused("share", new()));
            Assert.Equal("Give a path - /Kvitton/1042.pdf - or an id (path).", await Refused("get", new() { ["path"] = "/" }));
            Assert.Equal("'/Kvitton/../hemligt' is not a path - /Kvitton/2026/1042.pdf (path).", await Refused("delete", new() { ["path"] = "/Kvitton/../hemligt" }));
            Assert.Equal("'/Kvitton//1042.pdf' is not a path - /Kvitton/2026/1042.pdf (path).", await Refused("get", new() { ["path"] = "/Kvitton//1042.pdf" }));
            Assert.Equal("'/Kvitton/a\tb' is not a path - /Kvitton/2026/1042.pdf (to).", await Refused("move", new() { ["from"] = "/a", ["to"] = "/Kvitton/a\tb" }));
            Assert.Equal("Give a path - /Kvitton/1042.pdf - or an id (from).", await Refused("copy", new() { ["to"] = "/b" }));
            Assert.Equal("'id:a/b' is not an id - id: and Dropbox's letters (path).", await Refused("get", new() { ["path"] = "id:a/b" }));
            Assert.Equal("Give the query - what their names contain.", await Refused("search", new()));
            Assert.Equal("The query has a line break - one line only.", await Refused("search", new() { ["query"] = "a\nb" }));
            Assert.Equal("'spam' is not a category - image, document, pdf, spreadsheet, presentation, audio, video, folder, paper, other (categories).", await Refused("search", new() { ["query"] = "a", ["categories"] = "pdf, spam" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("list", new() { ["max"] = "0" }));
            Assert.Equal("Give the file - where in the site's pipeline folder it is written.", await Refused("download", new() { ["path"] = "/a.pdf" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["path"] = "/a.pdf", ["file"] = "a.pdf" }, files: false));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["path"] = "/a.txt" }));
            Assert.Equal("Give the file or text, not both.", await Refused("upload", new() { ["path"] = "/a.txt", ["file"] = "a.txt", ["text"] = "x" }));
            Assert.Equal("Give the path with its name - text has none of its own.", await Refused("upload", new() { ["path"] = "/Anteckningar/", ["text"] = "x" }));
            Assert.Equal("Give the path it is stored at - /Kvitton/1042.pdf.", await Refused("upload", new() { ["text"] = "x" }));
            Assert.Equal("'replace' is not overwrite or add (mode).", await Refused("upload", new() { ["path"] = "/a.txt", ["text"] = "x", ["mode"] = "replace" }));
            Assert.Equal("'everything' is not home or team (root).", await Refused("list", new(), new() { ["root"] = "everything" }));
            Assert.Equal("'https://api.dropboxapi.com/2' is not Dropbox's API - its address alone, https://api.dropboxapi.com.", await Refused("list", new(), endpoint: "https://api.dropboxapi.com/2"));
        }

        [Fact]
        public async Task A_file_past_dropboxs_upload_limit_is_refused_before_it_is_sent()
        {
            Directory.CreateDirectory(Path.Combine(_root, "stort"));
            await using (var big = File.Create(Path.Combine(_root, "stort", "film.mp4")))
            {
                big.SetLength(DropboxConnector.MaxUploadBytes + 1);
            }

            var dropbox = new Dropbox();
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(dropbox, "upload", new() { ["file"] = "stort/film.mp4", ["path"] = "/Film/" }, cap: DropboxConnector.MaxUploadBytes * 2));
            Assert.Equal($"'film.mp4' is {DropboxConnector.MaxUploadBytes + 1} bytes; Dropbox takes at most {DropboxConnector.MaxUploadBytes} in one upload.", refused.Message);
            Assert.Empty(Calls(dropbox)); // nothing sent
        }
    }
}
