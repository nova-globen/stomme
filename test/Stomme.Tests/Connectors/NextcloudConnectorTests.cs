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
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.Nextcloud;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Nextcloud connector's live tests' server (BL-229): NEXTCLOUD_TEST_HOST / PORT / PASSWORD where set, otherwise this
    /// machine's throwaway Nextcloud that <c>scripts/nextcloud-test-server.ps1</c> makes on 127.0.0.1:18088, its administrator
    /// <c>kafe</c> and its password read from the file that script writes under the user's temp folder. No password is a
    /// FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class NextcloudTestEndpoint
    {
        public const string User = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("NEXTCLOUD_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("NEXTCLOUD_TEST_PORT"), out var port) ? port : 18088;

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("NEXTCLOUD_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-nextcloud-test", "password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No Nextcloud test server: run 'pwsh scripts/nextcloud-test-server.ps1' (a throwaway Nextcloud on 127.0.0.1:18088, its password in {file}) or set NEXTCLOUD_TEST_HOST, NEXTCLOUD_TEST_PORT and NEXTCLOUD_TEST_PASSWORD.");
            }
        }
    }

    /// <summary>
    /// BL-229 - Nextcloud (n8n's Nextcloud node) without a server: the server's address - n8n's WebDAV address read as it -
    /// and no user, query or fragment in it; the signed-in user's id, from OCS, naming their WebDAV root; a folder listed and a
    /// file got through a PROPFIND, each entry named as n8n's folder List names it, the folder itself and the properties
    /// Nextcloud has not left out; a copy and a move naming their destination on that root, replacing or not; an upload of text
    /// or a file of the run's folder, refused when something is there and it is not to be replaced; a download into the run's
    /// folder, over its cap refused; a share, the users paged, a user updated field by field and disabled, through OCS; every
    /// parameter held to its shape before anything is sent; Nextcloud's refusals said with its own message.
    /// </summary>
    public sealed class NextcloudConnectorTests : IDisposable
    {
        private const string Server = "https://moln.kafe.example";
        private const string Dav = Server + "/remote.php/dav/files/ada%20l/";
        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "ada@kafe.example", ["password"] = "app-pw-1" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-nextcloud-" + Guid.NewGuid().ToString("N"));

        public NextcloudConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(string Method, string Url, Dictionary<string, string> Headers, string? ContentType, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);

            public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        }

        private sealed class Cloud : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Created);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, request.Content?.Headers.ContentType?.MediaType, body);
                Requests.Add(sent);
                return sent.Url == Server + "/ocs/v2.php/cloud/user?format=json"
                    ? Ocs("{\"id\":\"ada l\",\"displayname\":\"Ada Lovelace\",\"groups\":[\"admin\",\"kassa\"]}")
                    : Answer(sent);
            }
        }

        private static HttpResponseMessage Ocs(string data, HttpStatusCode status = HttpStatusCode.OK, string meta = "{\"status\":\"ok\",\"statuscode\":200,\"message\":\"OK\"}") =>
            new(status) { Content = new StringContent("{\"ocs\":{\"meta\":" + meta + ",\"data\":" + data + "}}", Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Xml(HttpStatusCode status, string xml) => new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

        private static IEnumerable<Sent> Calls(Cloud cloud) => cloud.Requests.Where(sent => !sent.Url.Contains("/cloud/user?", StringComparison.Ordinal));

        private Task<List<ConnectorRecord>> Run(Cloud cloud, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, string endpoint = Server) =>
            Contexts.DrainAsync(new NextcloudConnector(), Contexts.Extract(new StubEgress(cloud), endpoint, operation, parameters, credentials: Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private const string Listing =
            "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\" xmlns:oc=\"http://owncloud.org/ns\">"
            + "<d:response><d:href>/remote.php/dav/files/ada%20l/Kvitton/</d:href><d:propstat><d:prop><d:getlastmodified>Sat, 17 Oct 2026 10:37:38 GMT</d:getlastmodified><d:resourcetype><d:collection/></d:resourcetype><d:getetag>&quot;6ad3&quot;</d:getetag><oc:fileid>76</oc:fileid><oc:size>43</oc:size></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>"
            + "<d:propstat><d:prop><d:getcontentlength/><d:getcontenttype/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>"
            + "<d:response><d:href>/remote.php/dav/files/ada%20l/Kvitton/kvitto%20%c3%a5.csv</d:href><d:propstat><d:prop><d:getlastmodified>Sat, 17 Oct 2026 10:38:00 GMT</d:getlastmodified><d:getcontentlength>35</d:getcontentlength><d:getcontenttype>text/csv</d:getcontenttype><d:resourcetype/><d:getetag>&quot;cdf7&quot;</d:getetag><oc:fileid>77</oc:fileid><oc:size>35</oc:size></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
            + "<d:response><d:href>/remote.php/dav/files/ada%20l/Kvitton/2026/</d:href><d:propstat><d:prop><d:getcontentlength/><d:getcontenttype/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>" // the missing ones first: read by status, not order
            + "<d:propstat><d:prop><d:getlastmodified>Sat, 17 Oct 2026 10:39:00 GMT</d:getlastmodified><d:resourcetype><d:collection/></d:resourcetype><d:getetag>&quot;9a1&quot;</d:getetag><oc:fileid>78</oc:fileid><oc:size>8</oc:size></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";

        [Fact]
        public async Task The_address_is_the_servers_and_n8ns_webdav_address_is_read_as_it()
        {
            Assert.Equal("https://moln.kafe.example", NextcloudConnector.BaseOf("https://moln.kafe.example/"));
            Assert.Equal("https://moln.kafe.example", NextcloudConnector.BaseOf("https://moln.kafe.example/remote.php/webdav"));
            Assert.Equal("https://kafe.example/nextcloud", NextcloudConnector.BaseOf("https://kafe.example/nextcloud/remote.php/dav/files/ada/"));
            Assert.Equal("https://kafe.example/nextcloud", NextcloudConnector.BaseOf("https://kafe.example/nextcloud/index.php/apps/files"));
            foreach (var endpoint in new[] { "", "ftp://moln.kafe.example", "https://ada:pw@moln.kafe.example", "https://moln.kafe.example/?a=1" })
            {
                var cloud = new Cloud();
                var result = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(new StubEgress(cloud), endpoint, credentials: Credentials));
                Assert.False(result.Success, endpoint);
                Assert.Empty(cloud.Requests);
            }

            var signedIn = new Cloud();
            var test = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(new StubEgress(signedIn), Server + "/remote.php/webdav/", credentials: Credentials));
            Assert.Equal((true, "Nextcloud: Ada Lovelace's account answers (an administrator)."), (test.Success, test.Message));
            var who = signedIn.Requests.Single();
            Assert.Equal(("GET", "true", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada@kafe.example:app-pw-1"))), (who.Method, who.Header("OCS-APIRequest"), who.Header("Authorization")));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true)
            {
                var cloud = new Cloud();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(cloud, operation, parameters, files));
                Assert.Empty(cloud.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Nextcloud has no operation 'rename' - list, get, download,", await Refused("rename", new()), StringComparison.Ordinal);
            Assert.Equal("Give a path - Kvitton/1042.pdf (path).", await Refused("get", new()));
            Assert.Equal("'Kvitton/../hemligt' is not a path - Kvitton/2026/1042.pdf (path).", await Refused("delete", new() { ["path"] = "Kvitton/../hemligt" }));
            Assert.Equal("'Kvitton//1042.pdf' is not a path - Kvitton/2026/1042.pdf (from).", await Refused("copy", new() { ["from"] = "Kvitton//1042.pdf", ["to"] = "Arkiv" }));
            Assert.Equal("Give the file - where in the site's pipeline folder it is written.", await Refused("download", new() { ["path"] = "a.pdf" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["path"] = "a.txt" }));
            Assert.Equal("Give the file or text, not both.", await Refused("upload", new() { ["path"] = "a.txt", ["file"] = "a.txt", ["text"] = "x" }));
            Assert.Equal("Give the path with its name - text has none of its own.", await Refused("upload", new() { ["path"] = "Kvitton/", ["text"] = "x" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["path"] = "a.pdf", ["file"] = "a.pdf" }, files: false));
            Assert.Equal("'maybe' is not true or false (overwrite).", await Refused("upload", new() { ["path"] = "a.txt", ["text"] = "x", ["overwrite"] = "maybe" }));
            Assert.Equal("Give how it is shared - user, group, email, team, link or internal (type).", await Refused("share", new() { ["path"] = "a.pdf" }));
            Assert.Equal("'2' is not user, group, email, team, link or internal (type).", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "2" }));
            Assert.Equal("Give whom it is shared with - a user's or group's id, an address or a team's id (with).", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "user" }));
            Assert.Equal("A link is shared with no one in particular - leave 'with' empty.", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "link", ["with"] = "ada" }));
            Assert.Equal("'admin' is not read, update, create, delete, share or all (permissions).", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "group", ["with"] = "kassa", ["permissions"] = "read, admin" }));
            Assert.Equal("'32' is not a number from 1 to 31 (permissions).", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "group", ["with"] = "kassa", ["permissions"] = "32" }));
            Assert.Equal("'31/12/2026' is not a day - 2026-12-31 (expire).", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "link", ["expire"] = "31/12/2026" }));
            Assert.Equal("An internal link is the file's own address - it takes no permissions, expiry, note or label.", await Refused("share", new() { ["path"] = "a.pdf", ["type"] = "internal", ["note"] = "hej" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("users", new() { ["max"] = "0" }));
            Assert.Equal("'-1' is not a number from 0 (offset).", await Refused("users", new() { ["offset"] = "-1" }));
            Assert.Equal("Give the user's id - their login name (user).", await Refused("user_get", new()));
            Assert.Equal("'ada/../x' is not a user's id - letters, digits and _ . @ - ' (user).", await Refused("user_delete", new() { ["user"] = "ada/../x" }));
            Assert.Equal("Give the user's address - Nextcloud sends the link to set a password there (email).", await Refused("user_create", new() { ["user"] = "ada" }));
            Assert.Equal("'ada' is not an address - ada@example.com (email).", await Refused("user_create", new() { ["user"] = "ada", ["email"] = "ada" }));
            Assert.Equal("Give what changes - display_name, email, phone, address, website, twitter, quota or enabled.", await Refused("user_update", new() { ["user"] = "ada" }));
            Assert.Equal("A password is never a step's parameter - the user sets their own in Nextcloud.", await Refused("user_update", new() { ["user"] = "ada", ["password"] = "x", ["phone"] = "1" }));
            Assert.Equal("'ja' is not true or false (enabled).", await Refused("user_update", new() { ["user"] = "ada", ["enabled"] = "ja" }));
        }

        [Fact]
        public async Task A_folder_is_listed_through_a_propfind_on_the_signed_in_users_root_each_entry_named_as_n8n_names_it()
        {
            var cloud = new Cloud { Answer = _ => Xml((HttpStatusCode)207, Listing) };
            var entries = await Run(cloud, "list", new() { ["path"] = "/Kvitton/" });
            var call = Calls(cloud).Single();
            Assert.Equal(("PROPFIND", Dav + "Kvitton/", "1", "application/xml"), (call.Method, call.Url, call.Header("Depth"), call.ContentType)); // the user's id - from OCS, not the name signed in by - names the root
            Assert.Contains("<oc:fileid/>", call.Text, StringComparison.Ordinal);
            Assert.Equal(2, entries.Count); // the folder itself left out
            Assert.Equal(new Dictionary<string, object?>
            {
                ["path"] = "Kvitton/kvitto å.csv", ["name"] = "kvitto å.csv", ["type"] = "file", ["lastModified"] = "2026-10-17T10:38:00Z",
                ["contentLength"] = 35L, ["contentType"] = "text/csv", ["eTag"] = "cdf7", ["fileId"] = 77L, ["size"] = 35L,
            }, entries[0].Fields);
            Assert.Equal(("Kvitton/2026/", "2026", "folder", 8L), ((string)entries[1].Fields["path"]!, (string)entries[1].Fields["name"]!, (string)entries[1].Fields["type"]!, (long)entries[1].Fields["size"]!));
            Assert.False(entries[1].Fields.ContainsKey("contentLength")); // what Nextcloud has not, left out

            var root = new Cloud { Answer = _ => Xml((HttpStatusCode)207, Listing) };
            await Run(root, "list", new());
            Assert.Equal(Dav, Calls(root).Single().Url); // empty: the user's root

            var file = new Cloud { Answer = _ => Xml((HttpStatusCode)207, Listing.Replace("<d:resourcetype><d:collection/></d:resourcetype><d:getetag>&quot;6ad3", "<d:resourcetype/><d:getetag>&quot;6ad3", StringComparison.Ordinal)) };
            Assert.Equal("'Kvitton' is a file, not a folder - get it, or list the folder it is in.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(file, "list", new() { ["path"] = "Kvitton" }))).Message);

            var one = new Cloud { Answer = _ => Xml((HttpStatusCode)207, Listing) };
            var got = Assert.Single(await Run(one, "get", new() { ["path"] = "Kvitton" }));
            Assert.Equal((Dav + "Kvitton", "0"), (Calls(one).Single().Url, Calls(one).Single().Header("Depth")));
            Assert.Equal(("Kvitton/", "folder", 76L), ((string)got.Fields["path"]!, (string)got.Fields["type"]!, (long)got.Fields["fileId"]!));
        }

        [Fact]
        public async Task A_copy_and_a_move_name_their_destination_on_the_users_root_and_say_whether_to_replace()
        {
            var cloud = new Cloud();
            cloud.Answer = sent => new HttpResponseMessage(sent.Method == "MOVE" ? HttpStatusCode.NoContent : HttpStatusCode.Created) { Headers = { { "OC-FileId", "00000081oc35s8a8ysid" } } };
            var copied = Assert.Single(await Run(cloud, "copy", new() { ["from"] = "Kvitton/kvitto å.csv", ["to"] = "Arkiv/2026/kvitto å.csv" })).Fields;
            var copy = Calls(cloud).Last();
            Assert.Equal(("COPY", Dav + "Kvitton/kvitto%20%C3%A5.csv", Dav + "Arkiv/2026/kvitto%20%C3%A5.csv", "T"), (copy.Method, copy.Url, copy.Header("Destination"), copy.Header("Overwrite")));
            Assert.Equal(("Arkiv/2026/kvitto å.csv", "kvitto å.csv", false, 81L), ((string)copied["path"]!, (string)copied["name"]!, (bool)copied["replaced"]!, (long)copied["fileId"]!));

            var moved = Assert.Single(await Run(cloud, "move", new() { ["from"] = "Kvitton/a.csv", ["to"] = "Arkiv/a.csv", ["overwrite"] = "false" })).Fields;
            Assert.Equal(("MOVE", "F"), (Calls(cloud).Last().Method, Calls(cloud).Last().Header("Overwrite")));
            Assert.True((bool)moved["replaced"]!); // 204: something was there

            await Run(cloud, "create_folder", new() { ["path"] = "Arkiv/2027" });
            await Run(cloud, "delete", new() { ["path"] = "Arkiv/2025" });
            Assert.Equal(new[] { ("MKCOL", Dav + "Arkiv/2027"), ("DELETE", Dav + "Arkiv/2025") }, Calls(cloud).TakeLast(2).Select(sent => (sent.Method, sent.Url)));
        }

        [Fact]
        public async Task An_upload_and_a_download_go_through_the_runs_folder()
        {
            File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
            var cloud = new Cloud();
            cloud.Answer = sent => sent.Method == "GET"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf-bytes")) { Headers = { ContentType = new("application/pdf") } }, Headers = { ETag = new("\"e1\"") } }
                : Stored();
            static HttpResponseMessage Stored()
            {
                var created = new HttpResponseMessage(HttpStatusCode.Created) { Headers = { ETag = new("\"e2\"") } };
                created.Headers.Add("OC-FileId", "00000090ocabc");
                return created;
            }

            var stored = Assert.Single(await Run(cloud, "upload", new() { ["path"] = "Rapporter/", ["file"] = "juni.csv" })).Fields;
            var put = Calls(cloud).Last();
            Assert.Equal(("PUT", Dav + "Rapporter/juni.csv", "datum;summa\n2026-06-01;35\n", null), (put.Method, put.Url, put.Text, put.Header("If-None-Match"))); // its own name in the folder; replacing, as n8n's node
            Assert.Equal(("Rapporter/juni.csv", 26L, false, "e2", 90L), ((string)stored["path"]!, (long)stored["size"]!, (bool)stored["replaced"]!, (string)stored["eTag"]!, (long)stored["fileId"]!));

            await Run(cloud, "upload", new() { ["path"] = "Anteckningar/hej på dig.txt", ["query"] = "Kassan stängd\nÅter 08:00", ["overwrite"] = "false" }, files: false);
            put = Calls(cloud).Last();
            Assert.Equal((Dav + "Anteckningar/hej%20p%C3%A5%20dig.txt", "Kassan stängd\nÅter 08:00", "*"), (put.Url, put.Text, put.Header("If-None-Match"))); // text over several lines from the Query box; refused if there

            var fetched = Assert.Single(await Run(cloud, "download", new() { ["path"] = "Kvitton/1042.pdf", ["file"] = "downloads/" })).Fields;
            Assert.Equal(("downloads/1042.pdf", 9L, "application/pdf", "e1"), ((string)fetched["file"]!, (long)fetched["size"]!, (string)fetched["contentType"]!, (string)fetched["eTag"]!));
            Assert.Equal("pdf-bytes", File.ReadAllText(Path.Combine(_root, "downloads", "1042.pdf")));

            var big = new Cloud { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) } };
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["path"] = "stor.bin", ["file"] = "stor.bin" }, cap: 16));
            Assert.Equal("'stor.bin' is 64 bytes, over the pipeline folder's 16; it is not fetched.", refused.Message);
            Assert.False(File.Exists(Path.Combine(_root, "stor.bin")));
        }

        [Fact]
        public async Task A_share_and_the_users_go_through_ocs()
        {
            var cloud = new Cloud();
            cloud.Answer = sent => sent.Url switch
            {
                var url when url.Contains("/files_sharing/", StringComparison.Ordinal) => Ocs("{\"id\":\"7\",\"share_type\":3,\"url\":\"https://moln.kafe.example/s/abc\",\"permissions\":17}"),
                var url when url.Contains("/cloud/users?", StringComparison.Ordinal) && url.Contains("limit=500", StringComparison.Ordinal) => Ocs("{\"users\":[" + string.Join(",", Enumerable.Range(0, NextcloudConnector.UsersPage).Select(n => $"\"u{n:000}\"")) + "]}"),
                var url when url.Contains("/cloud/users?", StringComparison.Ordinal) && url.Contains("limit=100", StringComparison.Ordinal) => Ocs("{\"users\":[\"cia\"]}"),
                var url when url.Contains("/cloud/users?", StringComparison.Ordinal) => Ocs("{\"users\":[\"ada\",\"bo\",\"cia\"]}"),
                var url when url.Contains("/cloud/users/details?", StringComparison.Ordinal) => Ocs("{\"users\":{\"ada\":{\"id\":\"ada\",\"displayname\":\"Ada\"},\"bo\":{\"displayname\":\"Bo\"}}}"),
                var url when url.StartsWith(Server + "/remote.php/", StringComparison.Ordinal) => Xml((HttpStatusCode)207, Listing),
                _ => Ocs(sent.Method == "POST" ? "{\"id\":\"cia\"}" : "[]"),
            };
            var shared = Assert.Single(await Run(cloud, "share", new() { ["path"] = "Kvitton", ["type"] = "link", ["permissions"] = "read, share", ["expire"] = "2026-12-31", ["label"] = "Revisorn" })).Fields;
            var post = Calls(cloud).Last();
            Assert.Equal(("POST", Server + "/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json", "true"), (post.Method, post.Url, post.Header("OCS-APIRequest")));
            Assert.Equal("path=%2FKvitton&shareType=3&permissions=17&expireDate=2026-12-31&label=Revisorn", post.Text);
            Assert.Equal("https://moln.kafe.example/s/abc", shared["url"]);

            var internalLink = Assert.Single(await Run(cloud, "share", new() { ["path"] = "Kvitton", ["type"] = "200" })).Fields;
            Assert.Equal("https://moln.kafe.example/f/76", internalLink["link"]); // the file's own address, by its id

            var users = await Run(cloud, "users", new() { ["search"] = "a", ["max"] = "600" });
            Assert.Equal(NextcloudConnector.UsersPage + 1, users.Count); // a full page, then one more: a page with fewer than asked is the last
            Assert.Equal(("u000", "cia"), ((string)users[0].Fields["id"]!, (string)users[^1].Fields["id"]!));
            Assert.Equal(new[] { Server + "/ocs/v2.php/cloud/users?format=json&search=a&limit=500&offset=0", Server + "/ocs/v2.php/cloud/users?format=json&search=a&limit=100&offset=500" }, Calls(cloud).TakeLast(2).Select(sent => sent.Url));
            Assert.Equal(new[] { "ada", "bo" }, (await Run(cloud, "users", new() { ["max"] = "2" })).Select(user => (string)user.Fields["id"]!)); // no more than asked, whatever the server sends
            var detailed = await Run(cloud, "users", new() { ["details"] = "true", ["offset"] = "5" });
            Assert.Equal(new[] { ("ada", "Ada"), ("bo", "Bo") }, detailed.Select(user => ((string)user.Fields["id"]!, (string)user.Fields["displayname"]!))); // the id added where Nextcloud keyed by it alone
            Assert.EndsWith("/cloud/users/details?format=json&limit=50&offset=5", Calls(cloud).Last().Url, StringComparison.Ordinal);

            Assert.Equal("cia", Assert.Single(await Run(cloud, "user_create", new() { ["user"] = "cia", ["email"] = "cia@kafe.example", ["display_name"] = "Cia K", ["groups"] = "kassa, revision" })).Fields["id"]);
            Assert.Equal("userid=cia&email=cia%40kafe.example&displayName=Cia+K&groups%5B%5D=kassa&groups%5B%5D=revision", Calls(cloud).Last().Text);

            var before = Calls(cloud).Count();
            var updated = Assert.Single(await Run(cloud, "user_update", new() { ["user"] = "cia k", ["email"] = "cia@kassa.se", ["display_name"] = "Cia Kassa", ["enabled"] = "false" })).Fields;
            Assert.Equal(new[] { ("PUT", Server + "/ocs/v2.php/cloud/users/cia%20k?format=json", "key=displayname&value=Cia+Kassa"), ("PUT", Server + "/ocs/v2.php/cloud/users/cia%20k?format=json", "key=email&value=cia%40kassa.se"), ("PUT", Server + "/ocs/v2.php/cloud/users/cia%20k/disable?format=json", "") },
                Calls(cloud).Skip(before).Select(sent => (sent.Method, sent.Url, sent.Text)));
            Assert.Equal(new Dictionary<string, object?> { ["id"] = "cia k", ["display_name"] = "Cia Kassa", ["email"] = "cia@kassa.se", ["enabled"] = false }, updated);

            await Run(cloud, "user_delete", new() { ["user"] = "cia" });
            Assert.Equal(("DELETE", Server + "/ocs/v2.php/cloud/users/cia?format=json"), (Calls(cloud).Last().Method, Calls(cloud).Last().Url));
        }

        [Fact]
        public async Task Nextclouds_refusals_are_said_with_its_own_message()
        {
            var signIn = new Cloud();
            var refused = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.Unauthorized, "{\"ocs\":{\"meta\":{\"status\":\"failure\",\"statuscode\":997}}}")), Server, credentials: Credentials));
            Assert.Equal((false, "Nextcloud refused the sign-in (401) - check the user and its app password."), (refused.Success, refused.Message));
            var notNextcloud = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.OK, "<html>sign in</html>")), Server, credentials: Credentials));
            Assert.Equal("Nextcloud answered 200 with what is not its OCS JSON - check the connection's address.", notNextcloud.Message);

            var missing = new Cloud { Answer = _ => Xml(HttpStatusCode.NotFound, "<?xml version=\"1.0\"?><d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\"><s:exception>Sabre\\DAV\\Exception\\NotFound</s:exception><s:message>File with name /Kvitton/nope.txt could not be located</s:message></d:error>") };
            Assert.Equal("Nextcloud answered 404: File with name /Kvitton/nope.txt could not be located", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "delete", new() { ["path"] = "Kvitton/nope.txt" }))).Message);

            var ocs = new Cloud { Answer = _ => Ocs("[]", HttpStatusCode.BadRequest, "{\"status\":\"failure\",\"statuscode\":102,\"message\":\"User already exists\"}") };
            Assert.Equal("Nextcloud answered 102: User already exists", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ocs, "user_create", new() { ["user"] = "ada", ["email"] = "ada@kafe.example" }))).Message);
            var quiet = new Cloud { Answer = _ => Ocs("[]", HttpStatusCode.OK, "{\"status\":\"failure\",\"statuscode\":403,\"message\":\"Logged in account must be an admin\"}") };
            Assert.Equal("Nextcloud answered 403: Logged in account must be an admin", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(quiet, "users", new()))).Message); // a refusal OCS says on an HTTP 200

        }
    }

    /// <summary>
    /// BL-229 - the Nextcloud connector against a real Nextcloud 34 (<c>scripts/nextcloud-test-server.ps1</c>'s throwaway server
    /// on loopback) through the HOST's egress: loopback refused until allowlisted; a test that names the account, a wrong
    /// password refused; a folder made, text and a file uploaded - an accent and a space in the name - replaced, or refused when
    /// not to be; the folder listed and a file got; a copy and a move, a download into the run's folder; a link shared, an
    /// internal link by the file's id, a share with a user made for it; a user made, got, found, updated, disabled and deleted.
    /// Each test cleans up what it made. Fails, never skips, when the server is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class NextcloudConnectorLiveTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-nextcloud-live-" + Guid.NewGuid().ToString("N"));

        private static string Endpoint => $"http://{NextcloudTestEndpoint.Host}:{NextcloudTestEndpoint.Port}";

        private static Dictionary<string, string> Credentials => new() { ["username"] = NextcloudTestEndpoint.User, ["password"] = NextcloudTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { NextcloudTestEndpoint.Host });

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new NextcloudConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, credentials: Credentials, timeoutSeconds: 30) with
            {
                Files = PipelineConnectorFiles.Under(_root, 1 << 20),
            });

        [Fact]
        public async Task A_test_names_the_account_and_a_wrong_password_is_refused()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new NextcloudConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied)); // loopback refused until allowlisted: the runner says DeniedByPolicy
            var right = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint + "/remote.php/webdav", credentials: Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.Equal((true, $"Nextcloud: {NextcloudTestEndpoint.User}'s account answers (an administrator)."), (right.Success, right.Message));
            var wrong = await new NextcloudConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = NextcloudTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 30));
            Assert.Equal((false, "Nextcloud refused the sign-in (401) - check the user and its app password."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task Files_and_folders_round_trip()
        {
            var folder = "Kassa " + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Assert.Equal("folder", Assert.Single(await Run("create_folder", new() { ["path"] = folder })).Fields["type"]);
                Assert.StartsWith("Nextcloud answered 405: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("create_folder", new() { ["path"] = folder }))).Message, StringComparison.Ordinal);
                Assert.StartsWith("Nextcloud answered 409: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("create_folder", new() { ["path"] = folder + "/saknas/under" }))).Message, StringComparison.Ordinal);

                var first = Assert.Single(await Run("upload", new() { ["path"] = folder + "/kvitto å.txt", ["text"] = "Kafé Ada 35 kr" })).Fields;
                Assert.Equal((false, 15L), ((bool)first["replaced"]!, (long)first["size"]!));
                var again = Assert.Single(await Run("upload", new() { ["path"] = folder + "/kvitto å.txt", ["query"] = "Kafé Ada\n70 kr" })).Fields;
                Assert.True((bool)again["replaced"]!); // replaced, as n8n's node does
                Assert.StartsWith("Nextcloud answered 412: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("upload", new() { ["path"] = folder + "/kvitto å.txt", ["text"] = "x", ["overwrite"] = "false" }))).Message, StringComparison.Ordinal);

                Directory.CreateDirectory(_root);
                File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
                await Run("create_folder", new() { ["path"] = folder + "/Rapporter" });
                var report = Assert.Single(await Run("upload", new() { ["path"] = folder + "/Rapporter/", ["file"] = "juni.csv" })).Fields;
                Assert.Equal((folder + "/Rapporter/juni.csv", 26L), ((string)report["path"]!, (long)report["size"]!));

                var listed = await Run("list", new() { ["path"] = "/" + folder + "/" });
                Assert.Equal(new[] { ("Rapporter", "folder"), ("kvitto å.txt", "file") }, listed.Select(entry => ((string)entry.Fields["name"]!, (string)entry.Fields["type"]!)).OrderBy(entry => entry.Item1, StringComparer.Ordinal));
                var receipt = listed.Single(entry => (string)entry.Fields["type"]! == "file").Fields;
                Assert.Equal((folder + "/kvitto å.txt", 15L, "text/plain"), ((string)receipt["path"]!, (long)receipt["contentLength"]!, (string)receipt["contentType"]!));
                Assert.True((long)receipt["fileId"]! > 0);
                Assert.Matches("^20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", (string)receipt["lastModified"]!);
                Assert.Equal(receipt["fileId"], Assert.Single(await Run("get", new() { ["path"] = folder + "/kvitto å.txt" })).Fields["fileId"]);
                Assert.StartsWith("'" + folder + "/kvitto å.txt' is a file, not a folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("list", new() { ["path"] = folder + "/kvitto å.txt" }))).Message, StringComparison.Ordinal);

                Assert.False((bool)Assert.Single(await Run("copy", new() { ["from"] = folder + "/kvitto å.txt", ["to"] = folder + "/Rapporter/kopia å.txt" })).Fields["replaced"]!);
                Assert.StartsWith("Nextcloud answered 412: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("copy", new() { ["from"] = folder + "/kvitto å.txt", ["to"] = folder + "/Rapporter/kopia å.txt", ["overwrite"] = "false" }))).Message, StringComparison.Ordinal);
                Assert.True((bool)Assert.Single(await Run("move", new() { ["from"] = folder + "/Rapporter/juni.csv", ["to"] = folder + "/Rapporter/kopia å.txt" })).Fields["replaced"]!);

                var fetched = Assert.Single(await Run("download", new() { ["path"] = folder + "/Rapporter/kopia å.txt", ["file"] = "downloads/" })).Fields;
                Assert.Equal(("downloads/kopia å.txt", 26L), ((string)fetched["file"]!, (long)fetched["size"]!));
                Assert.Equal("datum;summa\n2026-06-01;35\n", File.ReadAllText(Path.Combine(_root, "downloads", "kopia å.txt"))); // the moved file replaced the copy
                Assert.Equal(new[] { "kopia å.txt" }, (await Run("list", new() { ["path"] = folder + "/Rapporter" })).Select(entry => (string)entry.Fields["name"]!));

                Assert.True((bool)Assert.Single(await Run("delete", new() { ["path"] = folder + "/Rapporter" })).Fields["deleted"]!);
                Assert.StartsWith("Nextcloud answered 404: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("get", new() { ["path"] = folder + "/Rapporter" }))).Message, StringComparison.Ordinal);
            }
            finally
            {
                await Run("delete", new() { ["path"] = folder });
            }
        }

        [Fact]
        public async Task Shares_and_users_go_through_ocs()
        {
            var folder = "Delat " + Guid.NewGuid().ToString("N")[..8];
            var user = "gast-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await Run("create_folder", new() { ["path"] = folder });
                var link = Assert.Single(await Run("share", new() { ["path"] = folder, ["type"] = "link", ["permissions"] = "read", ["expire"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"), ["label"] = "Revisorn" })).Fields;
                Assert.Equal((3L, "Revisorn", "/" + folder), ((long)link["share_type"]!, (string)link["label"]!, (string)link["path"]!));
                Assert.StartsWith(Endpoint + "/s/", (string)link["url"]!, StringComparison.Ordinal);
                var id = Assert.Single(await Run("get", new() { ["path"] = folder })).Fields["fileId"];
                Assert.Equal($"{Endpoint}/f/{id}", Assert.Single(await Run("share", new() { ["path"] = folder, ["type"] = "internal" })).Fields["link"]);

                var made = Assert.Single(await Run("user_create", new() { ["user"] = user, ["email"] = user + "@kafe.example", ["display_name"] = "Gäst Kafé", ["groups"] = "testers" })).Fields;
                Assert.Equal(user, made["id"]);
                var got = Assert.Single(await Run("user_get", new() { ["user"] = user })).Fields;
                Assert.Equal(("Gäst Kafé", user + "@kafe.example", true), ((string)got["displayname"]!, (string)got["email"]!, (bool)got["enabled"]!));
                Assert.Contains("\"testers\"", (string)got["groups"]!, StringComparison.Ordinal); // a list as its JSON
                Assert.Equal(new[] { user }, (await Run("users", new() { ["search"] = user })).Select(found => (string)found.Fields["id"]!));
                Assert.Equal("Gäst Kafé", Assert.Single(await Run("users", new() { ["search"] = user, ["details"] = "true" })).Fields["displayname"]);

                var shared = Assert.Single(await Run("share", new() { ["path"] = folder, ["type"] = "user", ["with"] = user, ["permissions"] = "read, create" })).Fields;
                Assert.Equal((0L, user, 5L), ((long)shared["share_type"]!, (string)shared["share_with"]!, (long)shared["permissions"]!));

                Assert.Equal(new Dictionary<string, object?> { ["id"] = user, ["display_name"] = "Gäst Kassa", ["phone"] = "+46 8 465 004 02", ["enabled"] = false },
                    Assert.Single(await Run("user_update", new() { ["user"] = user, ["display_name"] = "Gäst Kassa", ["phone"] = "+46 8 465 004 02", ["enabled"] = "false" })).Fields);
                got = Assert.Single(await Run("user_get", new() { ["user"] = user })).Fields;
                Assert.Equal(("Gäst Kassa", false), ((string)got["displayname"]!, (bool)got["enabled"]!));

                Assert.True((bool)Assert.Single(await Run("user_delete", new() { ["user"] = user })).Fields["deleted"]!);
                Assert.StartsWith("Nextcloud answered 404: ", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("user_get", new() { ["user"] = user }))).Message, StringComparison.Ordinal);
            }
            finally
            {
                try
                {
                    await Run("user_delete", new() { ["user"] = user });
                }
                catch (InvalidOperationException)
                {
                    // gone already: the test deleted it
                }

                await Run("delete", new() { ["path"] = folder });
            }
        }
    }
}
