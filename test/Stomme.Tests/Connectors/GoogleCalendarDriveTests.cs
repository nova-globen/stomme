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
    /// Google Calendar DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Google's token endpoint and the Calendar API: a Google Calendar connection made over
    /// the API, its client secret and refresh token vaulted, tested (the calendar named); a published pipeline asks whether the
    /// afternoon is free, books a meeting in a time zone with its attendees told, reads the day's events over two pages, moves
    /// the meeting to another room and cancels it - the refresh token exchanged once, every call a bearer; neither secret is in
    /// the graph or the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleCalendarDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_checks_the_afternoon_books_a_meeting_reads_the_day_moves_and_cancels_it()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            var refresh = "refresh-" + Guid.NewGuid().ToString("N");
            using var google = new CalendarStandIn();
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
                    Name = "Team calendar", ConnectorType = "googlecalendar", Endpoint = $"http://127.0.0.1:{google.Port}/calendar/v3", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Book the review" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-day", Name = "day" } },
                    Steps =
                    {
                        Call("free", "Is the afternoon free", "availability", "calendar = team@example.com\ntime_min = 2026-10-12T13:00:00+02:00\ntime_max = 2026-10-12T17:00:00+02:00"),
                        Call("book", "Book the review", "create_event", "calendar = team@example.com\nsummary = Order review\nstart = 2026-10-12 14:00\ntime_zone = Europe/Stockholm\nattendees = ada@example.com, grace@example.com\nnotify = all"),
                        Call("day", "The day", "events", "calendar = team@example.com\ntime_min = 2026-10-12T00:00:00+02:00\ntime_max = 2026-10-13T00:00:00+02:00"),
                        Call("move", "Another room", "update_event", "calendar = team@example.com\nevent = rev42\nlocation = Room 3"),
                        Call("cancel", "Cancel it", "delete_event", "calendar = team@example.com\nevent = rev42\nnotify = all"),
                    },
                };
                graph.Steps[2].OutputVariables["Result"] = "v-day";
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
                Assert.Equal("""{"timeMin":"2026-10-12T11:00:00Z","timeMax":"2026-10-12T15:00:00Z","items":[{"id":"team@example.com"}]}""", received.Single(request => request.Path == "/calendar/v3/freeBusy").Body);
                var booked = received.Single(request => request.Method == "POST" && request.Path == "/calendar/v3/calendars/team@example.com/events");
                Assert.Equal(("?sendUpdates=all", """{"summary":"Order review","start":{"dateTime":"2026-10-12T14:00:00","timeZone":"Europe/Stockholm"},"end":{"dateTime":"2026-10-12T15:00:00","timeZone":"Europe/Stockholm"},"attendees":[{"email":"ada@example.com"},{"email":"grace@example.com"}]}"""), (booked.Query, booked.Body));
                Assert.Equal(2, received.Count(request => request.Method == "GET" && request.Path == "/calendar/v3/calendars/team@example.com/events")); // both pages
                Assert.Equal("""{"location":"Room 3"}""", received.Single(request => request.Method == "PATCH").Body);
                Assert.Equal(("/calendar/v3/calendars/team@example.com/events/rev42", "?sendUpdates=all"), (received.Single(request => request.Method == "DELETE").Path, received.Single(request => request.Method == "DELETE").Query));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Standup", steps);
                Assert.Contains("Retro", steps); // the second page's event too
                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the Calendar API: a calendar, its afternoon free, a meeting booked as rev42, the day's events over two pages, the meeting moved and cancelled.</summary>
        private sealed class CalendarStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public CalendarStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string Query, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Event(string id, string summary, string start, string end) =>
                "{\"id\":\"" + id + "\",\"summary\":\"" + summary + "\",\"status\":\"confirmed\",\"htmlLink\":\"https://calendar.example/event?eid=" + id
                    + "\",\"start\":{\"dateTime\":\"" + start + "\"},\"end\":{\"dateTime\":\"" + end + "\"},\"organizer\":{\"email\":\"team@example.com\"}}";

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

                    var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
                    var query = context.Request.Url.Query;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, query, context.Request.Headers["Authorization"], body));
                    }

                    const string Events = "/calendar/v3/calendars/team@example.com/events";
                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/calendar/v3/calendars/primary") => (200, """{"id":"team@example.com","summary":"Team","timeZone":"Europe/Stockholm"}"""),
                        ("POST", "/calendar/v3/freeBusy") => (200, """{"calendars":{"team@example.com":{"busy":[]}}}"""),
                        ("POST", Events) => (200, Event("rev42", "Order review", "2026-10-12T14:00:00+02:00", "2026-10-12T15:00:00+02:00")),
                        ("GET", Events) when query.Contains("pageToken=p2", StringComparison.Ordinal) => (200, "{\"items\":[" + Event("e2", "Retro", "2026-10-12T16:00:00+02:00", "2026-10-12T17:00:00+02:00") + "]}"),
                        ("GET", Events) => (200, "{\"items\":[" + Event("e1", "Standup", "2026-10-12T09:00:00+02:00", "2026-10-12T09:15:00+02:00") + "],\"nextPageToken\":\"p2\"}"),
                        ("PATCH", Events + "/rev42") => (200, Event("rev42", "Order review", "2026-10-12T14:00:00+02:00", "2026-10-12T15:00:00+02:00")),
                        ("DELETE", Events + "/rev42") => (204, ""),
                        _ => (404, """{"error":{"code":404,"message":"Not Found","status":"NOT_FOUND"}}"""),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    if (bytes.Length > 0)
                    {
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
