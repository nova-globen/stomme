#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Connectivity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The Extract from connection step DRIVEN on installed hosts on SQLite, PostgreSQL and RavenDB, with a fixture extracting
    /// connector composed into the host: a connection created over <c>api/Connections</c> with its credentials (into the site's
    /// secret store), a pipeline authored over <c>api/Pipelines</c> - extract, validate exactly three records, log - published and
    /// started, so the step runs on the engine's worker, enters its site and reaches the REAL extraction service. The connector
    /// received the credentials from the vault, the operation and the parameters; the cap truncated five records to three and the
    /// run log says so; the records reached the next step through the records convention.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ExtractFromConnectionDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_pipeline_reads_a_connection_through_its_connector_with_the_vaulted_credentials(string storeName)
        {
            var fixture = new ProbeExtractor("fixture-extract", count: 5) { AuthType = AuthenticationType.Basic };
            Action<WebApplicationBuilder> configure = builder => builder.Services.AddSingleton<IConnector>(fixture);
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output, configure);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var connection = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Connections",
                    new ConnectionRequest { Name = "Warehouse", ConnectorType = "fixture-extract", Endpoint = "Server=warehouse;Database=stock", Credentials = new() { ["username"] = "svc", ["password"] = "s3cret" } }, "the connection");
                Assert.True(connection.Status == HttpStatusCode.OK, connection.Body);
                var connectionId = JsonSerializer.Deserialize<ConnectionView>(connection.Body, Json)!.Id;

                var created = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "From the warehouse" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "extract", Type = "Stomme.ExtractFromConnection", Name = "Extract", Inputs = { ["Connection"] = connectionId.ToString(), ["Operation"] = "list", ["Parameters"] = "region = north", ["MaxRecords"] = "3" } },
                        new PipelineGraphStep { Id = "check", Type = "Stomme.ValidateRecords", Name = "Check", Inputs = { ["Rules"] = "id required\nop required", ["MinRows"] = "3", ["MaxRows"] = "3", ["FailureMode"] = "failfast", ["OnFailure"] = "Continue" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "three records from the warehouse", ["Level"] = "Warning" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "extract", SourcePort = "Done", TargetId = "check" },
                        new PipelineGraphConnection { SourceId = "check", SourcePort = "Passed", TargetId = "log" },
                    },
                };
                Assert.Equal(HttpStatusCode.OK, (await Send(app, client, cookies, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish")).Status);
                var started = await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest { CorrelationId = "warehouse" }, "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                // Dispatched, not awaited: the run answers 404 until the engine has created it.
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, Json)!;
                foreach (var line in log)
                {
                    output.WriteLine($"[{storeName}][log] {line.StepType} {line.EventName} {line.Message}");
                }

                Assert.True(run?.IsFinished, $"the run did not finish: {run?.Status} {run?.SubStatus}");
                Assert.DoesNotContain(log, line => line.StepType == "Stomme.ExtractFromConnection" && line.EventName == "Failed");
                Assert.Contains(log, line => line.StepType == "Stomme.ExtractFromConnection" && line.EventName == "Truncated");
                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.EventName == "Warning" && line.Message == "three records from the warehouse");

                // The connector got what the vault holds and what the step asked for - the pipeline never saw the password.
                Assert.Equal(("svc", "s3cret", "list", "north", "Server=warehouse;Database=stock"),
                    (fixture.LastContext?.Credentials["username"], fixture.LastContext?.Credentials["password"], fixture.LastContext?.OperationName, fixture.LastContext?.Parameters["region"], fixture.LastContext?.Endpoint));
                Assert.Equal(4, fixture.Yielded); // the service reads ONE past the cap to know the cap cut the read short, then stops
                Assert.DoesNotContain(log, line => (line.Message ?? string.Empty).Contains("s3cret", StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);
    }
}
