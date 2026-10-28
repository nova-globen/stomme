#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AwsS3;
using Stomme.Modules.Pipelines.Steps;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Amazon S3 (n8n's AWS S3 and S3 nodes): every call signed with SigV4 for s3 with its payload's hash; objects listed
    /// by continuation token no further than asked, folders as records with a delimiter; a download written into the run's
    /// folder - refused over its cap, by S3's length or by what arrives - and an upload of a file of the folder signed with its
    /// bytes' hash, sent whole again after a throttle, or of text; copy, delete, folders and buckets; buckets by host or by path;
    /// what could go wrong refused before anything is signed; S3's own code and message on a failure, a bucket in another region
    /// named, a success that is not S3's XML a failure; a test listing the buckets or reading the connection's.
    /// </summary>
    public sealed class AwsS3ConnectorTests : IDisposable
    {
        private const string Host = "https://kafe-docs.s3.eu-north-1.amazonaws.com";

        private static readonly string EmptyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDKAFE", ["secret-key"] = "kafe-secret" };

        private readonly string root = Path.Combine(Path.GetTempPath(), "stomme-s3-" + Guid.NewGuid().ToString("N"));

        public AwsS3ConnectorTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private sealed record Sent(HttpMethod Method, string Url, IReadOnlyDictionary<string, string> Headers, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);
        }

        private sealed class S3 : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Xml(HttpStatusCode.OK, "<ListAllMyBucketsResult/>");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(",", header.Value), StringComparer.Ordinal);
                var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri, headers, body);
                Requests.Add(sent);
                return Answer(sent);
            }
        }

        private static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
            new(status) { Content = new StringContent("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml, Encoding.UTF8, "application/xml") };

        private static string Page(IEnumerable<string> keys, string? next, IEnumerable<string>? folders = null) =>
            "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>kafe-docs</Name><KeyCount>" + keys.Count() + "</KeyCount><IsTruncated>" + (next is null ? "false" : "true") + "</IsTruncated>"
            + (next is null ? "" : "<NextContinuationToken>" + next + "</NextContinuationToken>")
            + string.Concat(keys.Select(key => "<Contents><Key>" + System.Security.SecurityElement.Escape(key) + "</Key><LastModified>2026-10-16T08:00:00.000Z</LastModified><ETag>&quot;9b2cf535f27731c974343645a3985328&quot;</ETag><Size>1042</Size><StorageClass>STANDARD</StorageClass></Contents>"))
            + string.Concat((folders ?? Array.Empty<string>()).Select(folder => "<CommonPrefixes><Prefix>" + folder + "</Prefix></CommonPrefixes>"))
            + "</ListBucketResult>";

        private Task<List<ConnectorRecord>> Run(S3 s3, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", bool files = true, long cap = 1 << 20) =>
            Contexts.DrainAsync(new AwsS3Connector(), Contexts.Extract(new StubEgress(s3), endpoint, operation, parameters, settings ?? new() { ["region"] = "eu-north-1" }, Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(root, cap) : null,
            });

        private async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", bool files = true)
        {
            var s3 = new S3();
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(s3, operation, parameters, settings, endpoint, files));
            Assert.Empty(s3.Requests); // refused before anything was signed or sent
            return refusal.Message;
        }

        [Fact]
        public async Task Objects_are_listed_by_continuation_token_no_further_than_asked_every_call_signed_for_s3()
        {
            var s3 = new S3
            {
                Answer = sent => Xml(HttpStatusCode.OK, sent.Url.Contains("continuation-token=", StringComparison.Ordinal)
                    ? Page(new[] { "invoices/2026/1044.pdf" }, null)
                    : Page(new[] { "invoices/2026/1042.pdf", "invoices/2026/kvitto 1043 å.pdf" }, "tok+en/2==")),
            };

            var objects = await Run(s3, "objects", new() { ["bucket"] = "kafe-docs", ["prefix"] = "invoices/2026/" });
            Assert.Equal(new[] { "invoices/2026/1042.pdf", "invoices/2026/kvitto 1043 å.pdf", "invoices/2026/1044.pdf" }, objects.Select(record => (string?)record.Fields["key"]));
            Assert.Equal(new[]
            {
                $"{Host}/?list-type=2&max-keys=1000&prefix=invoices%2F2026%2F",
                $"{Host}/?list-type=2&max-keys=998&prefix=invoices%2F2026%2F&continuation-token=tok%2Ben%2F2%3D%3D", // S3's token passed back, encoded
            }, s3.Requests.Select(request => request.Url));
            var first = objects[0].Fields;
            Assert.Equal((1042L, "9b2cf535f27731c974343645a3985328", "STANDARD", "2026-10-16T08:00:00.000Z"), ((long?)first["size"], (string?)first["etag"], (string?)first["storage_class"], (string?)first["last_modified"])); // its ETag unquoted
            Assert.All(s3.Requests, request =>
            {
                Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDKAFE/", request.Headers["authorization"], StringComparison.Ordinal);
                Assert.Contains("/eu-north-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=", request.Headers["authorization"], StringComparison.Ordinal);
                Assert.Equal(EmptyHash, request.Headers["x-amz-content-sha256"]);
            });

            s3.Requests.Clear();
            Assert.Equal(2, (await Run(s3, "objects", new() { ["bucket"] = "kafe-docs", ["max"] = "2" })).Count); // no further than asked
            Assert.Equal($"{Host}/?list-type=2&max-keys=2", Assert.Single(s3.Requests).Url);

            // One folder level with a delimiter: its folders are records of their own; by path, the connection's bucket.
            var level = new S3 { Answer = _ => Xml(HttpStatusCode.OK, Page(new[] { "invoices/readme.txt" }, null, new[] { "invoices/2025/", "invoices/2026/" })) };
            var listed = await Run(level, "objects", new() { ["prefix"] = "invoices/", ["delimiter"] = "/", ["start_after"] = "invoices/a" }, new() { ["region"] = "us-east-1", ["path_style"] = "true", ["bucket"] = "kafe.docs" }, "http://127.0.0.1:9000");
            Assert.Equal("http://127.0.0.1:9000/kafe.docs?list-type=2&max-keys=1000&prefix=invoices%2F&delimiter=%2F&start-after=invoices%2Fa", Assert.Single(level.Requests).Url);
            Assert.Equal(("invoices/readme.txt", "invoices/2025/", true), ((string?)listed[0].Fields["key"], (string?)listed[1].Fields["prefix"], (bool?)listed[1].Fields["folder"]));
            Assert.Contains("/us-east-1/s3/aws4_request", level.Requests[0].Headers["authorization"], StringComparison.Ordinal);

            var folders = new S3 { Answer = _ => Xml(HttpStatusCode.OK, Page(new[] { "invoices/" }, null, new[] { "invoices/2025/", "invoices/2026/" })) };
            var named = await Run(folders, "folders", new() { ["bucket"] = "kafe-docs", ["folder"] = "/invoices/" });
            Assert.Equal(new[] { ("invoices/2025/", "2025"), ("invoices/2026/", "2026") }, named.Select(record => ((string?)record.Fields["folder"], (string?)record.Fields["name"]))); // the folder's own marker left out
            Assert.Equal($"{Host}/?list-type=2&max-keys=1000&prefix=invoices%2F&delimiter=%2F", Assert.Single(folders.Requests).Url);
        }

        [Fact]
        public async Task A_download_is_written_into_the_runs_folder_and_refused_over_its_cap()
        {
            var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x0A, 0x1B };
            var s3 = new S3
            {
                Answer = _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
                    response.Headers.TryAddWithoutValidation("x-amz-version-id", "v7");
                    return response;
                },
            };

            var record = Assert.Single(await Run(s3, "download", new() { ["bucket"] = "kafe-docs", ["key"] = "invoices/2026/kvitto 1042 å.pdf", ["file"] = "downloads/1042.pdf" })).Fields;
            Assert.Equal($"{Host}/invoices/2026/kvitto%201042%20%C3%A5.pdf", Assert.Single(s3.Requests).Url); // the key encoded segment by segment
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "downloads", "1042.pdf"))); // the very bytes
            Assert.Equal(("downloads/1042.pdf", 8L, "application/pdf", "abc123", "v7"), ((string?)record["file"], (long?)record["size"], (string?)record["content_type"], (string?)record["etag"], (string?)record["version_id"]));

            // Over the cap by the length S3 names: refused before the body is read, nothing written.
            var large = new S3
            {
                Answer = _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    response.Content.Headers.ContentLength = 100_000_000;
                    return response;
                },
            };
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(large, "download", new() { ["bucket"] = "kafe-docs", ["key"] = "big.pdf", ["file"] = "big.pdf" }));
            Assert.Equal("'big.pdf' is 100000000 bytes, over the pipeline folder's 1048576; it is not fetched.", refused.Message);
            Assert.False(File.Exists(Path.Combine(root, "big.pdf")));

            // Over the cap with no length named: stopped at the cap, nothing left under the name or beside it.
            var unnamed = new S3
            {
                Answer = _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) };
                    response.Content.Headers.ContentLength = null;
                    return response;
                },
            };
            var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unnamed, "download", new() { ["bucket"] = "kafe-docs", ["key"] = "big.pdf", ["file"] = "big.pdf" }, cap: 16));
            Assert.Equal("The file is over Pipelines:Files:MaxFileBytes (16); nothing was written to 'big.pdf'.", stopped.Message);
            Assert.Empty(Directory.GetFiles(root, "big.pdf*"));

            // A download names a file of the folder - never outside it - and needs a run's folder at all.
            var outside = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } };
            Assert.Equal("'../escape.pdf' resolves outside the site's folder.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(outside, "download", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "../escape.pdf" }))).Message);
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("download", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "a.pdf" }, files: false));
            Assert.Equal("Give the file - where in the site's pipeline folder the object is written.", await Refused("download", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf" }));
        }

        [Fact]
        public async Task An_upload_sends_the_files_bytes_signed_with_their_hash_whole_again_after_a_throttle()
        {
            var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
            Directory.CreateDirectory(Path.Combine(root, "reports"));
            File.WriteAllBytes(Path.Combine(root, "reports", "june.pdf"), bytes);
            var attempts = 0;
            var s3 = new S3
            {
                Answer = _ =>
                {
                    if (attempts++ == 0)
                    {
                        var slow = Xml(HttpStatusCode.ServiceUnavailable, "<Error><Code>SlowDown</Code><Message>Please reduce your request rate.</Message></Error>");
                        slow.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return slow;
                    }

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e7\"");
                    return response;
                },
            };

            var record = Assert.Single(await Run(s3, "upload", new() { ["bucket"] = "kafe-docs", ["key"] = "reports/2026/june.pdf", ["file"] = "reports/june.pdf", ["storage_class"] = "standard_ia", ["acl"] = "bucket-owner-full-control", ["metadata"] = "{\"Order\": \"1042\", \"kassa\": 3}" })).Fields;
            Assert.Equal(2, s3.Requests.Count); // throttled once, then sent again
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.All(s3.Requests, sent =>
            {
                Assert.Equal((HttpMethod.Put, $"{Host}/reports/2026/june.pdf"), (sent.Method, sent.Url));
                Assert.Equal(bytes, sent.Body); // the whole file each time - the second attempt from its start
                Assert.Equal(hash, sent.Headers["x-amz-content-sha256"]); // signed with its bytes' hash
                Assert.Equal(("application/pdf", "300000", "STANDARD_IA", "bucket-owner-full-control", "1042", "3"), (sent.Headers["content-type"], sent.Headers["content-length"], sent.Headers["x-amz-storage-class"], sent.Headers["x-amz-acl"], sent.Headers["x-amz-meta-order"], sent.Headers["x-amz-meta-kassa"]));
                Assert.Contains("SignedHeaders=content-type;host;x-amz-acl;x-amz-content-sha256;x-amz-date;x-amz-meta-kassa;x-amz-meta-order;x-amz-storage-class,", sent.Headers["authorization"], StringComparison.Ordinal);
            });
            Assert.Equal(("reports/2026/june.pdf", 300_000L, "e7", "reports/june.pdf"), ((string?)record["key"], (long?)record["size"], (string?)record["etag"], (string?)record["file"]));

            // Text as it is given - not trimmed - typed as text, signed with its own hash; no folder needed.
            var text = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) } };
            await Run(text, "upload", new() { ["bucket"] = "kafe-docs", ["key"] = "notes/kassa.txt", ["text"] = " Kassan är nere \n" }, files: false);
            var sentText = Assert.Single(text.Requests);
            Assert.Equal((" Kassan är nere \n", "text/plain; charset=utf-8"), (sentText.Text, sentText.Headers["content-type"]));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(" Kassan är nere \n"))).ToLowerInvariant(), sentText.Headers["x-amz-content-sha256"]);

            var typed = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) } };
            await Run(typed, "upload", new() { ["bucket"] = "kafe-docs", ["key"] = "data/orders", ["file"] = "reports/june.pdf", ["content_type"] = "application/json" });
            Assert.Equal("application/json", Assert.Single(typed.Requests).Headers["content-type"]); // as given, over the extension

            Assert.Equal("'reports/missing.pdf' is not a file in the site's pipeline folder.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new S3(), "upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "reports/missing.pdf" }))).Message);
            Assert.Equal("'reports/june.pdf' is 300000 bytes, over Pipelines:Files:MaxFileBytes (1000).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new S3(), "upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "reports/june.pdf" }, cap: 1000))).Message);
            Assert.Equal("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "reports/june.pdf" }, files: false));
        }

        [Fact]
        public async Task Copy_delete_folders_and_buckets_take_s3s_shapes()
        {
            var s3 = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<CopyObjectResult><LastModified>2026-10-16T09:00:00.000Z</LastModified><ETag>\"c0\"</ETag></CopyObjectResult>") };
            var copied = Assert.Single(await Run(s3, "copy", new() { ["bucket"] = "kafe-archive", ["key"] = "2026/kvitto 1042.pdf", ["source"] = "kafe.docs/invoices/kvitto 1042.pdf" })).Fields;
            var copy = Assert.Single(s3.Requests);
            Assert.Equal((HttpMethod.Put, "https://kafe-archive.s3.eu-north-1.amazonaws.com/2026/kvitto%201042.pdf", "kafe.docs/invoices/kvitto%201042.pdf"), (copy.Method, copy.Url, copy.Headers["x-amz-copy-source"])); // a dotted source bucket is a header, not a host
            Assert.Contains("x-amz-copy-source;", copy.Headers["authorization"], StringComparison.Ordinal);
            Assert.Equal(("kafe.docs/invoices/kvitto 1042.pdf", "c0"), ((string?)copied["source"], (string?)copied["etag"]));

            // A copy's failure inside its 200 is a failure.
            var failedCopy = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<Error><Code>InternalError</Code><Message>We encountered an internal error.</Message></Error>") };
            Assert.Equal("HTTP 200 InternalError: We encountered an internal error.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(failedCopy, "copy", new() { ["bucket"] = "kafe-docs", ["key"] = "b.pdf", ["source"] = "kafe-docs/a.pdf" }))).Message);

            var plain = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
            Assert.True((bool?)Assert.Single(await Run(plain, "delete", new() { ["bucket"] = "kafe-docs", ["key"] = "a b.pdf" })).Fields["deleted"]);
            Assert.Equal((HttpMethod.Delete, $"{Host}/a%20b.pdf"), (plain.Requests[^1].Method, plain.Requests[^1].Url));
            await Run(plain, "create_folder", new() { ["bucket"] = "kafe-docs", ["folder"] = "invoices/2026" });
            Assert.Equal((HttpMethod.Put, $"{Host}/invoices/2026/", 0), (plain.Requests[^1].Method, plain.Requests[^1].Url, plain.Requests[^1].Body.Length)); // a folder is an empty key ending with /

            // Folder delete: every key under it, a request of up to a thousand, MD5 named; until nothing is left.
            var round = 0;
            var folder = new S3
            {
                Answer = sent => sent.Method == HttpMethod.Post
                    ? Xml(HttpStatusCode.OK, "<DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>")
                    : Xml(HttpStatusCode.OK, round++ == 0 ? Page(new[] { "invoices/2026/", "invoices/2026/a&b.pdf" }, null) : Page(Array.Empty<string>(), null)),
            };
            Assert.Equal(2, (int?)Assert.Single(await Run(folder, "delete_folder", new() { ["bucket"] = "kafe-docs", ["folder"] = "invoices/2026/" })).Fields["deleted"]);
            var batch = folder.Requests.Single(sent => sent.Method == HttpMethod.Post);
            Assert.Equal($"{Host}/?delete", batch.Url);
            Assert.Equal("<Delete xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Quiet>true</Quiet><Object><Key>invoices/2026/</Key></Object><Object><Key>invoices/2026/a&amp;b.pdf</Key></Object></Delete>", batch.Text);
            Assert.Equal(Convert.ToBase64String(MD5.HashData(batch.Body)), batch.Headers["content-md5"]);
            Assert.Equal($"{Host}/?list-type=2&max-keys=1000&prefix=invoices%2F2026%2F", folder.Requests[0].Url); // under the folder alone

            var refusing = new S3
            {
                Answer = sent => Xml(HttpStatusCode.OK, sent.Method == HttpMethod.Post
                    ? "<DeleteResult><Error><Key>invoices/2026/a.pdf</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error></DeleteResult>"
                    : Page(new[] { "invoices/2026/a.pdf" }, null)),
            };
            Assert.Equal("Amazon S3 refused to delete 'invoices/2026/a.pdf' (AccessDenied: Access Denied); 0 deleted before it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "delete_folder", new() { ["bucket"] = "kafe-docs", ["folder"] = "invoices/2026" }))).Message);
            var sticky = new S3 { Answer = sent => Xml(HttpStatusCode.OK, sent.Method == HttpMethod.Post ? "<DeleteResult/>" : Page(new[] { "invoices/2026/a.pdf" }, null)) };
            Assert.Equal("Amazon S3 still lists 'invoices/2026/a.pdf' after deleting it; 1 deleted before it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(sticky, "delete_folder", new() { ["bucket"] = "kafe-docs", ["folder"] = "invoices/2026" }))).Message); // never a loop

            var buckets = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<ListAllMyBucketsResult><Buckets><Bucket><Name>kafe-docs</Name><CreationDate>2026-01-02T03:04:05.000Z</CreationDate></Bucket><Bucket><Name>kafe-archive</Name></Bucket></Buckets></ListAllMyBucketsResult>") };
            var listed = await Run(buckets, "buckets", new() { ["max"] = "1" });
            Assert.Equal(("kafe-docs", "2026-01-02T03:04:05.000Z"), ((string?)Assert.Single(listed).Fields["name"], (string?)listed[0].Fields["created"]));
            Assert.Equal("https://s3.eu-north-1.amazonaws.com/", buckets.Requests[0].Url); // the store itself, no bucket

            var made = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) };
            await Run(made, "create_bucket", new() { ["bucket"] = "kafe-new" }, new() { ["region"] = "eu-north-1", ["bucket"] = "kafe-docs" });
            Assert.Equal((HttpMethod.Put, "https://kafe-new.s3.eu-north-1.amazonaws.com/", "<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>eu-north-1</LocationConstraint></CreateBucketConfiguration>"), (made.Requests[0].Method, made.Requests[0].Url, made.Requests[0].Text)); // the bucket named, not the connection's
            await Run(made, "create_bucket", new() { ["bucket"] = "kafe-new" }, new() { ["region"] = "us-east-1" });
            Assert.Empty(made.Requests[^1].Body); // us-east-1 takes no location
            await Run(made, "delete_bucket", new() { ["bucket"] = "kafe-new" });
            Assert.Equal((HttpMethod.Delete, "https://kafe-new.s3.eu-north-1.amazonaws.com/"), (made.Requests[^1].Method, made.Requests[^1].Url));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed()
        {
            Assert.Equal("Amazon S3 has no operation 'move' - buckets, create_bucket, delete_bucket, objects, folders, create_folder, delete_folder, download, upload, copy or delete.", await Refused("move", new()));
            Assert.Equal("Give the bucket - its name, or the connection's.", await Refused("objects", new()));
            Assert.Equal("Give the bucket - its name, or the connection's.", await Refused("create_bucket", new(), new() { ["region"] = "eu-north-1", ["bucket"] = "kafe-docs" })); // a bucket made is named, never the connection's
            foreach (var name in new[] { "Kafe-Docs", "ka", "kafe..docs", "kafe.-docs", "-kafe", "192.168.10.20", "kafe_docs" })
            {
                Assert.StartsWith($"'{name}' is not a bucket's name", await Refused("objects", new() { ["bucket"] = name }, new() { ["region"] = "eu-north-1", ["path_style"] = "true" }), StringComparison.Ordinal);
            }

            Assert.Equal("The bucket 'kafe.docs' has a dot in its name - address buckets by path in the connection (its certificate does not cover a dotted name).", await Refused("objects", new() { ["bucket"] = "kafe.docs" }));
            Assert.Equal("'../1042.pdf' has a '.' or '..' segment, which an address on the way would read as another key (key).", await Refused("delete", new() { ["bucket"] = "kafe-docs", ["key"] = "../1042.pdf" }));
            Assert.Equal("'invoices/./1042.pdf' has a '.' or '..' segment, which an address on the way would read as another key (key).", await Refused("delete", new() { ["bucket"] = "kafe-docs", ["key"] = "invoices/./1042.pdf" }));
            Assert.Equal("'/invoices/1042.pdf' starts with a slash; a key does not (key).", await Refused("delete", new() { ["bucket"] = "kafe-docs", ["key"] = "/invoices/1042.pdf" }));
            Assert.Equal("'a\u0001b' holds a control character (key).", await Refused("delete", new() { ["bucket"] = "kafe-docs", ["key"] = "a\u0001b" }));
            Assert.StartsWith("'" + new string('å', 40) + "...' is over S3's 1,024 bytes (key).", await Refused("delete", new() { ["bucket"] = "kafe-docs", ["key"] = new string('å', 513) }), StringComparison.Ordinal);
            Assert.Equal("Give the key - an object's key.", await Refused("delete", new() { ["bucket"] = "kafe-docs" }));
            Assert.Equal("'x/../y' has a '.' or '..' segment, which an address on the way would read as another key (prefix).", await Refused("objects", new() { ["bucket"] = "kafe-docs", ["prefix"] = "x/../y" }));
            Assert.Equal("Give the folder - its path, never empty.", await Refused("delete_folder", new() { ["bucket"] = "kafe-docs", ["folder"] = "/" })); // a bucket is never emptied by a blank
            Assert.Equal("Give the folder - its path, never empty.", await Refused("delete_folder", new() { ["bucket"] = "kafe-docs" }));
            Assert.Equal("'kafe-docs' is not bucket/key (source).", await Refused("copy", new() { ["bucket"] = "kafe-docs", ["key"] = "b.pdf", ["source"] = "kafe-docs" }));
            Assert.Equal("Give the file in the site's pipeline folder to store, or text.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt" }));
            Assert.Equal("Give the file or text, not both.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["file"] = "a.txt", ["text"] = "hej" }));
            Assert.Equal("'pdf' is not a media type - application/pdf (content_type).", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["content_type"] = "pdf" }));
            Assert.StartsWith("'COLD' is not a storage class - STANDARD, ", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["storage_class"] = "COLD" }), StringComparison.Ordinal);
            Assert.StartsWith("'everyone' is not a canned ACL - private, ", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["acl"] = "everyone" }), StringComparison.Ordinal);
            Assert.Equal("'[1]' is not a JSON object of text (metadata).", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["metadata"] = "[1]" }));
            Assert.Equal("Metadata 'stad' - a name of letters, digits and hyphens, a value of plain ASCII text.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["metadata"] = "{\"stad\": \"Malmö\"}" }));
            Assert.Equal("Metadata 'a b' - a name of letters, digits and hyphens, a value of plain ASCII text.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["metadata"] = "{\"a b\": \"x\"}" }));
            Assert.Equal("Metadata 'tags' is not text.", await Refused("upload", new() { ["bucket"] = "kafe-docs", ["key"] = "a.txt", ["text"] = "hej", ["metadata"] = "{\"tags\": [\"x\"]}" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("objects", new() { ["bucket"] = "kafe-docs", ["max"] = "0" }));
            Assert.Equal("'a\u0002' is not a delimiter - a few characters, /.", await Refused("objects", new() { ["bucket"] = "kafe-docs", ["delimiter"] = "a\u0002" }));
            Assert.Equal("Give the region - eu-north-1 (us-east-1 for most S3-compatible stores).", await Refused("objects", new() { ["bucket"] = "kafe-docs" }, new()));
            Assert.Equal("'EU North' is not a region - eu-north-1.", await Refused("objects", new() { ["bucket"] = "kafe-docs" }, new() { ["region"] = "EU North" }));
            Assert.Equal("'https://s3.eu-north-1.amazonaws.com/kafe-docs' is not a store's address - its host alone, https://s3.eu-north-1.amazonaws.com.", await Refused("objects", new() { ["bucket"] = "kafe-docs" }, endpoint: "https://s3.eu-north-1.amazonaws.com/kafe-docs"));
        }

        [Fact]
        public async Task Failures_name_s3s_code_a_bucket_in_another_region_and_what_is_not_s3s_answer()
        {
            var missing = new S3 { Answer = _ => Xml(HttpStatusCode.NotFound, "<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message><Key>a.pdf</Key></Error>") };
            Assert.Equal("HTTP 404 NoSuchKey: The specified key does not exist.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "download", new() { ["bucket"] = "kafe-docs", ["key"] = "a.pdf", ["file"] = "a.pdf" }))).Message);
            Assert.False(File.Exists(Path.Combine(root, "a.pdf"))); // an error is never saved

            var moved = new S3
            {
                Answer = _ =>
                {
                    var response = Xml(HttpStatusCode.MovedPermanently, "<Error><Code>PermanentRedirect</Code><Message>The bucket you are attempting to access must be addressed using the specified endpoint.</Message></Error>");
                    response.Headers.TryAddWithoutValidation("x-amz-bucket-region", "us-west-2");
                    return response;
                },
            };
            Assert.Equal("HTTP 301: the bucket is in us-west-2, not eu-north-1 - set the connection's region to us-west-2.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(moved, "objects", new() { ["bucket"] = "kafe-docs" }))).Message);

            var signIn = new S3 { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html><body><form>Sign in</form></body></html>", Encoding.UTF8, "text/html") } };
            Assert.Equal("Amazon S3 answered HTTP 200 with what is not XML - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(signIn, "objects", new() { ["bucket"] = "kafe-docs" }))).Message);
            var other = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<html><body>Hej</body></html>") };
            Assert.Equal("Amazon S3 answered HTTP 200 with what is not its answer (html, not ListBucketResult) - check the address.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(other, "objects", new() { ["bucket"] = "kafe-docs" }))).Message);
            var endless = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<ListBucketResult><IsTruncated>true</IsTruncated><Contents><Key>a</Key></Contents></ListBucketResult>") };
            Assert.Equal("Amazon S3 said there was more but named no next page.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(endless, "objects", new() { ["bucket"] = "kafe-docs" }))).Message);
        }

        [Fact]
        public async Task A_test_lists_the_buckets_or_reads_the_connections_bucket()
        {
            var s3 = new S3 { Answer = _ => Xml(HttpStatusCode.OK, "<ListAllMyBucketsResult><Buckets><Bucket><Name>a1</Name></Bucket><Bucket><Name>b2</Name></Bucket></Buckets></ListAllMyBucketsResult>") };
            var result = await new AwsS3Connector().TestConnectionAsync(Contexts.Test(new StubEgress(s3), "", new Dictionary<string, string> { ["region"] = "eu-north-1" }, Credentials));
            Assert.True(result.Success);
            Assert.Equal(("S3 in eu-north-1: 2 buckets seen.", "https://s3.eu-north-1.amazonaws.com/"), (result.Message, s3.Requests[0].Url));

            var bucket = new S3 { Answer = _ => Xml(HttpStatusCode.OK, Page(Array.Empty<string>(), null)) };
            var read = await new AwsS3Connector().TestConnectionAsync(Contexts.Test(new StubEgress(bucket), "", new Dictionary<string, string> { ["region"] = "eu-north-1", ["bucket"] = "kafe-docs" }, Credentials));
            Assert.Equal((true, "S3 in eu-north-1: bucket 'kafe-docs' answers, empty."), (read.Success, read.Message));
            Assert.Equal($"{Host}/?list-type=2&max-keys=1", bucket.Requests[0].Url); // a key scoped to one bucket need not list them all

            var denied = new S3 { Answer = _ => Xml(HttpStatusCode.Forbidden, "<Error><Code>InvalidAccessKeyId</Code><Message>The AWS Access Key Id you provided does not exist in our records.</Message></Error>") };
            var failed = await new AwsS3Connector().TestConnectionAsync(Contexts.Test(new StubEgress(denied), "", new Dictionary<string, string> { ["region"] = "eu-north-1" }, Credentials));
            Assert.Equal((false, "HTTP 403 InvalidAccessKeyId: The AWS Access Key Id you provided does not exist in our records."), (failed.Success, failed.Message));
            var noSecret = await new AwsS3Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new S3()), "", new Dictionary<string, string> { ["region"] = "eu-north-1" }, new Dictionary<string, string> { ["access-key"] = "AKID" }));
            Assert.Equal((false, "The Amazon S3 connection has no secret key."), (noSecret.Success, noSecret.Message));
        }
    }
}
