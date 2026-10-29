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
using Stomme.Modules.Connectors.AzureStorage;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Azure Storage connector's live tests' server: AZURITE_TEST_HOST / PORT / KEY where set, otherwise this
    /// machine's throwaway Azurite that <c>scripts/azurite-test-server.ps1</c> makes on 127.0.0.1:10010, its account <c>kafe</c>
    /// and its key read from the file that script writes under the user's temp folder. No key is a FAILURE that says how to get
    /// one, never a skip.
    /// </summary>
    internal static class AzureStorageTestEndpoint
    {
        public const string Account = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("AZURITE_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("AZURITE_TEST_PORT"), out var port) ? port : 10010;

        /// <summary>Azurite's own address for the account: path-style, the account after the host.</summary>
        public static string Endpoint => $"http://{Host}:{Port}/{Account}";

        public static string Key
        {
            get
            {
                if (Environment.GetEnvironmentVariable("AZURITE_TEST_KEY") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-azurite-test", "key");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No Azure Storage test server: run 'pwsh scripts/azurite-test-server.ps1' (a throwaway Azurite on 127.0.0.1:10010, its account key in {file}) or set AZURITE_TEST_HOST, AZURITE_TEST_PORT and AZURITE_TEST_KEY.");
            }
        }
    }

    /// <summary>
    /// Azure Storage (n8n's Azure Storage node) without a server: the account's address - its name from the host, or
    /// the setting's - no SAS or user in it; every request signed (SharedKey and the account's name, the key never sent) with
    /// x-ms-version and x-ms-date; the containers and a container's blobs listed page by page through Azure's marker, each with
    /// its properties camel-cased and its metadata; a container made public or not with its metadata; a blob's properties got;
    /// an upload of a file of the run's folder, of text from the Query box or from a URL, with its type, tier, metadata and tags,
    /// If-None-Match when not to replace; a download into the run's folder, over its cap refused; a copy and a delete; every
    /// parameter held to its shape before anything is sent; Azure's refusals said with its code and message.
    /// </summary>
    public sealed class AzureStorageConnectorTests : IDisposable
    {
        private const string Service = "https://kafe.blob.core.windows.net";
        private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 64).Select(n => (byte)n).ToArray());
        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = Key };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-azurestorage-" + Guid.NewGuid().ToString("N"));

        public AzureStorageConnectorTests() => Directory.CreateDirectory(_root);

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

        private sealed class Blob : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Created);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, request.Content?.Headers.ContentType?.ToString(), body);
                Requests.Add(sent);
                return Answer(sent);
            }
        }

        private static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

        private Task<List<ConnectorRecord>> Run(Blob blob, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, string endpoint = Service, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new AzureStorageConnector(), Contexts.Extract(new StubEgress(blob), endpoint, operation, parameters, settings, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        [Fact]
        public async Task The_account_is_its_addresss_or_the_settings_and_every_request_is_signed_without_the_key()
        {
            foreach (var (endpoint, settings) in new (string, Dictionary<string, string>?)[] { ("", null), ("https://storage.kafe.example", null), ("https://kafe.blob.core.windows.net/?sv=2025&sig=x", null), ("https://ada:pw@kafe.blob.core.windows.net", null), (Service, new() { ["account"] = "Kafe_1" }) })
            {
                var none = new Blob();
                var result = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(none), endpoint, settings, Credentials));
                Assert.False(result.Success, endpoint);
                Assert.Empty(none.Requests);
            }

            var blob = new Blob { Answer = _ => Xml("<?xml version=\"1.0\"?><EnumerationResults><Containers><Container><Name>kvitton</Name></Container></Containers><NextMarker/></EnumerationResults>") };
            var test = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(blob), Service, credentials: Credentials));
            Assert.Equal((true, "Azure Storage: the account kafe answers."), (test.Success, test.Message));
            var sent = blob.Requests.Single();
            Assert.Equal(("GET", Service + "/?comp=list&maxresults=1", AzureStorageConnector.ApiVersion), (sent.Method, sent.Url, sent.Header("x-ms-version")));
            Assert.StartsWith("SharedKey kafe:", sent.Header("Authorization"), StringComparison.Ordinal);
            Assert.True(DateTime.TryParse(sent.Header("x-ms-date"), out _));
            Assert.DoesNotContain(sent.Headers.Values, value => value.Contains(Key, StringComparison.Ordinal)); // the key signs, never travels

            var emulator = new Blob { Answer = _ => Xml("<EnumerationResults><Containers/><NextMarker/></EnumerationResults>") };
            var named = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(emulator), "http://127.0.0.1:10000/kafe", new Dictionary<string, string> { ["account"] = "kafe" }, Credentials));
            Assert.Equal("Azure Storage: the account kafe answers, no container yet.", named.Message);
            Assert.Equal("http://127.0.0.1:10000/kafe?comp=list&maxresults=1", emulator.Requests.Single().Url); // an emulator's path-style address, as given
            var pathStyle = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(emulator), "http://127.0.0.1:10000/kafe", credentials: Credentials));
            Assert.Equal("Azure Storage: the account kafe answers, no container yet.", pathStyle.Message); // the account its one path segment, no setting needed
            var unnamed = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Blob()), "http://127.0.0.1:10000/kafe/kvitton", credentials: Credentials));
            Assert.Equal("Give the storage account's name - 'http://127.0.0.1:10000/kafe/kvitton' does not name it (account).", unnamed.Message);
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true)
            {
                var blob = new Blob();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(blob, operation, parameters, files));
                Assert.Empty(blob.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Azure Storage has no operation 'rename' - containers, container_get,", await Refused("rename", new()), StringComparison.Ordinal);
            Assert.Equal("Give the container's name - kvitton (container).", await Refused("blobs", new()));
            foreach (var name in new[] { "Kvitton", "kv", "kv--itton", "-kvitton", "kvitton-", "kvitt_on" })
            {
                Assert.Equal($"'{name}' is not a container's name - 3 to 63 lower-case letters, digits and single hyphens (container).", await Refused("container_get", new() { ["container"] = name }));
            }

            Assert.Equal("'/2026/a.pdf' is not a blob's name - 2026/1042.pdf, 1 to 1,024 characters (blob).", await Refused("get", new() { ["container"] = "kvitton", ["blob"] = "/2026/a.pdf" }));
            Assert.Equal("'maybe' is not private, blob or container (access).", await Refused("container_create", new() { ["container"] = "kvitton", ["access"] = "maybe" }));
            Assert.Equal("The metadata is not a JSON object - {\"kund\": \"ada\"}.", await Refused("container_create", new() { ["container"] = "kvitton", ["metadata"] = "kund=ada" }));
            Assert.Equal("'kund-namn' = 'ada' is not metadata Azure takes - a name of letters, digits and _, a value of ASCII (metadata).", await Refused("container_create", new() { ["container"] = "kvitton", ["metadata"] = "{\"kund-namn\": \"ada\"}" }));
            Assert.Equal("'size' is not one of copy, deleted, metadata, snapshots, tags, uncommittedblobs, versions, immutabilitypolicy, legalhold, deletedwithversions, permissions (include).", await Refused("blobs", new() { ["container"] = "kvitton", ["include"] = "metadata, size" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, text or a URL.", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "a.txt" }));
            Assert.Equal("Give one of the file, the text and the URL.", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "a.txt", ["text"] = "x", ["url"] = "https://kafe.example/a.txt" }));
            Assert.Equal("'ftp://kafe.example/a.txt' is not an http(s) address (url).", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "a.txt", ["url"] = "ftp://kafe.example/a.txt" }));
            Assert.Equal("Give the blob's name with its own - text and a URL have none here.", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "2026/", ["text"] = "x" }));
            Assert.Equal("'Lukewarm' is not Hot, Cool, Cold or Archive (tier).", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "a.txt", ["text"] = "x", ["tier"] = "Lukewarm" }));
            Assert.Equal("'maybe' is not true or false (overwrite).", await Refused("upload", new() { ["container"] = "kvitton", ["blob"] = "a.txt", ["text"] = "x", ["overwrite"] = "maybe" }));
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["container"] = "kvitton", ["blob"] = "a.pdf", ["file"] = "a.pdf" }, files: false));
            Assert.Equal("Give the blob's name - 2026/1042.pdf (to_blob).", await Refused("copy", new() { ["container"] = "kvitton", ["blob"] = "a.pdf" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("containers", new() { ["max"] = "0" }));
        }

        [Fact]
        public async Task Containers_and_blobs_are_listed_page_by_page_through_azures_marker()
        {
            var blob = new Blob
            {
                Answer = sent => Xml(sent.Url.Contains("marker=m2", StringComparison.Ordinal)
                    ? "<EnumerationResults><Blobs><Blob><Name>2026/kvitto 3.csv</Name><Properties><Content-Length>12</Content-Length></Properties></Blob></Blobs><NextMarker/></EnumerationResults>"
                    : "<EnumerationResults><Blobs><Blob><Name>2026/kvitto å.csv</Name><Properties><Creation-Time>Sat, 17 Oct 2026 11:04:47 GMT</Creation-Time><Last-Modified>Sat, 17 Oct 2026 11:04:48 GMT</Last-Modified><Etag>0x279322A7D0CF520</Etag><Content-Length>35</Content-Length><Content-Type>text/csv</Content-Type><BlobType>BlockBlob</BlobType><AccessTier>Hot</AccessTier></Properties><Metadata><kund>ada</kund></Metadata></Blob>"
                      + "<Blob><Name>2026/kvitto 2.csv</Name><Properties><Content-Length>20</Content-Length></Properties></Blob></Blobs><NextMarker>m2</NextMarker></EnumerationResults>"),
            };
            var blobs = await Run(blob, "blobs", new() { ["container"] = "kvitton", ["prefix"] = "2026/", ["include"] = "Metadata, tags", ["max"] = "3" });
            Assert.Equal(new[] { Service + "/kvitton?comp=list&include=metadata%2Ctags&maxresults=3&prefix=2026%2F&restype=container", Service + "/kvitton?comp=list&include=metadata%2Ctags&marker=m2&maxresults=1&prefix=2026%2F&restype=container" }, blob.Requests.Select(sent => sent.Url));
            Assert.Equal(new Dictionary<string, object?>
            {
                ["container"] = "kvitton", ["name"] = "2026/kvitto å.csv", ["creationTime"] = "2026-10-17T11:04:47Z", ["lastModified"] = "2026-10-17T11:04:48Z", ["etag"] = "0x279322A7D0CF520",
                ["contentLength"] = 35L, ["contentType"] = "text/csv", ["blobType"] = "BlockBlob", ["accessTier"] = "Hot", ["metadata"] = "{\"kund\":\"ada\"}",
            }, blobs[0].Fields);
            Assert.Equal(new[] { "2026/kvitto å.csv", "2026/kvitto 2.csv", "2026/kvitto 3.csv" }, blobs.Select(item => (string)item.Fields["name"]!));

            var few = new Blob { Answer = _ => Xml("<EnumerationResults><Containers><Container><Name>a1</Name></Container><Container><Name>a2</Name></Container><Container><Name>a3</Name></Container></Containers><NextMarker>m9</NextMarker></EnumerationResults>") };
            Assert.Equal(new[] { "a1", "a2" }, (await Run(few, "containers", new() { ["prefix"] = "a", ["metadata"] = "true", ["max"] = "2" })).Select(item => (string)item.Fields["name"]!)); // never more than asked, whatever a page holds
            Assert.Equal(Service + "/?comp=list&include=metadata&maxresults=2&prefix=a", few.Requests.Single().Url); // no further than asked
        }

        [Fact]
        public async Task A_container_is_made_and_a_blob_uploaded_downloaded_copied_and_deleted()
        {
            File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
            var blob = new Blob();
            blob.Answer = sent => sent.Method switch
            {
                "GET" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pdf-bytes")) { Headers = { ContentType = new("application/pdf") } }, Headers = { ETag = new("\"0x1\"") } },
                "HEAD" => Head(),
                "DELETE" => new HttpResponseMessage(HttpStatusCode.Accepted),
                _ => new HttpResponseMessage(HttpStatusCode.Created) { Headers = { ETag = new("\"0x2\"") } },
            };
            static HttpResponseMessage Head()
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Headers.Add("x-ms-blob-type", "BlockBlob");
                head.Headers.Add("x-ms-meta-kund", "ada");
                head.Headers.Add("x-ms-request-id", "r1");
                return head;
            }

            var made = Assert.Single(await Run(blob, "container_create", new() { ["container"] = "kvitton", ["access"] = "blob", ["metadata"] = "{\"avdelning\": \"kassa\"}" })).Fields;
            var put = blob.Requests.Last();
            Assert.Equal(("PUT", Service + "/kvitton?restype=container", "blob", "kassa"), (put.Method, put.Url, put.Header("x-ms-blob-public-access"), put.Header("x-ms-meta-avdelning")));
            Assert.Equal(("kvitton", true, "0x2"), ((string)made["name"]!, (bool)made["created"]!, (string)made["etag"]!));

            var stored = Assert.Single(await Run(blob, "upload", new() { ["container"] = "kvitton", ["blob"] = "2026/", ["file"] = "juni.csv", ["tier"] = "cool", ["tags"] = "{\"projekt\": \"kassa å\"}", ["metadata"] = "{\"kund\": \"ada\"}" })).Fields;
            put = blob.Requests.Last();
            Assert.Equal((Service + "/kvitton/2026/juni.csv", "BlockBlob", "Cool", "projekt=kassa%20%C3%A5", "ada", "text/csv", "datum;summa\n2026-06-01;35\n", null),
                (put.Url, put.Header("x-ms-blob-type"), put.Header("x-ms-access-tier"), put.Header("x-ms-tags"), put.Header("x-ms-meta-kund"), put.ContentType, put.Text, put.Header("If-None-Match")));
            Assert.Equal(("2026/juni.csv", 26L), ((string)stored["name"]!, (long)stored["size"]!));

            await Run(blob, "upload", new() { ["container"] = "kvitton", ["blob"] = "anteckningar/hej på dig #2?.txt", ["query"] = "Kassan stängd\nÅter 08:00", ["overwrite"] = "false", ["content_type"] = "text/markdown" });
            put = blob.Requests.Last();
            Assert.Equal((Service + "/kvitton/anteckningar/hej%20p%C3%A5%20dig%20%232%3F.txt", "Kassan stängd\nÅter 08:00", "*", "text/markdown"), (put.Url, put.Text, put.Header("If-None-Match"), put.Header("x-ms-blob-content-type"))); // a # and a ? are the name's, never a fragment or a query

            await Run(blob, "upload", new() { ["container"] = "kvitton", ["blob"] = "extern.pdf", ["url"] = "https://kafe.example/1042.pdf" });
            Assert.Equal(("https://kafe.example/1042.pdf", 0), (blob.Requests.Last().Header("x-ms-copy-source"), blob.Requests.Last().Body.Length)); // Azure fetches it, not the host

            var got = Assert.Single(await Run(blob, "get", new() { ["container"] = "kvitton", ["blob"] = "2026/juni.csv" })).Fields;
            Assert.Equal(("HEAD", "BlockBlob", "{\"kund\":\"ada\"}"), (blob.Requests.Last().Method, (string)got["blobType"]!, (string)got["metadata"]!));
            Assert.False(got.ContainsKey("requestId")); // the transport's own headers left out

            var fetched = Assert.Single(await Run(blob, "download", new() { ["container"] = "kvitton", ["blob"] = "2026/1042.pdf", ["file"] = "downloads/" })).Fields;
            Assert.Equal(("downloads/1042.pdf", 9L), ((string)fetched["file"]!, (long)fetched["size"]!));
            Assert.Equal("pdf-bytes", File.ReadAllText(Path.Combine(_root, "downloads", "1042.pdf")));

            await Run(blob, "copy", new() { ["container"] = "kvitton", ["blob"] = "2026/juni.csv", ["to_container"] = "arkiv", ["to_blob"] = "2026/juni.csv" });
            Assert.Equal((Service + "/arkiv/2026/juni.csv", Service + "/kvitton/2026/juni.csv"), (blob.Requests.Last().Url, blob.Requests.Last().Header("x-ms-copy-source")));
            await Run(blob, "delete", new() { ["container"] = "kvitton", ["blob"] = "2026/juni.csv", ["snapshots"] = "true" });
            Assert.Equal(("DELETE", "include"), (blob.Requests.Last().Method, blob.Requests.Last().Header("x-ms-delete-snapshots")));

            var big = new Blob { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) } };
            Assert.Equal("'stor.bin' is 64 bytes, over the pipeline folder's 16; it is not fetched.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(big, "download", new() { ["container"] = "kvitton", ["blob"] = "stor.bin", ["file"] = "stor.bin" }, cap: 16))).Message);
        }

        [Fact]
        public async Task Azures_refusals_are_said_with_its_code_and_message()
        {
            var missing = new Blob
            {
                Answer = _ =>
                {
                    var refused = Xml("<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>ContainerNotFound</Code><Message>The specified container does not exist.\nRequestId:1\nTime:2026</Message></Error>", HttpStatusCode.NotFound);
                    refused.Headers.Add("x-ms-error-code", "ContainerNotFound");
                    return refused;
                },
            };
            Assert.Equal("Azure Storage answered 404 ContainerNotFound: The specified container does not exist.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "blobs", new() { ["container"] = "kvitton" }))).Message);
            var head = new Blob { Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
            Assert.Equal("Azure Storage answered 404.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(head, "get", new() { ["container"] = "kvitton", ["blob"] = "a.pdf" }))).Message);
            var badKey = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Blob()), Service, credentials: new Dictionary<string, string> { ["api-key"] = "not base64!" }));
            Assert.Equal("The account key is not one Azure gives - its base64 text, from the account's Access keys.", badKey.Message);
        }
    }

    /// <summary>
    /// The Azure Storage connector against a real Azurite (<c>scripts/azurite-test-server.ps1</c>'s throwaway emulator on
    /// loopback, Microsoft's own) through the HOST's egress: loopback refused until allowlisted; a test that signs in, a wrong key
    /// refused - Azurite checking every signature; a container made with its metadata, listed by its prefix and got; text, a file
    /// and a blob in a folder uploaded with their types, metadata and tags - an accent and a space in the names - replaced, or
    /// refused with 409 when not to be; the blobs listed by a folder's prefix with their metadata and tags, page by page; a
    /// blob's properties got, downloaded into the run's folder, copied and deleted; the container deleted and gone. Fails, never
    /// skips, when the server is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AzureStorageConnectorLiveTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-azurestorage-live-" + Guid.NewGuid().ToString("N"));

        private static Dictionary<string, string> Credentials => new() { ["api-key"] = AzureStorageTestEndpoint.Key };

        private static Dictionary<string, string> Settings => new() { ["account"] = AzureStorageTestEndpoint.Account };

        private static HostConnectorEgress Egress => new(new[] { AzureStorageTestEndpoint.Host });

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters)
        {
            Directory.CreateDirectory(_root);
            return Contexts.DrainAsync(new AzureStorageConnector(), Contexts.Extract(Egress, AzureStorageTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30) with
            {
                Files = PipelineConnectorFiles.Under(_root, 1 << 20),
            });
        }

        [Fact]
        public async Task A_test_signs_in_and_a_wrong_key_is_refused()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AzureStorageConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), AzureStorageTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied)); // loopback refused until allowlisted
            var right = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(Egress, AzureStorageTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            var wrongKey = Convert.ToBase64String(Enumerable.Range(0, 64).Select(n => (byte)n).ToArray());
            var wrong = await new AzureStorageConnector().TestConnectionAsync(Contexts.Test(Egress, AzureStorageTestEndpoint.Endpoint, Settings, new Dictionary<string, string> { ["api-key"] = wrongKey }, timeoutSeconds: 30));
            Assert.Equal((false, true), (wrong.Success, wrong.Message.StartsWith("Azure Storage answered 403 AuthorizationFailure", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Containers_and_blobs_round_trip()
        {
            var container = "kassa-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Assert.True((bool)Assert.Single(await Run("container_create", new() { ["container"] = container, ["metadata"] = "{\"avdelning\": \"kassa\"}" })).Fields["created"]!);
                Assert.StartsWith("Azure Storage answered 409 ContainerAlreadyExists", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("container_create", new() { ["container"] = container }))).Message, StringComparison.Ordinal);
                var listed = Assert.Single(await Run("containers", new() { ["prefix"] = container, ["metadata"] = "true" })).Fields;
                Assert.Equal((container, "{\"avdelning\":\"kassa\"}"), ((string)listed["name"]!, (string)listed["metadata"]!));
                Assert.Equal("{\"avdelning\":\"kassa\"}", Assert.Single(await Run("container_get", new() { ["container"] = container })).Fields["metadata"]);

                var note = Assert.Single(await Run("upload", new() { ["container"] = container, ["blob"] = "anteckningar/stängt å.txt", ["text"] = "Kafé Ada 35 kr", ["metadata"] = "{\"kund\": \"ada\"}", ["tags"] = "{\"projekt\": \"kassa\"}" })).Fields;
                Assert.Equal(15L, note["size"]);
                Assert.Equal("anteckningar/stängt å.txt", Assert.Single(await Run("upload", new() { ["container"] = container, ["blob"] = "anteckningar/stängt å.txt", ["query"] = "Kassan stängd\nÅter 08:00" })).Fields["name"]); // replaced, as n8n's node does
                Assert.StartsWith("Azure Storage answered 409 BlobAlreadyExists", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("upload", new() { ["container"] = container, ["blob"] = "anteckningar/stängt å.txt", ["text"] = "x", ["overwrite"] = "false" }))).Message, StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(_root, "juni.csv"), "datum;summa\n2026-06-01;35\n");
                await Run("upload", new() { ["container"] = container, ["blob"] = "2026/", ["file"] = "juni.csv", ["tier"] = "Cool" });
                await Run("upload", new() { ["container"] = container, ["blob"] = "2026/juli.csv", ["text"] = "datum;summa", ["content_type"] = "text/csv" });

                var folder = await Run("blobs", new() { ["container"] = container, ["prefix"] = "2026/", ["include"] = "metadata, tags", ["max"] = "1" });
                Assert.Equal("2026/juli.csv", Assert.Single(folder).Fields["name"]); // no more than asked
                var all = await Run("blobs", new() { ["container"] = container, ["include"] = "metadata,tags" });
                Assert.Equal(new[] { "2026/juli.csv", "2026/juni.csv", "anteckningar/stängt å.txt" }, all.Select(item => (string)item.Fields["name"]!));
                var stängt = all[2].Fields;
                Assert.Equal((26L, "text/plain; charset=utf-8"), ((long)stängt["contentLength"]!, (string)stängt["contentType"]!));
                Assert.Equal(("Cool", "text/csv"), ((string)all[1].Fields["accessTier"]!, (string)all[0].Fields["contentType"]!));
                Assert.Matches("^20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", (string)stängt["lastModified"]!);

                var got = Assert.Single(await Run("get", new() { ["container"] = container, ["blob"] = "2026/juni.csv" })).Fields;
                Assert.Equal((26L, "BlockBlob", "Cool"), ((long)got["contentLength"]!, (string)got["blobType"]!, (string)got["accessTier"]!));
                Assert.Equal(("downloads/stängt å.txt", 26L), ((string)Assert.Single(await Run("download", new() { ["container"] = container, ["blob"] = "anteckningar/stängt å.txt", ["file"] = "downloads/" })).Fields["file"]!, new FileInfo(Path.Combine(_root, "downloads", "stängt å.txt")).Length));
                Assert.Equal("Kassan stängd\nÅter 08:00", File.ReadAllText(Path.Combine(_root, "downloads", "stängt å.txt")));

                await Run("copy", new() { ["container"] = container, ["blob"] = "2026/juni.csv", ["to_blob"] = "arkiv/juni.csv" });
                Assert.Equal(26L, Assert.Single(await Run("get", new() { ["container"] = container, ["blob"] = "arkiv/juni.csv" })).Fields["contentLength"]);
                Assert.True((bool)Assert.Single(await Run("delete", new() { ["container"] = container, ["blob"] = "arkiv/juni.csv" })).Fields["deleted"]!);
                Assert.StartsWith("Azure Storage answered 404", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("get", new() { ["container"] = container, ["blob"] = "arkiv/juni.csv" }))).Message, StringComparison.Ordinal);
            }
            finally
            {
                await Run("container_delete", new() { ["container"] = container });
            }

            Assert.Empty(await Run("containers", new() { ["prefix"] = container }));
        }
    }
}
