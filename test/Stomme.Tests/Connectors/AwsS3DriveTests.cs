#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Shared;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Amazon S3 DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the extraction port and
    /// the run's pipeline folder, against a loopback S3-compatible store (buckets by path, as MinIO is reached) that checks every
    /// request's SigV4 signature - its body's hash included - for the connection's key alone. A connection made over the API,
    /// tested (the buckets listed); a published pipeline uploads text, lists a folder over two pages, downloads an object INTO
    /// the run's folder, reads it there with Read text file, uploads that file to another key - the store receiving the same
    /// bytes - copies it, deletes an object, makes a folder, lists it, and deletes it; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsS3DriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIDKAFEDRIVE";
        private const string Csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_moves_files_between_s3_and_its_folder()
        {
            var secret = "s3-" + Guid.NewGuid().ToString("N");
            using var s3 = new S3StandIn(secret);
            s3.Put("invoices/2026/1042.csv", Encoding.UTF8.GetBytes(Csv));
            s3.Put("invoices/2026/1043.csv", Encoding.UTF8.GetBytes("1043"));
            s3.Put("invoices/2026/1044.csv", Encoding.UTF8.GetBytes("1044"));
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé files", ConnectorType = "awss3", Endpoint = $"http://127.0.0.1:{s3.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "us-east-1", ["path_style"] = "true" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé files" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("note", "upload", "bucket = kafe-docs\nkey = notes/kassa.txt\ntext = Kassan är nere"),
                        Call("list", "objects", "bucket = kafe-docs\nprefix = invoices/"),
                        Call("get", "download", "bucket = kafe-docs\nkey = invoices/2026/1042.csv\nfile = downloads/1042.csv"),
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/1042.csv" } },
                        Call("put", "upload", "bucket = kafe-docs\nkey = archive/1042.csv\nfile = downloads/1042.csv"),
                        Call("copy", "copy", "bucket = kafe-docs\nkey = copies/1042.csv\nsource = kafe-docs/archive/1042.csv"),
                        Call("drop", "delete", "bucket = kafe-docs\nkey = invoices/2026/1043.csv"),
                        Call("mkdir", "create_folder", "bucket = kafe-docs\nfolder = reports/2026"),
                        Call("dirs", "folders", "bucket = kafe-docs\nfolder = reports"),
                        Call("rmdir", "delete_folder", "bucket = kafe-docs\nfolder = reports"),
                    },
                };
                for (var i = 1; i < graph.Steps.Count; i++)
                {
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = graph.Steps[i - 1].Id, SourcePort = "Done", TargetId = graph.Steps[i].Id });
                }

                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);
                var received = s3.Received;
                Assert.All(received, request => Assert.True(request.Verified, "refused: " + request.Line)); // every request signed by the key, its body as hashed
                Assert.Equal(new[]
                {
                    "GET /", // the test: the buckets
                    "PUT /kafe-docs/notes/kassa.txt",
                    "GET /kafe-docs?list-type=2&max-keys=1000&prefix=invoices%2F",
                    "GET /kafe-docs?list-type=2&max-keys=998&prefix=invoices%2F&continuation-token=2",
                    "GET /kafe-docs/invoices/2026/1042.csv",
                    "PUT /kafe-docs/archive/1042.csv",
                    "PUT /kafe-docs/copies/1042.csv",
                    "DELETE /kafe-docs/invoices/2026/1043.csv",
                    "PUT /kafe-docs/reports/2026/",
                    "GET /kafe-docs?list-type=2&max-keys=1000&prefix=reports%2F&delimiter=%2F",
                    "GET /kafe-docs?list-type=2&max-keys=1000&prefix=reports%2F",
                    "POST /kafe-docs?delete",
                    "GET /kafe-docs?list-type=2&max-keys=1000&prefix=reports%2F",
                }, received.Select(request => request.Line));

                // The store as the run left it: the text, the downloaded file sent back byte for byte, its copy; the deleted gone.
                Assert.Equal("Kassan är nere", Encoding.UTF8.GetString(s3.Get("notes/kassa.txt")!));
                Assert.Equal(Encoding.UTF8.GetBytes(Csv), s3.Get("archive/1042.csv"));
                Assert.Equal(Encoding.UTF8.GetBytes(Csv), s3.Get("copies/1042.csv"));
                Assert.Null(s3.Get("invoices/2026/1043.csv"));
                Assert.Null(s3.Get("reports/2026/"));
                Assert.Equal(("text/plain; charset=utf-8", "text/csv"), (s3.TypeOf("notes/kassa.txt"), s3.TypeOf("archive/1042.csv")));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(3, Outputs("list")["RecordCount"]!.GetValue<int>()); // both pages
                Assert.Equal(("downloads/1042.csv", (long)Encoding.UTF8.GetByteCount(Csv)), (Outputs("get")["Result"]![0]!["file"]!.GetValue<string>(), Outputs("get")["Result"]![0]!["size"]!.GetValue<long>()));
                Assert.Equal(Csv, Outputs("read")["Result"]!.GetValue<string>()); // the file steps read what was downloaded, in the run's folder
                Assert.Equal("reports/2026/", Outputs("dirs")["Result"]![0]!["folder"]!.GetValue<string>());
                Assert.Equal(1, Outputs("rmdir")["Result"]![0]!["deleted"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A loopback S3-compatible store, buckets by path: one bucket (kafe-docs) in memory, ListObjectsV2 two keys a page (so a
        /// listing follows its continuation token), get, put, copy, delete and the batch delete (its Content-MD5 checked). Every
        /// request is verified as S3 would - its SigV4 signature recomputed for the key's secret at the request's own time, over
        /// the body that arrived - and refused otherwise.
        /// </summary>
        private sealed class S3StandIn : IDisposable
        {
            private const string Bucket = "/kafe-docs";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly SortedDictionary<string, (byte[] Body, string? Type)> _objects = new(StringComparer.Ordinal);
            private readonly List<(string Line, bool Verified)> _received = new();

            public S3StandIn(string secret)
            {
                _secret = secret;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Verified)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            public void Put(string key, byte[] body)
            {
                lock (_gate)
                {
                    _objects[key] = (body, null);
                }
            }

            public byte[]? Get(string key)
            {
                lock (_gate)
                {
                    return _objects.TryGetValue(key, out var found) ? found.Body : null;
                }
            }

            public string? TypeOf(string key)
            {
                lock (_gate)
                {
                    return _objects.TryGetValue(key, out var found) ? found.Type : null;
                }
            }

            // The signature S3 would compute: the same method, address, x-amz-* headers and content type, the body as it arrived, at
            // the request's own time - and the payload hash it was signed with the hash of that body.
            private bool Verify(HttpListenerRequest request, byte[] body)
            {
                var date = request.Headers["x-amz-date"];
                var authorization = request.Headers["Authorization"];
                var hash = request.Headers["x-amz-content-sha256"];
                if (date is null || authorization is null || hash != AwsSigV4.Hex(SHA256.HashData(body)))
                {
                    return false;
                }

                using var again = new HttpRequestMessage(new HttpMethod(request.HttpMethod), $"http://127.0.0.1:{Port}{request.RawUrl}");
                if (body.Length > 0 || request.HttpMethod is "PUT" or "POST")
                {
                    again.Content = new ByteArrayContent(body);
                    if (request.ContentType is { } type)
                    {
                        again.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                    }
                }

                foreach (var name in request.Headers.AllKeys.Where(name => name is not null && name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) && name is not ("x-amz-date" or "x-amz-content-sha256")))
                {
                    again.Headers.TryAddWithoutValidation(name!, request.Headers[name]);
                }

                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "us-east-1", "s3", AccessKey, _secret, null, when, signPayloadHeader: true);
                return again.Headers.GetValues("Authorization").Single() == authorization;
            }

            private static Dictionary<string, string> Query(string raw) =>
                raw.Contains('?') ? raw[(raw.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);

            private static string KeyOf(string path) => string.Join("/", path[(Bucket.Length + 1)..].Split('/').Select(Uri.UnescapeDataString));

            private (int Status, string Xml) List(Dictionary<string, string> query)
            {
                var prefix = query.GetValueOrDefault("prefix") ?? "";
                var delimiter = query.GetValueOrDefault("delimiter");
                var size = Math.Min(2, int.Parse(query.GetValueOrDefault("max-keys") ?? "1000", CultureInfo.InvariantCulture));
                var start = query.TryGetValue("continuation-token", out var token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
                List<(string Key, bool Folder)> entries;
                lock (_gate)
                {
                    entries = _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(key => delimiter is not null && key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var at and >= 0 ? (key[..(at + delimiter.Length)], true) : (key, false))
                        .Distinct().ToList();
                }

                var page = entries.Skip(start).Take(size).ToList();
                var more = start + size < entries.Count;
                var xml = new XElement("ListBucketResult",
                    new XElement("Name", "kafe-docs"), new XElement("KeyCount", page.Count), new XElement("IsTruncated", more ? "true" : "false"),
                    more ? new XElement("NextContinuationToken", (start + size).ToString(CultureInfo.InvariantCulture)) : null,
                    page.Select(entry => entry.Folder
                        ? new XElement("CommonPrefixes", new XElement("Prefix", entry.Key))
                        : new XElement("Contents", new XElement("Key", entry.Key), new XElement("Size", Get(entry.Key)!.Length), new XElement("ETag", "\"e\""), new XElement("StorageClass", "STANDARD"))));
                return (200, xml.ToString(SaveOptions.DisableFormatting));
            }

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

                    byte[] body;
                    using (var buffer = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(buffer);
                        body = buffer.ToArray();
                    }

                    var raw = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var query = Query(raw);
                    var verified = Verify(context.Request, body);
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}", verified));
                    }

                    byte[]? answer = null;
                    var status = 200;
                    if (!verified)
                    {
                        (status, answer) = (403, Encoding.UTF8.GetBytes("<Error><Code>SignatureDoesNotMatch</Code><Message>The request signature we calculated does not match the signature you provided.</Message></Error>"));
                    }
                    else if (method == "GET" && path == "/")
                    {
                        answer = Encoding.UTF8.GetBytes("<ListAllMyBucketsResult><Buckets><Bucket><Name>kafe-docs</Name></Bucket></Buckets></ListAllMyBucketsResult>");
                    }
                    else if (method == "GET" && path == Bucket)
                    {
                        var (code, xml) = List(query);
                        (status, answer) = (code, Encoding.UTF8.GetBytes(xml));
                    }
                    else if (method == "POST" && path == Bucket && query.ContainsKey("delete"))
                    {
                        if (context.Request.Headers["Content-MD5"] != Convert.ToBase64String(MD5.HashData(body)))
                        {
                            (status, answer) = (400, Encoding.UTF8.GetBytes("<Error><Code>InvalidDigest</Code><Message>The Content-MD5 you specified was invalid.</Message></Error>"));
                        }
                        else
                        {
                            foreach (var key in XElement.Parse(Encoding.UTF8.GetString(body)).Descendants().Where(element => element.Name.LocalName == "Key").Select(element => element.Value))
                            {
                                lock (_gate)
                                {
                                    _objects.Remove(key);
                                }
                            }

                            answer = Encoding.UTF8.GetBytes("<DeleteResult/>");
                        }
                    }
                    else if (path.StartsWith(Bucket + "/", StringComparison.Ordinal))
                    {
                        var key = KeyOf(path);
                        switch (method)
                        {
                            case "GET":
                                if (Get(key) is { } found)
                                {
                                    answer = found;
                                    context.Response.ContentType = "text/csv";
                                }
                                else
                                {
                                    (status, answer) = (404, Encoding.UTF8.GetBytes("<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message></Error>"));
                                }

                                break;
                            case "PUT" when context.Request.Headers["x-amz-copy-source"] is { } copySource:
                                var from = string.Join("/", copySource.Split('/').Select(Uri.UnescapeDataString));
                                lock (_gate)
                                {
                                    _objects[key] = _objects[from["kafe-docs/".Length..]];
                                }

                                answer = Encoding.UTF8.GetBytes("<CopyObjectResult><LastModified>2026-10-16T10:00:00.000Z</LastModified><ETag>\"c\"</ETag></CopyObjectResult>");
                                break;
                            case "PUT":
                                lock (_gate)
                                {
                                    _objects[key] = (body, context.Request.ContentType);
                                }

                                context.Response.AddHeader("ETag", "\"p\"");
                                break;
                            case "DELETE":
                                lock (_gate)
                                {
                                    _objects.Remove(key);
                                }

                                status = 204;
                                break;
                            default:
                                status = 405;
                                break;
                        }
                    }
                    else
                    {
                        status = 404;
                    }

                    context.Response.StatusCode = status;
                    if (answer is not null && answer.Length > 0)
                    {
                        context.Response.ContentType ??= "application/xml";
                        await context.Response.OutputStream.WriteAsync(answer);
                    }

                    context.Response.Close();
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
