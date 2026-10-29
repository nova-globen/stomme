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
using Stomme.Modules.Connectors.Box;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Box (n8n's Box node) as a server app: Box's Client Credentials Grant with the enterprise or user the app acts as
    /// (box_subject_type and box_subject_id in the form, asked for only once everything is held to its shape); a folder's items
    /// page by page through Box's marker; a search page by page through its offset with n8n's filters; a download that Box sends
    /// to its download host, followed without the token; an upload in one multipart call to the upload host - its attributes,
    /// then its bytes - a taken name refused, or its new version when asked; a copy, a move, a delete; a folder made with its
    /// shared link, updated and deleted with what is in it; a collaboration by address, user or group, n8n's roles read; a shared
    /// link; Box's refusals said with their code and message.
    /// </summary>
    public sealed class BoxConnectorTests : IDisposable
    {
        private const string Api = "https://api.box.com/2.0/";
        private const string Upload = "https://upload.box.com/api/2.0/";
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-app", ["client-secret"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["subject_id"] = "1042" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-box-" + Guid.NewGuid().ToString("N"));

        public BoxConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(string Method, string Url, string? Authorization, string? ContentType, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);
        }

        private sealed class BoxApi : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json("{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return sent.Url == BoxConnector.DefaultTokenUrl ? Json("{\"access_token\":\"box-token\",\"expires_in\":3600}") : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static IEnumerable<Sent> Calls(BoxApi box) => box.Requests.Where(sent => sent.Url != BoxConnector.DefaultTokenUrl);

        private Task<List<ConnectorRecord>> Run(BoxApi box, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new BoxConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(box), "", operation, parameters, settings ?? Settings, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true, Dictionary<string, string>? settings = null)
            {
                var box = new BoxApi();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(box, operation, parameters, files, settings: settings));
                Assert.Empty(box.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Box has no operation 'rename' - list, search, get,", await Refused("rename", new()), StringComparison.Ordinal);
            Assert.Equal("Give the enterprise's id, or the user's, the app acts as (subject_id).", await Refused("list", new(), settings: new()));
            Assert.Equal("'team' is not enterprise or user (subject_type).", await Refused("list", new(), settings: new() { ["subject_id"] = "1042", ["subject_type"] = "team" }));
            Assert.Equal("'abc' is not a Box id - its digits (file_id).", await Refused("get", new() { ["file_id"] = "abc" }));
            Assert.Equal("Give what to search for (query).", await Refused("search", new()));
            Assert.Equal("'note' is not file or folder (type).", await Refused("search", new() { ["query"] = "kvitto", ["type"] = "note" }));
            Assert.Equal("'body' is not name, description, file_content, comments or tag (content_types).", await Refused("search", new() { ["query"] = "kvitto", ["content_types"] = "name, body" }));
            Assert.Equal("Give its name - text has none of its own (name).", await Refused("upload", new() { ["text"] = "x" }));
            Assert.Equal("'a/b.txt' is not a name Box takes - 1 to 255 characters, no slash, not . or .. (name).", await Refused("upload", new() { ["name"] = "a/b.txt", ["text"] = "x" }));
            Assert.Equal("Give where it moves (parent) or its new name (name).", await Refused("move", new() { ["file_id"] = "1" }));
            Assert.Equal("Give what changes - name, description, parent or tags.", await Refused("folder_update", new() { ["folder_id"] = "1" }));
            Assert.Equal("'public' is not open or collaborators (access).", await Refused("folder_create", new() { ["name"] = "Kvitton", ["access"] = "public" }));
            Assert.Equal("Give whom it is shared with - one of a user's address (email), a user's id (user_id) or a group's id (group_id).", await Refused("share", new() { ["item_type"] = "file", ["item_id"] = "1", ["email"] = "ada@kafe.example", ["group_id"] = "2" }));
            Assert.Equal("'owner' is not editor, viewer, previewer, uploader, previewer uploader, viewer uploader or co-owner (role).", await Refused("share", new() { ["item_type"] = "file", ["item_id"] = "1", ["email"] = "ada@kafe.example", ["role"] = "owner" }));
            Assert.Equal("'tomorrow' is not a time - 2026-12-31T00:00:00Z (expires).", await Refused("link", new() { ["item_type"] = "folder", ["item_id"] = "1", ["access"] = "open", ["expires"] = "tomorrow" }));
            Assert.Equal("'page' is not file or folder (item_type).", await Refused("link", new() { ["item_type"] = "page", ["item_id"] = "1", ["access"] = "open" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["file_id"] = "1", ["file"] = "a.pdf" }, files: false));
        }

        [Fact]
        public async Task The_app_acts_as_its_subject_and_a_folder_is_listed_through_boxs_marker()
        {
            var box = new BoxApi
            {
                Answer = sent => Json(sent.Url.Contains("marker=m2", StringComparison.Ordinal)
                    ? "{\"entries\":[{\"type\":\"file\",\"id\":\"3\",\"name\":\"c.pdf\"}]}"
                    : "{\"entries\":[{\"type\":\"file\",\"id\":\"1\",\"name\":\"a.pdf\",\"size\":35,\"path_collection\":{\"total_count\":0}},{\"type\":\"folder\",\"id\":\"2\",\"name\":\"2026\"}],\"next_marker\":\"m2\"}"),
            };
            var items = await Run(box, "list", new() { ["folder_id"] = "0", ["max"] = "3" }, settings: new() { ["subject_id"] = "77", ["subject_type"] = "user" });
            Assert.Equal("grant_type=client_credentials&client_id=kafe-app&client_secret=kafe-secret&box_subject_type=user&box_subject_id=77", box.Requests.First().Text);
            Assert.Equal(new[] { Api + "folders/0/items?usemarker=true&limit=3", Api + "folders/0/items?usemarker=true&limit=1&marker=m2" }, Calls(box).Select(sent => sent.Url));
            Assert.All(Calls(box), sent => Assert.Equal("Bearer box-token", sent.Authorization));
            Assert.Equal(new[] { "a.pdf", "2026", "c.pdf" }, items.Select(item => (string)item.Fields["name"]!));
            Assert.Equal((35L, "{\"total_count\":0}"), ((long)items[0].Fields["size"]!, (string)items[0].Fields["path_collection"]!));

            var search = new BoxApi { Answer = sent => Json(sent.Url.Contains("offset=0", StringComparison.Ordinal) ? "{\"total_count\":3,\"entries\":[{\"id\":\"1\"},{\"id\":\"2\"}]}" : "{\"total_count\":3,\"entries\":[{\"id\":\"3\"}]}") };
            var found = await Run(search, "search", new() { ["query"] = "kvitto å", ["type"] = "File", ["folders"] = "0, 12", ["extensions"] = ".pdf,csv", ["content_types"] = "Name,tag", ["trash"] = "true", ["max"] = "2" });
            Assert.Equal(Api + "search?query=kvitto%20%C3%A5&type=file&ancestor_folder_ids=0,12&file_extensions=pdf%2Ccsv&content_types=name,tag&trash_content=trashed_only&limit=2&offset=0", Calls(search).Single().Url); // max reached: one page
            Assert.Equal(2, found.Count);
            // A stand-in that honours limit and offset over a number of hits: a full page goes on, a short one or the total ends it.
            static Func<Sent, HttpResponseMessage> Hits(int total) => sent =>
            {
                var query = new Uri(sent.Url).Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
                var (limit, offset) = (int.Parse(query["limit"]), int.Parse(query["offset"]));
                return Json("{\"total_count\":" + total + ",\"entries\":[" + string.Join(",", Enumerable.Range(offset, Math.Max(0, Math.Min(limit, total - offset))).Select(n => "{\"id\":\"" + n + "\"}")) + "]}");
            };
            var all = new BoxApi { Answer = Hits(250) };
            Assert.Equal(250, (await Run(all, "search", new() { ["query"] = "kvitto", ["max"] = "300" })).Count);
            Assert.Equal(new[] { "limit=200&offset=0", "limit=100&offset=200" }, Calls(all).Select(sent => sent.Url[(sent.Url.IndexOf("limit=", StringComparison.Ordinal))..])); // on through the offset
            var exact = new BoxApi { Answer = Hits(200) };
            Assert.Equal(200, (await Run(exact, "search", new() { ["query"] = "kvitto", ["max"] = "300" })).Count);
            Assert.Single(Calls(exact)); // the total read: no page asked for past it
        }

        [Fact]
        public async Task A_download_is_followed_to_boxs_download_host_without_the_token()
        {
            var box = new BoxApi();
            box.Answer = sent => sent.Url switch
            {
                var url when url.EndsWith("files/1042?fields=name,size", StringComparison.Ordinal) => Json("{\"name\":\"kvitto 1042.pdf\",\"size\":9}"),
                var url when url.EndsWith("files/1042/content", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://dl.boxcloud.com/d/1/signed/download") } },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf-bytes")) },
            };
            var fetched = Assert.Single(await Run(box, "download", new() { ["file_id"] = "1042", ["file"] = "downloads/" })).Fields;
            var last = Calls(box).Last();
            Assert.Equal(("https://dl.boxcloud.com/d/1/signed/download", null), (last.Url, last.Authorization)); // the token never sent to the download host
            Assert.Equal(("downloads/kvitto 1042.pdf", 9L), ((string)fetched["file"]!, (long)fetched["size"]!));
            Assert.Equal("pdf-bytes", File.ReadAllText(Path.Combine(_root, "downloads", "kvitto 1042.pdf")));

            var big = new BoxApi { Answer = _ => Json("{\"name\":\"stor.bin\",\"size\":64}") };
            Assert.Equal("'stor.bin' is 64 bytes, over the pipeline folder's 16; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["file_id"] = "1", ["file"] = "stor.bin" }, cap: 16))).Message);
            Assert.Single(Calls(big)); // its size read first: nothing fetched
        }

        [Fact]
        public async Task An_upload_goes_to_the_upload_host_and_a_taken_name_is_refused_or_versioned()
        {
            File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
            var box = new BoxApi
            {
                Answer = sent => sent.Url switch
                {
                    Upload + "files/content" => Json("{\"type\":\"error\",\"status\":409,\"code\":\"item_name_in_use\",\"message\":\"Item with the same name already exists\",\"context_info\":{\"conflicts\":{\"type\":\"file\",\"id\":\"555\"}}}", HttpStatusCode.Conflict),
                    _ => Json("{\"total_count\":1,\"entries\":[{\"type\":\"file\",\"id\":\"555\",\"name\":\"juni.csv\",\"size\":26}]}"),
                },
            };
            Assert.Equal("Box answered 409 item_name_in_use: Item with the same name already exists", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(box, "upload", new() { ["file"] = "juni.csv", ["parent"] = "12" }))).Message); // as n8n's node: refused
            var first = Calls(box).Single();
            Assert.StartsWith("multipart/form-data", first.ContentType, StringComparison.Ordinal);
            Assert.Contains("name=attributes", first.Text, StringComparison.Ordinal);
            Assert.Contains("{\"name\":\"juni.csv\",\"parent\":{\"id\":\"12\"}}", first.Text, StringComparison.Ordinal);
            Assert.Contains("datum;summa\n2026-06-01;35\n", first.Text, StringComparison.Ordinal);
            Assert.True(first.Text.IndexOf("name=attributes", StringComparison.Ordinal) < first.Text.IndexOf("name=file", StringComparison.Ordinal)); // its attributes first

            var versioned = Assert.Single(await Run(box, "upload", new() { ["file"] = "juni.csv", ["parent"] = "12", ["new_version"] = "true" })).Fields;
            Assert.Equal(Upload + "files/555/content", Calls(box).Last().Url); // the existing file's new version
            Assert.Equal(("555", 26L), ((string)versioned["id"]!, (long)versioned["size"]!));

            var text = new BoxApi { Answer = _ => Json("{\"entries\":[{\"id\":\"9\",\"name\":\"stängt.txt\"}]}") };
            await Run(text, "upload", new() { ["name"] = "stängt.txt", ["query"] = "Kassan stängd\nÅter 08:00" }, files: false);
            Assert.Contains("{\"name\":\"stängt.txt\",\"parent\":{\"id\":\"0\"}}", Calls(text).Single().Text, StringComparison.Ordinal); // the root by default
            Assert.Contains("Kassan stängd\nÅter 08:00", Calls(text).Single().Text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Files_and_folders_are_copied_moved_made_shared_and_deleted()
        {
            var box = new BoxApi { Answer = sent => sent.Method == "DELETE" ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json("{\"type\":\"folder\",\"id\":\"88\",\"name\":\"Kvitton\",\"shared_link\":{\"url\":\"https://app.box.com/s/abc\",\"access\":\"open\"}}") };
            await Run(box, "copy", new() { ["file_id"] = "1", ["parent"] = "12", ["name"] = "kopia.pdf" });
            Assert.Equal(("POST", Api + "files/1/copy", "{\"parent\":{\"id\":\"12\"},\"name\":\"kopia.pdf\"}"), (Calls(box).Last().Method, Calls(box).Last().Url, Calls(box).Last().Text));
            await Run(box, "move", new() { ["file_id"] = "1", ["name"] = "ny.pdf" });
            Assert.Equal(("PUT", Api + "files/1", "{\"name\":\"ny.pdf\"}"), (Calls(box).Last().Method, Calls(box).Last().Url, Calls(box).Last().Text));
            Assert.True((bool)Assert.Single(await Run(box, "delete", new() { ["file_id"] = "1" })).Fields["deleted"]!);

            var made = Assert.Single(await Run(box, "folder_create", new() { ["name"] = "Kvitton", ["parent"] = "12", ["access"] = "Open" })).Fields;
            var folderCalls = Calls(box).TakeLast(2).ToList();
            Assert.Equal(("POST", Api + "folders", "{\"name\":\"Kvitton\",\"parent\":{\"id\":\"12\"}}"), (folderCalls[0].Method, folderCalls[0].Url, folderCalls[0].Text));
            Assert.Equal(("PUT", Api + "folders/88?fields=id,type,name,shared_link", "{\"shared_link\":{\"access\":\"open\"}}"), (folderCalls[1].Method, folderCalls[1].Url, folderCalls[1].Text));
            Assert.Contains("https://app.box.com/s/abc", (string)made["shared_link"]!, StringComparison.Ordinal);

            await Run(box, "folder_update", new() { ["folder_id"] = "88", ["description"] = "Kafé", ["parent"] = "0", ["tags"] = "kassa, 2026" });
            Assert.Equal("{\"description\":\"Kafé\",\"parent\":{\"id\":\"0\"},\"tags\":[\"kassa\",\"2026\"]}", Calls(box).Last().Text);
            await Run(box, "folder_delete", new() { ["folder_id"] = "88", ["recursive"] = "true" });
            Assert.Equal(("DELETE", Api + "folders/88?recursive=true"), (Calls(box).Last().Method, Calls(box).Last().Url));

            await Run(box, "share", new() { ["item_type"] = "Folder", ["item_id"] = "88", ["email"] = "ada@kafe.example", ["role"] = "viewerUploader", ["can_view_path"] = "true", ["expires"] = "2026-12-31", ["notify"] = "true" });
            Assert.Equal((Api + "collaborations?notify=true", "{\"item\":{\"type\":\"folder\",\"id\":\"88\"},\"accessible_by\":{\"type\":\"user\",\"login\":\"ada@kafe.example\"},\"role\":\"viewer uploader\",\"can_view_path\":true,\"expires_at\":\"2026-12-31T00:00:00Z\"}"), (Calls(box).Last().Url, Calls(box).Last().Text));
            await Run(box, "share", new() { ["item_type"] = "file", ["item_id"] = "1", ["group_id"] = "4" });
            Assert.Equal("{\"item\":{\"type\":\"file\",\"id\":\"1\"},\"accessible_by\":{\"type\":\"group\",\"id\":\"4\"},\"role\":\"editor\"}", Calls(box).Last().Text);

            var link = Assert.Single(await Run(box, "link", new() { ["item_type"] = "folder", ["item_id"] = "88", ["access"] = "company", ["can_download"] = "false" })).Fields;
            Assert.Equal(("PUT", Api + "folders/88?fields=shared_link", "{\"shared_link\":{\"access\":\"company\",\"permissions\":{\"can_download\":false}}}"), (Calls(box).Last().Method, Calls(box).Last().Url, Calls(box).Last().Text));
            Assert.Equal(("https://app.box.com/s/abc", "88", "folder"), ((string)link["url"]!, (string)link["id"]!, (string)link["type"]!));
        }

        [Fact]
        public async Task A_test_names_whom_the_app_acts_as_and_boxs_refusals_are_said()
        {
            var box = new BoxApi { Answer = _ => Json("{\"type\":\"user\",\"id\":\"9\",\"name\":\"Kafé Automation\",\"login\":\"AutomationUser_1@boxdevedition.com\"}") };
            var test = await new BoxConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(box), "", Settings, Credentials));
            Assert.Equal((true, "Box: the app acts as Kafé Automation (AutomationUser_1@boxdevedition.com)."), (test.Success, test.Message));
            Assert.Equal(Api + "users/me?fields=name,login", Calls(box).Single().Url);

            var refused = new BoxApi { Answer = _ => Json("{\"type\":\"error\",\"status\":404,\"code\":\"not_found\",\"message\":\"Not Found\"}", HttpStatusCode.NotFound) };
            Assert.Equal("Box answered 404 not_found: Not Found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refused, "get", new() { ["file_id"] = "1" }))).Message);
            var address = await new BoxConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(new BoxApi()), "https://api.box.com/2.0", Settings, Credentials));
            Assert.Equal("'https://api.box.com/2.0' is not Box's API - its address alone, https://api.box.com.", address.Message);
        }
    }
}
