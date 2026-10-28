#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// BL-229 - AWS Certificate Manager DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which checks every signature
    /// against the IAM key it issued: a connection made over the API and tested; a published pipeline requesting a certificate
    /// for a domain and another name, listing the certificates waiting for validation, describing it and deleting it; the secret
    /// in neither the graph nor the run. Fails, never skips, without moto (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsCertificateManagerDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_requests_lists_describes_and_deletes_a_certificate()
        {
            var secret = MotoTestEndpoint.SecretKey;
            var domain = "kafe-" + Guid.NewGuid().ToString("N")[..8] + ".example";
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MotoTestEndpoint.Host }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé certifikat", ConnectorType = "awsacm", Endpoint = MotoTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["region"] = MotoTestEndpoint.Region },
                    Credentials = new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans certifikat" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-cert", Name = "cert" } },
                    Steps =
                    {
                        new() { Id = "ask", Type = "Stomme.ExtractFromConnection", Name = "ask", Inputs = { ["Connection"] = connection, ["Operation"] = "request", ["Parameters"] = $"domain = {domain}\nother_names = www.{domain}" }, OutputVariables = { ["Result"] = "v-cert" } },
                        new() { Id = "list", Type = "Stomme.ExtractFromConnection", Name = "list", Inputs = { ["Connection"] = connection, ["Operation"] = "certificates", ["Parameters"] = "statuses = PENDING_VALIDATION" } },
                        new() { Id = "read", Type = "Stomme.ExtractFromConnection", Name = "read", Inputs = { ["Connection"] = connection, ["Operation"] = "certificate" }, InputExpressions = { ["Parameters"] = "certificate = {{ cert[0].CertificateArn }}" } },
                        new() { Id = "drop", Type = "Stomme.ExtractFromConnection", Name = "drop", Inputs = { ["Connection"] = connection, ["Operation"] = "delete" }, InputExpressions = { ["Parameters"] = "certificate = {{ cert[0].CertificateArn }}" } },
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
                for (var attempt = 0; attempt < 600 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var arn = Outputs("ask")["Result"]![0]!["CertificateArn"]!.GetValue<string>();
                Assert.Contains(Outputs("list")["Result"]!.AsArray(), found => found!["CertificateArn"]!.GetValue<string>() == arn);
                Assert.Equal(domain, Outputs("read")["Result"]![0]!["DomainName"]!.GetValue<string>());
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
