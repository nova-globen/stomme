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
using Stomme.Modules.Connectors.GoogleCloudStorage;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Google Cloud Storage connector's live tests' server: GCS_TEST_HOST / PORT where set, otherwise this machine's
    /// throwaway fake-gcs-server that <c>scripts/gcs-test-server.ps1</c> makes on 127.0.0.1:14443. It checks no token, so the
    /// tests give the connector a loopback stand-in for Google's token endpoint. No server is a FAILURE that says how to get
    /// one, never a skip.
    /// </summary>
    internal static class GcsTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("GCS_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("GCS_TEST_PORT"), out var port) ? port : 14443;

        public static string Endpoint => $"http://{Host}:{Port}";
    }

    /// <summary>A loopback stand-in for Google's token endpoint: a token for one refresh grant alone, the form it was asked with kept.</summary>
    internal sealed class GoogleTokenStandIn : IDisposable
    {
        public const string Token = "gcs-test-token";
        private readonly HttpListener _listener;
        private readonly string _token;
        private int _issued;

        /// <summary>A stand-in issuing <paramref name="token"/> - <see cref="Token"/> unless a test's server wants its own (Google's Firestore emulator lets in only "owner").</summary>
        public GoogleTokenStandIn(string clientId, string secret, string refresh, string token = Token)
        {
            _token = token;
            Form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
            (_listener, Port) = LoopbackHttp.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}/token";

        public string Form { get; }

        public int Issued => Volatile.Read(ref _issued);

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var form = await reader.ReadToEndAsync();
                var right = context.Request.HttpMethod == "POST" && context.Request.RawUrl == "/token" && form == Form;
                if (right)
                {
                    Interlocked.Increment(ref _issued);
                }

                var answer = right ? "{\"access_token\":\"" + _token + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}" : "{\"error\":\"invalid_grant\"}";
                context.Response.StatusCode = right ? 200 : 400;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(answer);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }

    /// <summary>
    /// Google Cloud Storage (n8n's Google Cloud Storage node) without a server: the app's client and a refresh token for
    /// a token, asked for only once everything is held to its shape; a project's buckets and a bucket's objects listed page by
    /// page through Google's nextPageToken, a folder's subfolders as folders; a bucket made with its location, class, labels and
    /// versioning; an object uploaded in one multipart call - its metadata first, then its bytes - from the run's folder or the
    /// Query box, ifGenerationMatch=0 when not to replace; downloaded with alt=media, over the folder's cap refused; its metadata
    /// patched; copied by a rewrite carried over as many calls as Google asks; deleted; an object's name escaped whole, its
    /// slashes too; Google's refusals said with their reason and message.
    /// </summary>
    public sealed class GoogleCloudStorageConnectorTests : IDisposable
    {
        private const string Api = "https://storage.googleapis.com";
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-gcs-" + Guid.NewGuid().ToString("N"));

        public GoogleCloudStorageConnectorTests() => Directory.CreateDirectory(_root);

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

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json("{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return sent.Url == GoogleCloudStorageConnector.DefaultTokenUrl ? Json("{\"access_token\":\"gcs-token\",\"expires_in\":3600}") : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static IEnumerable<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleCloudStorageConnector.DefaultTokenUrl);

        private Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new GoogleCloudStorageConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, settings ?? new(), Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private const string Receipt = "{\"kind\":\"storage#object\",\"id\":\"kafe-kvitton/2026/1042 å.csv/1\",\"selfLink\":\"https://x\",\"name\":\"2026/1042 å.csv\",\"bucket\":\"kafe-kvitton\",\"size\":\"35\",\"contentType\":\"text/csv\",\"generation\":\"1792238116920141\",\"updated\":\"2026-10-17T11:55:16.920Z\",\"metadata\":{\"kund\":\"ada\"},\"acl\":[{\"role\":\"OWNER\"}]}";

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters, files));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Assert.StartsWith("Google Cloud Storage has no operation 'rename' - buckets, bucket_get,", await Refused("rename", new()), StringComparison.Ordinal);
            Assert.Equal("Give the project's id - kafe-kassa (project).", await Refused("buckets", new()));
            Assert.Equal("'Kafe Kassa' is not a project's id - kafe-kassa (project).", await Refused("buckets", new() { ["project"] = "Kafe Kassa" }));
            Assert.Equal("Give the bucket's name - kafe-kvitton (bucket).", await Refused("objects", new()));
            foreach (var name in new[] { "Kvitton", "kv", "-kvitton", "kvitton-", "goog-kvitton", new string('k', 64) })
            {
                Assert.Equal($"'{name}' is not a bucket's name - 3 to 63 lower-case letters, digits, - and _ (bucket).", await Refused("bucket_get", new() { ["bucket"] = name }));
            }

            Assert.Equal("'..' is not an object's name - 2026/1042.pdf, 1 to 1,024 characters (object).", await Refused("get", new() { ["bucket"] = "kafe-kvitton", ["object"] = ".." }));
            Assert.Equal("'Glacier' is not STANDARD, NEARLINE, COLDLINE or ARCHIVE (storage_class).", await Refused("bucket_create", new() { ["project"] = "kafe-kassa", ["bucket"] = "kafe-kvitton", ["storage_class"] = "Glacier" }));
            Assert.Equal("The labels is not a JSON object - {\"kund\": \"ada\"}.", await Refused("bucket_create", new() { ["project"] = "kafe-kassa", ["bucket"] = "kafe-kvitton", ["labels"] = "[1]" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.txt" }));
            Assert.Equal("Give the object's name with its own - text has none here.", await Refused("upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/", ["text"] = "x" }));
            Assert.Equal("'maybe' is not true or false (overwrite).", await Refused("upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.txt", ["text"] = "x", ["overwrite"] = "maybe" }));
            Assert.Equal("Give what changes - metadata, content_type, cache_control or content_disposition.", await Refused("update", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.txt" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.pdf", ["file"] = "a.pdf" }, files: false));
            Assert.Equal("Give the object's name - 2026/1042.pdf (to_object).", await Refused("copy", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.pdf" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("objects", new() { ["bucket"] = "kafe-kvitton", ["max"] = "0" }));
        }

        [Fact]
        public async Task Buckets_and_objects_are_listed_page_by_page_and_a_folder_s_subfolders_named()
        {
            var google = new Google
            {
                Answer = sent => Json(sent.Url.Contains("pageToken=p2", StringComparison.Ordinal)
                    ? "{\"items\":[" + Receipt.Replace("1042 å", "1043") + "],\"prefixes\":[\"2026/arkiv/\"]}"
                    : "{\"items\":[" + Receipt + "],\"prefixes\":[\"2026/q1/\"],\"nextPageToken\":\"p2\"}"),
            };
            var listed = await Run(google, "objects", new() { ["bucket"] = "kafe-kvitton", ["prefix"] = "2026/", ["delimiter"] = "/", ["versions"] = "true", ["max"] = "4" });
            Assert.Equal(new[] { Api + "/storage/v1/b/kafe-kvitton/o?prefix=2026%2F&delimiter=%2F&versions=true&maxResults=4", Api + "/storage/v1/b/kafe-kvitton/o?prefix=2026%2F&delimiter=%2F&versions=true&maxResults=2&pageToken=p2" }, Calls(google).Select(sent => sent.Url));
            Assert.Equal(new[] { ("2026/1042 å.csv", "file"), ("2026/q1/", "folder"), ("2026/1043.csv", "file"), ("2026/arkiv/", "folder") }, listed.Select(item => ((string)item.Fields["name"]!, (string)item.Fields["type"]!)));
            Assert.Equal(new Dictionary<string, object?>
            {
                ["name"] = "2026/1042 å.csv", ["bucket"] = "kafe-kvitton", ["size"] = 35L, ["contentType"] = "text/csv", ["generation"] = "1792238116920141", ["updated"] = "2026-10-17T11:55:16.920Z", ["metadata"] = "{\"kund\":\"ada\"}", ["type"] = "file",
            }, listed[0].Fields); // the transport's own fields - kind, id, selfLink, acl - left out
            Assert.All(Calls(google), sent => Assert.Equal("Bearer gcs-token", sent.Authorization));
            Assert.Equal("grant_type=refresh_token&refresh_token=kafe-refresh&client_id=kafe-client&client_secret=kafe-secret", google.Requests.First().Text);

            var buckets = new Google { Answer = _ => Json("{\"items\":[{\"name\":\"kafe-a\"},{\"name\":\"kafe-b\"},{\"name\":\"kafe-c\"}],\"nextPageToken\":\"n\"}") };
            Assert.Equal(new[] { "kafe-a", "kafe-b" }, (await Run(buckets, "buckets", new() { ["prefix"] = "kafe-", ["max"] = "2" }, settings: new() { ["project"] = "kafe-kassa" })).Select(item => (string)item.Fields["name"]!)); // the connection's project; no more than asked
            Assert.Equal(Api + "/storage/v1/b?project=kafe-kassa&prefix=kafe-&maxResults=2", Calls(buckets).Single().Url);
        }

        [Fact]
        public async Task A_bucket_is_made_and_an_object_uploaded_downloaded_updated_copied_and_deleted()
        {
            File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
            var rewrites = 0;
            var google = new Google();
            google.Answer = sent => sent.Url switch
            {
                var url when url.Contains("alt=media", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf-bytes")) { Headers = { ContentType = new("application/pdf") } } },
                var url when url.Contains("/rewriteTo/", StringComparison.Ordinal) => Json(rewrites++ == 0 ? "{\"done\":false,\"rewriteToken\":\"r1\"}" : "{\"done\":true,\"resource\":" + Receipt.Replace("2026/1042 å.csv", "arkiv/1042.csv") + "}"),
                _ when sent.Method == "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => Json(Receipt),
            };

            await Run(google, "bucket_create", new() { ["project"] = "kafe-kassa", ["bucket"] = "kafe-kvitton", ["location"] = "EU", ["storage_class"] = "nearline", ["labels"] = "{\"avdelning\": \"kassa\"}", ["versioning"] = "true" });
            var made = Calls(google).Last();
            Assert.Equal(("POST", Api + "/storage/v1/b?project=kafe-kassa", "{\"name\":\"kafe-kvitton\",\"location\":\"EU\",\"storageClass\":\"NEARLINE\",\"labels\":{\"avdelning\":\"kassa\"},\"versioning\":{\"enabled\":true}}"), (made.Method, made.Url, made.Text));

            var stored = Assert.Single(await Run(google, "upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/", ["file"] = "juni.csv", ["metadata"] = "{\"kund\": \"ada\"}" })).Fields;
            var upload = Calls(google).Last();
            Assert.Equal(("POST", Api + "/upload/storage/v1/b/kafe-kvitton/o?uploadType=multipart"), (upload.Method, upload.Url));
            Assert.StartsWith("multipart/related", upload.ContentType, StringComparison.Ordinal);
            Assert.Contains("{\"name\":\"2026/juni.csv\",\"contentType\":\"text/csv\",\"metadata\":{\"kund\":\"ada\"}}", upload.Text, StringComparison.Ordinal); // its metadata first
            Assert.Contains("datum;summa\n2026-06-01;35\n", upload.Text, StringComparison.Ordinal); // then its bytes
            Assert.Equal(35L, stored["size"]);

            await Run(google, "upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "anteckningar/hej på dig #2?.txt", ["query"] = "Kassan stängd\nÅter 08:00", ["overwrite"] = "false" }, files: false);
            upload = Calls(google).Last();
            Assert.Equal(Api + "/upload/storage/v1/b/kafe-kvitton/o?uploadType=multipart&ifGenerationMatch=0", upload.Url); // refused if it exists
            Assert.Contains("\"name\":\"anteckningar/hej på dig #2?.txt\",\"contentType\":\"text/plain; charset=utf-8\"", upload.Text, StringComparison.Ordinal);
            Assert.Contains("Kassan stängd\nÅter 08:00", upload.Text, StringComparison.Ordinal);

            await Run(google, "get", new() { ["bucket"] = "kafe-kvitton", ["object"] = "anteckningar/hej på dig #2?.txt" });
            Assert.Equal(Api + "/storage/v1/b/kafe-kvitton/o/anteckningar%2Fhej%20p%C3%A5%20dig%20%232%3F.txt", Calls(google).Last().Url); // the name escaped whole, its slashes too

            var fetched = Assert.Single(await Run(google, "download", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/1042.pdf", ["file"] = "downloads/" })).Fields;
            Assert.Equal((Api + "/storage/v1/b/kafe-kvitton/o/2026%2F1042.pdf?alt=media", "downloads/1042.pdf", 9L), (Calls(google).Last().Url, (string)fetched["file"]!, (long)fetched["size"]!));
            Assert.Equal("pdf-bytes", File.ReadAllText(Path.Combine(_root, "downloads", "1042.pdf")));

            await Run(google, "update", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/1042.pdf", ["metadata"] = "{\"kund\": \"bo\"}", ["cache_control"] = "public, max-age=3600" });
            Assert.Equal(("PATCH", "{\"metadata\":{\"kund\":\"bo\"},\"cacheControl\":\"public, max-age=3600\"}"), (Calls(google).Last().Method, Calls(google).Last().Text));

            var copied = Assert.Single(await Run(google, "copy", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/1042.pdf", ["to_bucket"] = "kafe-arkiv", ["to_object"] = "arkiv/1042.pdf" })).Fields;
            var copies = Calls(google).Where(sent => sent.Url.Contains("/rewriteTo/", StringComparison.Ordinal)).Select(sent => sent.Url).ToList();
            Assert.Equal(new[] { Api + "/storage/v1/b/kafe-kvitton/o/2026%2F1042.pdf/rewriteTo/b/kafe-arkiv/o/arkiv%2F1042.pdf", Api + "/storage/v1/b/kafe-kvitton/o/2026%2F1042.pdf/rewriteTo/b/kafe-arkiv/o/arkiv%2F1042.pdf?rewriteToken=r1" }, copies); // carried on until done
            Assert.Equal(("arkiv/1042.csv", "kafe-kvitton/2026/1042.pdf"), ((string)copied["name"]!, (string)copied["from"]!));

            Assert.True((bool)Assert.Single(await Run(google, "delete", new() { ["bucket"] = "kafe-kvitton", ["object"] = "2026/1042.pdf" })).Fields["deleted"]!);
            Assert.Equal(("DELETE", Api + "/storage/v1/b/kafe-kvitton/o/2026%2F1042.pdf"), (Calls(google).Last().Method, Calls(google).Last().Url));

            var big = new Google { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) } };
            Assert.Equal("'stor.bin' is 64 bytes, over the pipeline folder's 16; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["bucket"] = "kafe-kvitton", ["object"] = "stor.bin", ["file"] = "stor.bin" }, cap: 16))).Message);
        }

        [Fact]
        public async Task Googles_refusals_are_said_with_their_reason_and_message()
        {
            var google = new Google { Answer = _ => Json("{\"error\":{\"code\":412,\"message\":\"Precondition failed\",\"errors\":[{\"domain\":\"global\",\"reason\":\"conditionNotMet\",\"message\":\"Precondition failed\"}]}}", HttpStatusCode.PreconditionFailed) };
            Assert.Equal("Google Cloud Storage answered 412 conditionNotMet: Precondition failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, "upload", new() { ["bucket"] = "kafe-kvitton", ["object"] = "a.txt", ["text"] = "x", ["overwrite"] = "false" }, files: false))).Message);
            var page = new Google { Answer = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>no</html>") } };
            Assert.Equal("Google Cloud Storage answered 403 <html>no</html>", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(page, "bucket_get", new() { ["bucket"] = "kafe-kvitton" }))).Message);
            var test = await new GoogleCloudStorageConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(new Google()), "https://storage.googleapis.com/storage/v1", credentials: Credentials));
            Assert.Equal((false, "'https://storage.googleapis.com/storage/v1' is not Google Cloud Storage's address - its own, https://storage.googleapis.com."), (test.Success, test.Message));
        }
    }

    /// <summary>
    /// The Google Cloud Storage connector against a real fake-gcs-server (<c>scripts/gcs-test-server.ps1</c>'s throwaway
    /// on loopback - an independent implementation of Google's JSON API) and a loopback stand-in for Google's token endpoint,
    /// through the HOST's egress: loopback refused until allowlisted; a test that lists the project's buckets; a bucket made -
    /// again 409 - listed by its prefix and got; text and a file uploaded with their types and metadata - an accent and a space
    /// in a name - replaced, and refused 412 when not to be; a folder listed with its subfolders as folders, page by page; an
    /// object got, downloaded into the run's folder, its metadata patched, copied by a rewrite and deleted; the bucket, emptied,
    /// deleted. Fails, never skips, when the server is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class GoogleCloudStorageConnectorLiveTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-gcs-live-" + Guid.NewGuid().ToString("N"));
        private readonly GoogleTokenStandIn _tokens = new("kafe-client", "kafe-secret", "kafe-refresh");

        private static Dictionary<string, string> Credentials => new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private Dictionary<string, string> Settings => new() { ["token_url"] = _tokens.Url, ["project"] = "kafe-kassa" };

        private static HostConnectorEgress Egress => new(new[] { GcsTestEndpoint.Host });

        public void Dispose()
        {
            _tokens.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters)
        {
            Directory.CreateDirectory(_root);
            return Contexts.DrainAsync(new GoogleCloudStorageConnector(new OAuth2TokenProvider()), Contexts.Extract(Egress, GcsTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30) with
            {
                Files = PipelineConnectorFiles.Under(_root, 1 << 20),
            });
        }

        [Fact]
        public async Task A_test_lists_the_projects_buckets_and_loopback_is_refused_until_allowed()
        {
            var denied = await Assert.ThrowsAnyAsync<Exception>(() => new GoogleCloudStorageConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), GcsTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied)); // loopback refused until allowlisted
            var right = await new GoogleCloudStorageConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(Egress, GcsTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.StartsWith("Google Cloud Storage: the project kafe-kassa answers", right.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Buckets_and_objects_round_trip()
        {
            var bucket = "kafe-" + Guid.NewGuid().ToString("N")[..10];
            try
            {
                Assert.True((bool)Assert.Single(await Run("bucket_create", new() { ["bucket"] = bucket, ["labels"] = "{\"avdelning\": \"kassa\"}" })).Fields["created"]!);
                Assert.StartsWith("Google Cloud Storage answered 409 conflict", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("bucket_create", new() { ["bucket"] = bucket }))).Message, StringComparison.Ordinal);
                Assert.Equal(bucket, Assert.Single(await Run("buckets", new() { ["prefix"] = bucket })).Fields["name"]);
                Assert.Equal(bucket, Assert.Single(await Run("bucket_get", new() { ["bucket"] = bucket })).Fields["name"]);

                var note = Assert.Single(await Run("upload", new() { ["bucket"] = bucket, ["object"] = "anteckningar/stängt å.txt", ["text"] = "Kafé Ada 35 kr", ["metadata"] = "{\"kund\": \"ada\"}" })).Fields;
                Assert.Equal((15L, "ada"), ((long)note["size"]!, System.Text.Json.Nodes.JsonNode.Parse((string)note["metadata"]!)!["kund"]!.GetValue<string>()));
                Assert.Equal(26L, Assert.Single(await Run("upload", new() { ["bucket"] = bucket, ["object"] = "anteckningar/stängt å.txt", ["query"] = "Kassan stängd\nÅter 08:00" })).Fields["size"]); // replaced, as n8n's node does
                Assert.StartsWith("Google Cloud Storage answered 412 conditionNotMet", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("upload", new() { ["bucket"] = bucket, ["object"] = "anteckningar/stängt å.txt", ["text"] = "x", ["overwrite"] = "false" }))).Message, StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
                await Run("upload", new() { ["bucket"] = bucket, ["object"] = "2026/", ["file"] = "juni.csv" });
                await Run("upload", new() { ["bucket"] = bucket, ["object"] = "2026/q1/jan.csv", ["text"] = "datum;summa" });

                var folder = await Run("objects", new() { ["bucket"] = bucket, ["prefix"] = "2026/", ["delimiter"] = "/" });
                Assert.Equal(new[] { ("2026/juni.csv", "file"), ("2026/q1/", "folder") }, folder.Select(item => ((string)item.Fields["name"]!, (string)item.Fields["type"]!)));
                Assert.Equal(("text/csv", 26L), ((string)folder[0].Fields["contentType"]!, (long)folder[0].Fields["size"]!));
                Assert.Equal(new[] { "2026/juni.csv", "2026/q1/jan.csv", "anteckningar/stängt å.txt" }, (await Run("objects", new() { ["bucket"] = bucket })).Select(item => (string)item.Fields["name"]!));
                Assert.Single(await Run("objects", new() { ["bucket"] = bucket, ["max"] = "1" })); // no more than asked

                Assert.Equal(26L, Assert.Single(await Run("get", new() { ["bucket"] = bucket, ["object"] = "anteckningar/stängt å.txt" })).Fields["size"]);
                Assert.Equal("downloads/stängt å.txt", Assert.Single(await Run("download", new() { ["bucket"] = bucket, ["object"] = "anteckningar/stängt å.txt", ["file"] = "downloads/" })).Fields["file"]);
                Assert.Equal("Kassan stängd\nÅter 08:00", File.ReadAllText(Path.Combine(_root, "downloads", "stängt å.txt")));
                await Run("update", new() { ["bucket"] = bucket, ["object"] = "2026/juni.csv", ["metadata"] = "{\"kund\": \"bo\"}" });
                Assert.Contains("\"kund\":\"bo\"", (string)Assert.Single(await Run("get", new() { ["bucket"] = bucket, ["object"] = "2026/juni.csv" })).Fields["metadata"]!, StringComparison.Ordinal);

                Assert.Equal("arkiv/juni.csv", Assert.Single(await Run("copy", new() { ["bucket"] = bucket, ["object"] = "2026/juni.csv", ["to_object"] = "arkiv/juni.csv" })).Fields["name"]);
                Assert.Equal(26L, Assert.Single(await Run("get", new() { ["bucket"] = bucket, ["object"] = "arkiv/juni.csv" })).Fields["size"]);
                Assert.StartsWith("Google Cloud Storage answered 412", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("bucket_delete", new() { ["bucket"] = bucket }))).Message, StringComparison.Ordinal); // not empty
            }
            finally
            {
                foreach (var item in await Run("objects", new() { ["bucket"] = bucket }))
                {
                    await Run("delete", new() { ["bucket"] = bucket, ["object"] = (string)item.Fields["name"]! });
                }

                await Run("bucket_delete", new() { ["bucket"] = bucket });
            }

            Assert.StartsWith("Google Cloud Storage answered 404", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("bucket_get", new() { ["bucket"] = bucket }))).Message, StringComparison.Ordinal);
            Assert.True(_tokens.Issued >= 1);
        }
    }
}
