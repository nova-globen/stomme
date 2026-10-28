#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// BL-229 - HighLevel DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for HighLevel's API v2 that refuses a call without the token as a bearer and its Version
    /// header: a HighLevel connection made over the API with its location, the token vaulted, tested; a published pipeline
    /// upserting a contact with a tag and a note, making an opportunity for the contact the first step answered (an expression
    /// over its result) and a task for it, and reading the contact's tasks; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HighLevelDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Token = "pit-highlevel-drive";
        private const string Location = "ve9EPM428h8vShlRW1KT";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for HighLevel's API v2: the token as a bearer and the Version header or a 401, every call kept.</summary>
        private sealed class HighLevelStandIn : IDisposable
        {
            private readonly HttpListener _listener;

            public HighLevelStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

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
                    var body = await reader.ReadToEndAsync();
                    var path = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    Received.Enqueue((method, path, body));
                    var (status, answer) = (method, path.Split('?')[0]) switch
                    {
                        _ when context.Request.Headers["Authorization"] != "Bearer " + Token || context.Request.Headers["Version"] != "2021-07-28" => (401, "{\"statusCode\":401,\"message\":\"Invalid JWT\"}"),
                        ("GET", "/contacts/") => (200, "{\"contacts\":[{\"id\":\"c42\"}],\"meta\":{\"total\":1}}"),
                        ("POST", "/contacts/upsert") => (201, "{\"new\":true,\"contact\":{\"id\":\"c42\",\"email\":\"ada@kafe.example\",\"tags\":[\"stamkund\"]}}"),
                        ("POST", "/contacts/c42/notes") => (201, "{\"note\":{\"id\":\"n7\"}}"),
                        ("POST", "/opportunities/") => (201, "{\"opportunity\":{\"id\":\"o9\",\"name\":\"Kassan\",\"contactId\":" + JsonSerializer.Serialize((string?)JsonNode.Parse(body)!["contactId"]) + ",\"status\":\"open\"}}"),
                        ("POST", "/contacts/c42/tasks") => (201, "{\"task\":{\"id\":\"t3\",\"title\":\"Ring tillbaka\",\"completed\":false}}"),
                        ("GET", "/contacts/c42/tasks") => (200, "{\"tasks\":[{\"id\":\"t3\",\"title\":\"Ring tillbaka\"}]}"),
                        _ => (404, "{\"statusCode\":404,\"message\":\"Not found\"}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        [Fact]
        public async Task A_pipeline_upserts_a_contact_and_makes_its_opportunity_and_task_through_highlevel()
        {
            using var highLevel = new HighLevelStandIn();
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
                    Name = "Kafé säljare", ConnectorType = "highlevel", Endpoint = $"http://127.0.0.1:{highLevel.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["location"] = Location },
                    Credentials = new() { ["token"] = Token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans säljare" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string? parameters = null, string? expression = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                    if (parameters is not null)
                    {
                        step.Inputs["Parameters"] = parameters;
                    }

                    if (expression is not null)
                    {
                        step.InputExpressions["Parameters"] = expression;
                    }

                    return step;
                }

                var upsert = Call("upsert", "contact_upsert", "email = ada@kafe.example\nfirst_name = Ada\ntags = stamkund\nnote = Stamkund sedan 2019.");
                upsert.OutputVariables["Result"] = "v-contact";
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-contact", Name = "contact" } },
                    Steps =
                    {
                        upsert,
                        Call("deal", "opportunity_create", expression: "contact = {{ contact[0].id }}\nname = Kassan\npipeline = p1"),
                        Call("todo", "task_create", expression: "contact = {{ contact[0].id }}\ntitle = Ring tillbaka\ndue = 2026-10-20T09:00:00+02:00"),
                        Call("todos", "tasks", "contact = c42"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var contact = Outputs("upsert")["Result"]![0]!;
                Assert.Equal(("c42", true, "n7"), (contact["id"]!.GetValue<string>(), contact["new"]!.GetValue<bool>(), contact["note_id"]!.GetValue<string>()));
                Assert.Equal("c42", Outputs("deal")["Result"]![0]!["contactId"]!.GetValue<string>()); // the contact the first step answered
                Assert.Equal("t3", Outputs("todo")["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("Ring tillbaka", Outputs("todos")["Result"]![0]!["title"]!.GetValue<string>());

                var upserted = JsonNode.Parse(highLevel.Received.Single(request => request.Path == "/contacts/upsert").Body)!;
                Assert.Equal(("[\"stamkund\"]", Location), (upserted["tags"]!.ToJsonString(), upserted["locationId"]!.GetValue<string>()));
                var deal = JsonNode.Parse(highLevel.Received.Single(request => request.Path == "/opportunities/").Body)!;
                Assert.Equal(("c42", "p1", "open", Location), (deal["contactId"]!.GetValue<string>(), deal["pipelineId"]!.GetValue<string>(), deal["status"]!.GetValue<string>(), deal["locationId"]!.GetValue<string>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
