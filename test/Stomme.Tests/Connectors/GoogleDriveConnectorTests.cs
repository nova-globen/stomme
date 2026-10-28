#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.GoogleDrive;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google Drive (n8n's Google Drive node: files, folders and shared drives): the OAuth client's refresh token at
    /// Google's token endpoint, then Drive's API as a bearer; a search as n8n builds Drive's query - a name escaped, a folder's
    /// children, files or folders, types, the trash - page by page no further than asked; a download into the run's folder, a
    /// Google file exported to n8n's default for its kind or to what is asked, a path ending with / taking the file's name; an
    /// upload multipart, sent whole again after a throttle, converted when asked; an update, a copy, a move by its parents, a
    /// delete to the trash or for good, a share with no one e-mailed unless asked and the owner's only transferred; folders and
    /// shared drives; what could go wrong refused before a token is asked for; Google's reason and message on a failure.
    /// </summary>
    public sealed class GoogleDriveConnectorTests : IDisposable
    {
        private const string Api = "https://www.googleapis.com/drive/v3";
        private const string Upload = "https://www.googleapis.com/upload/drive/v3";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz";
        private const string Folder = "0FoLdErIdEfGhIjKlMnOp";
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-gdrive-" + Guid.NewGuid().ToString("N"));

        public GoogleDriveConnectorTests() => Directory.CreateDirectory(_root);

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

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return sent.Url == TokenUrl ? Json(HttpStatusCode.OK, "{\"access_token\":\"gd-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}") : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private IEnumerable<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != TokenUrl);

        private Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, string endpoint = "") =>
            Contexts.DrainAsync(new GoogleDriveConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), endpoint, operation, parameters, null, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true, string endpoint = "")
        {
            var google = new Google();
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters, files, endpoint: endpoint));
            Assert.Empty(google.Requests); // refused before a token was asked for
            return refusal.Message;
        }

        private static string Files(int first, int count, string? next) =>
            "{" + (next is null ? "" : "\"nextPageToken\":\"" + next + "\",") + "\"files\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"file" + i + "abcdefghij\",\"name\":\"Kvitto " + i + ".pdf\",\"mimeType\":\"application/pdf\",\"parents\":[\"" + Folder + "\"],\"size\":\"1042\",\"trashed\":false}")) + "]}";

        [Fact]
        public async Task A_search_is_drives_own_query_as_n8n_builds_it_page_by_page_no_further_than_asked()
        {
            var google = new Google { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("pageToken=", StringComparison.Ordinal) ? Files(3, 1, null) : Files(1, 2, "page/2+")) };
            var found = await Run(google, "search", new() { ["name"] = "Kafé's \\kassa", ["folder"] = $"https://drive.google.com/drive/folders/{Folder}?usp=sharing", ["what"] = "files", ["types"] = "application/pdf, application/vnd.google-apps.spreadsheet" });
            Assert.Equal(3, found.Count);
            Assert.Equal(("POST", TokenUrl, "grant_type=refresh_token&refresh_token=kafe-refresh&client_id=kafe-client&client_secret=kafe-secret"), (google.Requests[0].Method.Method, google.Requests[0].Url, google.Requests[0].Text));
            var query = Uri.EscapeDataString($"name contains 'Kafé\\'s \\\\kassa' and '{Folder}' in parents and mimeType != 'application/vnd.google-apps.folder' and (mimeType = 'application/pdf' or mimeType = 'application/vnd.google-apps.spreadsheet') and trashed = false");
            var fields = Uri.EscapeDataString("nextPageToken,files(id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description)");
            Assert.Equal(new[]
            {
                $"{Api}/files?corpora=user&includeItemsFromAllDrives=true&supportsAllDrives=true&q={query}&pageSize=1000&fields={fields}",
                $"{Api}/files?corpora=user&includeItemsFromAllDrives=true&supportsAllDrives=true&q={query}&pageSize=998&fields={fields}&pageToken=page%2F2%2B",
            }, Calls(google).Select(sent => sent.Url));
            Assert.All(Calls(google), sent => Assert.Equal("Bearer gd-token", sent.Authorization));
            var first = found[0].Fields;
            Assert.Equal(("file1abcdefghij", "Kvitto 1.pdf", "1042", false, "[\"" + Folder + "\"]"), ((string?)first["id"], (string?)first["name"], (string?)first["size"], (bool?)first["trashed"], (string?)first["parents"]));

            var bounded = new Google { Answer = _ => Json(HttpStatusCode.OK, Files(1, 2, "more")) };
            Assert.Single(await Run(bounded, "search", new() { ["query"] = "starred = true", ["drive"] = "0ADrIvEiD", ["trashed"] = "true", ["max"] = "1" })); // no further than asked
            Assert.Equal($"{Api}/files?corpora=drive&driveId=0ADrIvEiD&includeItemsFromAllDrives=true&supportsAllDrives=true&q={Uri.EscapeDataString("(starred = true)")}&pageSize=1&fields={fields}", Assert.Single(Calls(bounded)).Url);

            var folders = new Google { Answer = _ => Json(HttpStatusCode.OK, Files(1, 1, null)) };
            await Run(folders, "search", new() { ["what"] = "folders", ["types"] = "application/pdf" });
            Assert.Contains(Uri.EscapeDataString("mimeType = 'application/vnd.google-apps.folder' and trashed = false"), Calls(folders).Single().Url, StringComparison.Ordinal); // a folder search names no types

            var same = new Google { Answer = _ => Json(HttpStatusCode.OK, Files(1, 1, "again")) };
            Assert.Equal("Google Drive named the same next page again.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(same, "search", new()))).Message);
        }

        [Fact]
        public async Task A_download_is_written_into_the_runs_folder_a_google_file_exported_as_n8n_does()
        {
            var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x1B };
            var google = new Google
            {
                Answer = sent => sent.Url.Contains("alt=media", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) }
                    : Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"Kvitto 1042.pdf\",\"mimeType\":\"application/pdf\",\"size\":\"7\"}"),
            };
            var record = Assert.Single(await Run(google, "download", new() { ["file_id"] = $"https://drive.google.com/file/d/{Id}/view?usp=sharing", ["file"] = "downloads/" })).Fields;
            Assert.Equal(new[] { $"{Api}/files/{Id}?supportsAllDrives=true&fields={Uri.EscapeDataString("id,name,mimeType,size")}", $"{Api}/files/{Id}?alt=media&supportsAllDrives=true" }, Calls(google).Select(sent => sent.Url));
            Assert.Equal(pdf, File.ReadAllBytes(Path.Combine(_root, "downloads", "Kvitto 1042.pdf"))); // ending with /: its own name, the very bytes
            Assert.Equal(("downloads/Kvitto 1042.pdf", 7L, "application/pdf", (string?)null), ((string?)record["file"], (long?)record["size"], (string?)record["mimeType"], (string?)record["exported_from"]));

            // A Google Doc: exported to n8n's default for a document, HTML, its extension added.
            var doc = new Google
            {
                Answer = sent => sent.Url.Contains("/export?", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>Kassan</p>") }
                    : Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"Kassa: rutiner\",\"mimeType\":\"application/vnd.google-apps.document\"}"),
            };
            var exported = Assert.Single(await Run(doc, "download", new() { ["file_id"] = $"https://docs.google.com/document/d/{Id}/edit", ["file"] = "docs/" })).Fields;
            Assert.Equal($"{Api}/files/{Id}/export?mimeType=text%2Fhtml", Calls(doc).Last().Url);
            Assert.Equal(("docs/Kassa_ rutiner.html", "text/html", "application/vnd.google-apps.document"), ((string?)exported["file"], (string?)exported["mimeType"], (string?)exported["exported_from"])); // its unsafe characters replaced
            Assert.Equal("<p>Kassan</p>", File.ReadAllText(Path.Combine(_root, "docs", "Kassa_ rutiner.html")));

            var sheet = new Google
            {
                Answer = sent => sent.Url.Contains("/export?", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) }
                    : Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"Omsättning\",\"mimeType\":\"application/vnd.google-apps.spreadsheet\"}"),
            };
            await Run(sheet, "download", new() { ["file_id"] = $"https://drive.google.com/open?id={Id}", ["file"] = "sheets/omsattning.xlsx", ["sheets_as"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
            Assert.Equal($"{Api}/files/{Id}/export?mimeType=application%2Fvnd.openxmlformats-officedocument.spreadsheetml.sheet", Calls(sheet).Last().Url);
            Assert.True(File.Exists(Path.Combine(_root, "sheets", "omsattning.xlsx"))); // a path given is the path

            Google Of(string type, string size = "7") => new() { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"X\",\"mimeType\":\"" + type + "\",\"size\":\"" + size + "\"}") };
            Assert.Equal("'X' is a folder; a folder is not downloaded.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Of("application/vnd.google-apps.folder"), "download", new() { ["file_id"] = Id, ["file"] = "x" }))).Message);
            Assert.Equal("'X' is a Google file (application/vnd.google-apps.form) with no export here - only Docs, Sheets, Slides and Drawings are exported.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Of("application/vnd.google-apps.form"), "download", new() { ["file_id"] = Id, ["file"] = "x" }))).Message);
            var big = Of("application/pdf", "100000000");
            Assert.Equal("'X' is 100000000 bytes, over the pipeline folder's 1048576; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["file_id"] = Id, ["file"] = "x" }))).Message);
            Assert.DoesNotContain(Calls(big), sent => sent.Url.Contains("alt=media", StringComparison.Ordinal)); // refused by its size before its bytes were asked for

            // An export names no size beforehand: refused by the length Google answers with, nothing written.
            var longExport = new Google
            {
                Answer = sent =>
                {
                    if (!sent.Url.Contains("/export?", StringComparison.Ordinal))
                    {
                        return Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"Stor\",\"mimeType\":\"application/vnd.google-apps.document\"}");
                    }

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
                    response.Content.Headers.ContentLength = 100_000_000;
                    return response;
                },
            };
            Assert.Equal("'Stor' is 100000000 bytes, over the pipeline folder's 1048576; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(longExport, "download", new() { ["file_id"] = Id, ["file"] = "stor.html" }))).Message);
            Assert.False(File.Exists(Path.Combine(_root, "stor.html")));

            Assert.Equal("'image/gif' is not one of image/jpeg, application/pdf, image/png, image/svg+xml (drawings_as).", await Refused("download", new() { ["file_id"] = Id, ["file"] = "x", ["drawings_as"] = "image/gif" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["file_id"] = Id, ["file"] = "x" }, files: false));
            Assert.Equal("Give the file - where in the site's pipeline folder it is written.", await Refused("download", new() { ["file_id"] = Id }));
        }

        [Fact]
        public async Task An_upload_is_multipart_sent_whole_again_after_a_throttle_converted_when_asked()
        {
            var csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
            Directory.CreateDirectory(Path.Combine(_root, "reports"));
            File.WriteAllText(Path.Combine(_root, "reports", "juni.csv"), csv);
            var attempts = 0;
            var google = new Google
            {
                Answer = _ =>
                {
                    if (attempts++ == 0)
                    {
                        var slow = Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"message\":\"Backend Error\"}}");
                        slow.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return slow;
                    }

                    return Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\",\"name\":\"juni.csv\",\"mimeType\":\"text/csv\",\"parents\":[\"" + Folder + "\"]}");
                },
            };

            var record = Assert.Single(await Run(google, "upload", new() { ["file"] = "reports/juni.csv", ["folder"] = Folder, ["description"] = "Juni", ["properties"] = "{\"order\": 1042}", ["app_properties"] = "{\"kassa\": \"1\"}" })).Fields;
            Assert.Equal(2, Calls(google).Count()); // throttled once, sent again
            Assert.All(Calls(google), sent =>
            {
                Assert.Equal((HttpMethod.Post, $"{Upload}/files?uploadType=multipart&supportsAllDrives=true&fields={Uri.EscapeDataString("id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description")}"), (sent.Method, sent.Url));
                Assert.StartsWith("multipart/related; boundary=stomme", sent.ContentType, StringComparison.Ordinal); // unquoted, as Drive reads it
                Assert.Contains("{\"name\":\"juni.csv\",\"description\":\"Juni\",\"properties\":{\"order\":\"1042\"},\"appProperties\":{\"kassa\":\"1\"},\"parents\":[\"" + Folder + "\"]}", sent.Text, StringComparison.Ordinal);
                Assert.Contains("Content-Type: text/csv\r\n\r\n" + csv, sent.Text, StringComparison.Ordinal); // the whole file each time, typed by its extension
            });
            Assert.Equal(("juni.csv", "text/csv"), ((string?)record["name"], (string?)record["mimeType"]));

            // Text into a shared drive's root, made a Google Doc - the type Drive converts it to named in the metadata.
            var text = new Google { Answer = _ => Json(HttpStatusCode.OK, "{\"id\":\"" + Id + "\"}") };
            await Run(text, "upload", new() { ["name"] = "Kassa", ["text"] = " Kassan är nere \n", ["drive"] = "0ADrIvEiD", ["convert"] = "true" }, files: false);
            var sent = Calls(text).Single();
            Assert.Contains("{\"name\":\"Kassa\",\"mimeType\":\"application/vnd.google-apps.document\",\"parents\":[\"0ADrIvEiD\"]}", sent.Text, StringComparison.Ordinal);
            Assert.Contains("Content-Type: text/plain\r\n\r\n Kassan är nere \n", sent.Text, StringComparison.Ordinal); // the text as given

            Assert.Equal("Drive makes a Google file of a document, a spreadsheet or a presentation - 'application/pdf' is none of them (convert).", await Refused("upload", new() { ["file"] = "reports/juni.csv", ["content_type"] = "application/pdf", ["convert"] = "true" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["name"] = "x" }));
            Assert.Equal("Give the file or text, not both.", await Refused("upload", new() { ["file"] = "a.txt", ["text"] = "hej" }));
            Assert.Equal("Give the name - text has none of its own.", await Refused("upload", new() { ["text"] = "hej" }));
            Assert.Equal("'[1]' is not a JSON object of text (properties).", await Refused("upload", new() { ["text"] = "hej", ["name"] = "a", ["properties"] = "[1]" }));
            Assert.Equal("Property 'tags' is not text (app_properties).", await Refused("upload", new() { ["text"] = "hej", ["name"] = "a", ["app_properties"] = "{\"tags\": [\"x\"]}" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("upload", new() { ["file"] = "reports/juni.csv" }, files: false));
        }

        [Fact]
        public async Task Update_copy_move_delete_and_share_take_drives_shapes()
        {
            var google = new Google { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("fields=name", StringComparison.Ordinal) ? "{\"name\":\"Kvitto 1042.pdf\"}" : sent.Url.Contains("fields=parents", StringComparison.Ordinal) ? "{\"parents\":[\"oldparentAAAA\",\"oldparentBBBB\"]}" : "{\"id\":\"" + Id + "\"}") };
            var fields = "&fields=" + Uri.EscapeDataString("id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description");

            await Run(google, "update", new() { ["file_id"] = Id, ["name"] = "Kvitto 1042 (betald).pdf", ["description"] = "Betald" });
            Assert.Equal((HttpMethod.Patch, $"{Api}/files/{Id}?supportsAllDrives=true{fields}", "{\"name\":\"Kvitto 1042 (betald).pdf\",\"description\":\"Betald\"}"), (Calls(google).Last().Method, Calls(google).Last().Url, Calls(google).Last().Text)); // metadata alone, on the API
            await Run(google, "update", new() { ["file_id"] = Id, ["text"] = "ny text" });
            Assert.Equal((HttpMethod.Patch, $"{Upload}/files/{Id}?uploadType=multipart&supportsAllDrives=true{fields}"), (Calls(google).Last().Method, Calls(google).Last().Url)); // content too: the upload twin
            Assert.Contains("ny text", Calls(google).Last().Text, StringComparison.Ordinal);

            google.Requests.Clear();
            await Run(google, "copy", new() { ["file_id"] = Id });
            Assert.Equal(new[] { $"{Api}/files/{Id}?supportsAllDrives=true&fields=name", $"{Api}/files/{Id}/copy?supportsAllDrives=true{fields}" }, Calls(google).Select(sent => sent.Url));
            Assert.Equal("{\"name\":\"Copy of Kvitto 1042.pdf\"}", Calls(google).Last().Text); // n8n's name for a copy
            await Run(google, "copy", new() { ["file_id"] = Id, ["name"] = "Arkiv", ["folder"] = Folder, ["description"] = "Arkiverad" });
            Assert.Equal("{\"name\":\"Arkiv\",\"parents\":[\"" + Folder + "\"],\"description\":\"Arkiverad\"}", Calls(google).Last().Text);

            google.Requests.Clear();
            await Run(google, "move", new() { ["file_id"] = Id, ["folder"] = Folder });
            Assert.Equal(new[] { $"{Api}/files/{Id}?supportsAllDrives=true&fields=parents", $"{Api}/files/{Id}?supportsAllDrives=true&addParents={Folder}&removeParents=oldparentAAAA%2ColdparentBBBB{fields}" }, Calls(google).Select(sent => sent.Url));
            await Run(google, "move", new() { ["file_id"] = Id, ["drive"] = "0ADrIvEiD" });
            Assert.Contains("addParents=0ADrIvEiD&", Calls(google).Last().Url, StringComparison.Ordinal); // a shared drive's root
            await Run(google, "move", new() { ["file_id"] = Id, ["folder"] = "root", ["drive"] = "0ADrIvEiD" });
            Assert.Contains("addParents=0ADrIvEiD&", Calls(google).Last().Url, StringComparison.Ordinal); // the root of the drive named, not My Drive's
            await Run(google, "move", new() { ["file_id"] = Id, ["folder"] = "root" });
            Assert.Contains("addParents=root&", Calls(google).Last().Url, StringComparison.Ordinal);

            google.Requests.Clear();
            Assert.Equal((Id, true, false), Assert.Single(await Run(google, "delete", new() { ["file_id"] = Id })).Fields is var trashed ? ((string?)trashed["id"], (bool?)trashed["deleted"], (bool?)trashed["permanently"]) : default);
            Assert.Equal((HttpMethod.Patch, $"{Api}/files/{Id}?supportsAllDrives=true&fields=id", "{\"trashed\":true}"), (Calls(google).Last().Method, Calls(google).Last().Url, Calls(google).Last().Text)); // to the trash by default
            await Run(google, "delete", new() { ["file_id"] = Id, ["permanently"] = "true" });
            Assert.Equal((HttpMethod.Delete, $"{Api}/files/{Id}?supportsAllDrives=true"), (Calls(google).Last().Method, Calls(google).Last().Url));

            google.Requests.Clear();
            await Run(google, "share", new() { ["file_id"] = Id, ["role"] = "writer", ["type"] = "user", ["email"] = "ada@kafe.example.com" });
            Assert.Equal(($"{Api}/files/{Id}/permissions?supportsAllDrives=true&sendNotificationEmail=false", "{\"role\":\"writer\",\"type\":\"user\",\"emailAddress\":\"ada@kafe.example.com\"}"), (Calls(google).Last().Url, Calls(google).Last().Text)); // no one e-mailed unless asked
            await Run(google, "share", new() { ["file_id"] = Id, ["role"] = "Reader", ["type"] = "anyone", ["discoverable"] = "true" });
            Assert.Equal("{\"role\":\"reader\",\"type\":\"anyone\",\"allowFileDiscovery\":true}", Calls(google).Last().Text);
            await Run(google, "share", new() { ["file_id"] = Id, ["role"] = "owner", ["type"] = "user", ["email"] = "bo@kafe.example.com", ["transfer_ownership"] = "true", ["notify"] = "true", ["message"] = "Din nu & för alltid" });
            Assert.Equal($"{Api}/files/{Id}/permissions?supportsAllDrives=true&sendNotificationEmail=true&transferOwnership=true&emailMessage=Din%20nu%20%26%20f%C3%B6r%20alltid", Calls(google).Last().Url);
            Assert.Equal("Making someone the owner transfers the file - set transfer_ownership to true.", await Refused("share", new() { ["file_id"] = Id, ["role"] = "owner", ["type"] = "user", ["email"] = "bo@kafe.example.com" }));
            Assert.Equal("A message goes with an e-mail - set notify to true.", await Refused("share", new() { ["file_id"] = Id, ["role"] = "reader", ["type"] = "user", ["email"] = "bo@kafe.example.com", ["message"] = "Hej" }));
            Assert.Equal("'bo' is not an e-mail address.", await Refused("share", new() { ["file_id"] = Id, ["role"] = "reader", ["type"] = "group", ["email"] = "bo" }));
            Assert.Equal("'kafe' is not a domain - kafe.example.com.", await Refused("share", new() { ["file_id"] = Id, ["role"] = "reader", ["type"] = "domain", ["domain"] = "kafe" }));
            Assert.StartsWith("'boss' is not a role - reader, ", await Refused("share", new() { ["file_id"] = Id, ["role"] = "boss", ["type"] = "user" }), StringComparison.Ordinal);
            Assert.Equal("'everyone' is not user, group, domain or anyone (type).", await Refused("share", new() { ["file_id"] = Id, ["role"] = "reader", ["type"] = "everyone" }));
        }

        [Fact]
        public async Task Folders_and_shared_drives()
        {
            var google = new Google { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("/drives?pageSize", StringComparison.Ordinal) ? (sent.Url.Contains("pageToken", StringComparison.Ordinal) ? "{\"drives\":[{\"id\":\"0AD3\",\"name\":\"Arkiv\"}]}" : "{\"nextPageToken\":\"d2\",\"drives\":[{\"id\":\"0AD1\",\"name\":\"Kafé\"},{\"id\":\"0AD2\",\"name\":\"Kassa\"}]}") : "{\"id\":\"0AD9\",\"name\":\"Ny\"}") };
            var fields = "&fields=" + Uri.EscapeDataString("id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description");
            await Run(google, "create_folder", new() { ["name"] = "Kvitton 2026", ["folder"] = Folder });
            Assert.Equal(($"{Api}/files?supportsAllDrives=true{fields}", "{\"name\":\"Kvitton 2026\",\"mimeType\":\"application/vnd.google-apps.folder\",\"parents\":[\"" + Folder + "\"]}"), (Calls(google).Last().Url, Calls(google).Last().Text));
            await Run(google, "create_folder", new() { ["name"] = "Topp", ["folder"] = "root" });
            Assert.Equal("{\"name\":\"Topp\",\"mimeType\":\"application/vnd.google-apps.folder\",\"parents\":[\"root\"]}", Calls(google).Last().Text);

            google.Requests.Clear();
            var drives = await Run(google, "drives", new() { ["query"] = "name contains 'Ka'", ["domain_admin"] = "true" });
            Assert.Equal(new[] { "Kafé", "Kassa", "Arkiv" }, drives.Select(record => (string?)record.Fields["name"]));
            Assert.Equal(new[] { $"{Api}/drives?pageSize=100&q=name%20contains%20%27Ka%27&useDomainAdminAccess=true", $"{Api}/drives?pageSize=98&q=name%20contains%20%27Ka%27&useDomainAdminAccess=true&pageToken=d2" }, Calls(google).Select(sent => sent.Url));

            google.Requests.Clear();
            var attempts = 0;
            var made = new Google
            {
                Answer = _ =>
                {
                    if (attempts++ == 0)
                    {
                        var slow = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":429,\"message\":\"Rate limit\"}}");
                        slow.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return slow;
                    }

                    return Json(HttpStatusCode.OK, "{\"id\":\"0AD9\",\"name\":\"Kafé 2026\"}");
                },
            };
            await Run(made, "create_drive", new() { ["name"] = "Kafé 2026" });
            var tried = Calls(made).ToList();
            Assert.Equal(2, tried.Count);
            Assert.StartsWith($"{Api}/drives?requestId=", tried[0].Url, StringComparison.Ordinal);
            Assert.Equal(tried[0].Url, tried[1].Url); // the same request id again: one drive, never two
            Assert.Equal("{\"name\":\"Kafé 2026\"}", tried[1].Text);

            await Run(google, "get_drive", new() { ["drive"] = "0AD9" });
            await Run(google, "update_drive", new() { ["drive"] = "0AD9", ["name"] = "Kafé arkiv" });
            Assert.Equal(true, (bool?)Assert.Single(await Run(google, "delete_drive", new() { ["drive"] = "0AD9" })).Fields["deleted"]);
            Assert.Equal(new[] { (HttpMethod.Get, $"{Api}/drives/0AD9", ""), (HttpMethod.Patch, $"{Api}/drives/0AD9", "{\"name\":\"Kafé arkiv\"}"), (HttpMethod.Delete, $"{Api}/drives/0AD9", "") }, Calls(google).Select(sent => (sent.Method, sent.Url, sent.Text)));
            Assert.Equal("Give the shared drive - its id.", await Refused("delete_drive", new()));
            Assert.Equal("Give the name.", await Refused("create_folder", new()));
            Assert.Equal("Give the name.", await Refused("update_drive", new() { ["drive"] = "0AD9" }));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_failures_say_googles_reason()
        {
            Assert.StartsWith("Google Drive has no operation 'sync' - search, get, download,", await Refused("sync", new()), StringComparison.Ordinal);
            Assert.Equal("Give the file id - its id or its address.", await Refused("get", new()));
            Assert.Equal("'../1Ab' is not a file - its id, or its address (drive.google.com/file/d/{id}).", await Refused("get", new() { ["file_id"] = "../1Ab" }));
            Assert.Equal("'https://evil.example.com/file/d/1AbCdEfGhIjKl' is not a file - its id, or its address (drive.google.com/file/d/{id}).", await Refused("get", new() { ["file_id"] = "https://evil.example.com/file/d/1AbCdEfGhIjKl" }));
            Assert.Equal("'x y' is not a file - its id, or its address (drive.google.com/file/d/{id}).", await Refused("search", new() { ["folder"] = "x y" }));
            Assert.Equal("'0A/D' is not a shared drive's id.", await Refused("search", new() { ["drive"] = "0A/D" }));
            Assert.Equal("'some' is not all, files or folders (what).", await Refused("search", new() { ["what"] = "some" }));
            Assert.Equal("'pdf' is not a media type - application/pdf (types).", await Refused("search", new() { ["types"] = "pdf" }));
            Assert.Equal("The query has a line break - one line only.", await Refused("search", new() { ["query"] = "a\nb" }));
            Assert.Equal("'ja' is not true or false (trashed).", await Refused("search", new() { ["trashed"] = "ja" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("search", new() { ["max"] = "0" }));
            Assert.Equal("The name has a line break - one line only.", await Refused("create_folder", new() { ["name"] = "a\nb" }));
            Assert.Equal("'https://www.googleapis.com/drive/v2' is not Drive's API - its address ends with /drive/v3, https://www.googleapis.com/drive/v3.", await Refused("get", new() { ["file_id"] = Id }, endpoint: "https://www.googleapis.com/drive/v2"));

            var missing = new Google { Answer = _ => Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"File not found: 1AbC.\",\"errors\":[{\"domain\":\"global\",\"reason\":\"notFound\",\"message\":\"File not found: 1AbC.\"}]}}") };
            Assert.Equal("HTTP 404 notFound: File not found: 1AbC.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "get", new() { ["file_id"] = Id }))).Message);
            var signIn = new Google { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Sign in</body></html>", Encoding.UTF8, "text/html") } };
            Assert.Equal("Google Drive answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(signIn, "get", new() { ["file_id"] = Id }))).Message);
            var denied = new Google { Answer = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":403,\"message\":\"The user does not have sufficient permissions for this file.\",\"status\":\"PERMISSION_DENIED\"}}") } };
            Assert.Equal("HTTP 403 PERMISSION_DENIED: The user does not have sufficient permissions for this file.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(denied, "download", new() { ["file_id"] = Id, ["file"] = "x" }))).Message);
            Assert.False(File.Exists(Path.Combine(_root, "x"))); // an error is never saved

            var about = new Google { Answer = _ => Json(HttpStatusCode.OK, "{\"user\":{\"displayName\":\"Ada\",\"emailAddress\":\"ada@kafe.example.com\"}}") };
            var test = await new GoogleDriveConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(about), "", null, Credentials));
            Assert.Equal((true, "Google Drive: signed in as ada@kafe.example.com."), (test.Success, test.Message));
            Assert.Equal($"{Api}/about?fields=user%28displayName%2CemailAddress%29", Calls(about).Single().Url);
        }
    }
}
