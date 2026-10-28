#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - YouTube DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Google's token endpoint and the YouTube Data API: a YouTube connection made over the API,
    /// its client and refresh token vaulted, tested; a published pipeline makes a playlist with an accent, puts a video in it,
    /// reads the account's videos over two pages - no more than asked - changes a video's title with the rest kept, likes it and
    /// deletes the playlist - every call the exchanged access token; the secret and the refresh token in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class YouTubeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Playlist = "PLBCF2DAC6FFB574DE";
        private const string Video = "dQw4w9WgXcQ";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_a_playlist_fills_it_reads_changes_and_likes_videos_and_deletes_it()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var google = new YouTubeStandIn();
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
                    Name = "Channel", ConnectorType = "youtube", Endpoint = $"http://127.0.0.1:{google.Port}/youtube/v3", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Channel" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("list", "New list", "create_playlist", "title = Fika & café\ndescription = Our fika\nprivacy = unlisted\ntags = fika, café"),
                        Call("add", "Add video", "add_playlist_item", $"playlist = {Playlist}\nvideo = {Video}\nposition = 0"),
                        Call("videos", "My videos", "videos", "max = 70"),
                        Call("retitle", "Retitle", "update_video", $"video = {Video}\ntitle = Fika & bullar"),
                        Call("like", "Like", "rate_video", $"video = {Video}\nrating = like"),
                        Call("drop", "Drop list", "delete_playlist", $"playlist = {Playlist}"),
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
                var received = google.Received;
                Assert.Contains("refresh_token=" + refresh, received.First(request => request.Path == "/token").Body);
                Assert.All(received.Where(request => request.Path != "/token"), request => Assert.Equal("Bearer access-drive", request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /youtube/v3/channels?part=snippet&mine=true",
                    "POST /youtube/v3/playlists?part=snippet,status {\"snippet\":{\"title\":\"Fika & café\",\"description\":\"Our fika\",\"tags\":[\"fika\",\"café\"]},\"status\":{\"privacyStatus\":\"unlisted\"}}",
                    $"POST /youtube/v3/playlistItems?part=snippet {{\"snippet\":{{\"playlistId\":\"{Playlist}\",\"resourceId\":{{\"kind\":\"youtube#video\",\"videoId\":\"{Video}\"}},\"position\":0}}}}",
                    "GET /youtube/v3/search?part=snippet&type=video&forMine=true&maxResults=50",
                    "GET /youtube/v3/search?part=snippet&type=video&forMine=true&maxResults=20&pageToken=CDIQAA",
                    $"GET /youtube/v3/videos?part=snippet,status&id={Video}",
                    $"PUT /youtube/v3/videos?part=snippet,status {{\"id\":\"{Video}\",\"snippet\":{{\"title\":\"Fika & bullar\",\"description\":\"Kept\",\"tags\":[\"fika\"],\"categoryId\":\"26\"}},\"status\":{{\"privacyStatus\":\"public\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true,\"selfDeclaredMadeForKids\":false}}}}",
                    $"POST /youtube/v3/videos/rate?id={Video}&rating=like",
                    $"DELETE /youtube/v3/playlists?id={Playlist}",
                }, received.Where(request => request.Path != "/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var videos = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "videos")!;
                Assert.Contains("\"RecordCount\": 70", videos["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the YouTube Data API: the account's channel, a playlist made and deleted, a video put in it, the account's videos over two pages, a video read, changed and rated.</summary>
        private sealed class YouTubeStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public YouTubeStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string Body, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Found(int from, int count, string? next) =>
                "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":{\"kind\":\"youtube#video\",\"videoId\":\"v" + n.ToString("0000000000", System.Globalization.CultureInfo.InvariantCulture) + "\"},\"snippet\":{\"title\":\"Clip " + n + "\",\"channelId\":\"UC_x5XG1OV2P6uZZ5FSM9Ttw\"}}"))
                + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";

            private const string VideoJson = "{\"id\":\"" + Video + "\",\"snippet\":{\"title\":\"Fika\",\"description\":\"Kept\",\"tags\":[\"fika\"],\"categoryId\":\"26\",\"channelId\":\"UC_x5XG1OV2P6uZZ5FSM9Ttw\"},"
                + "\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"public\",\"license\":\"youtube\",\"embeddable\":true,\"publicStatsViewable\":true,\"madeForKids\":false,\"selfDeclaredMadeForKids\":false}}";

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

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add((path, $"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 || path == "/token" ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/youtube/v3/channels") => (200, """{"items":[{"id":"UC_x5XG1OV2P6uZZ5FSM9Ttw","snippet":{"title":"Café"}}]}"""),
                        ("POST", "/youtube/v3/playlists") => (200, "{\"id\":\"" + Playlist + "\",\"snippet\":{\"title\":\"Fika & café\"},\"status\":{\"privacyStatus\":\"unlisted\"}}"),
                        ("POST", "/youtube/v3/playlistItems") => (200, "{\"id\":\"UExCQ0YyREFDNkZGQjU3NERF\",\"snippet\":{\"playlistId\":\"" + Playlist + "\",\"position\":0,\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\"" + Video + "\"}}}"),
                        ("GET", "/youtube/v3/search") when url.Query.Contains("pageToken=CDIQAA", StringComparison.Ordinal) => (200, Found(51, 50, "CGQQAA")),
                        ("GET", "/youtube/v3/search") => (200, Found(1, 50, "CDIQAA")),
                        ("GET", "/youtube/v3/videos") => (200, "{\"items\":[" + VideoJson + "]}"),
                        ("PUT", "/youtube/v3/videos") => (200, VideoJson),
                        ("POST", "/youtube/v3/videos/rate") => (204, ""),
                        ("DELETE", "/youtube/v3/playlists") => (204, ""),
                        _ => (404, """{"error":{"code":404,"message":"Not found.","status":"NOT_FOUND"}}"""),
                    };
                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
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
