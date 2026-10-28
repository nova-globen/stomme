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
    /// BL-229 - AWS Elastic Load Balancing DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which checks every signature
    /// against the IAM key it issued: a connection made over the API and tested; a published pipeline creating an application load
    /// balancer in two of moto's default subnets with a tag, reading it by the ARN the first step answered (an expression over its
    /// result), finding it by name, listing its listeners - none - and deleting it; the secret in neither the graph nor the run.
    /// Fails, never skips, without moto (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsElbDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_and_deletes_a_load_balancer()
        {
            var secret = MotoTestEndpoint.SecretKey;
            var name = "kafe-" + Guid.NewGuid().ToString("N")[..8];
            var subnets = (await AwsElbConnectorLiveTests.SubnetsAsync()).GroupBy(subnet => subnet.Vpc).First().GroupBy(subnet => subnet.Zone).Take(2).Select(zone => zone.First().Subnet).ToList();
            Assert.Equal(2, subnets.Count);
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string route, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, route, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MotoTestEndpoint.Host }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé lastbalans", ConnectorType = "awselb", Endpoint = MotoTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["region"] = MotoTestEndpoint.Region },
                    Credentials = new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans lastbalans" }, "create");
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

                var make = Call("make", "load_balancer_create", $"name = {name}\nsubnets = {string.Join(", ", subnets)}\ntags = {{\"team\": \"kassa\"}}");
                make.OutputVariables["Result"] = "v-lb";
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-lb", Name = "lb" } },
                    Steps =
                    {
                        make,
                        Call("read", "load_balancer", expression: "load_balancer = {{ lb[0].LoadBalancerArn }}"),
                        Call("find", "load_balancers", $"names = {name}"),
                        Call("listen", "listeners", expression: "load_balancer = {{ lb[0].LoadBalancerArn }}"),
                        Call("drop", "load_balancer_delete", expression: "load_balancer = {{ lb[0].LoadBalancerArn }}"),
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
                var arn = Outputs("make")["Result"]![0]!["LoadBalancerArn"]!.GetValue<string>();
                Assert.StartsWith("arn:aws:elasticloadbalancing:", arn, StringComparison.Ordinal);
                Assert.Equal(name, Outputs("read")["Result"]![0]!["LoadBalancerName"]!.GetValue<string>());
                Assert.Equal(arn, Outputs("find")["Result"]![0]!["LoadBalancerArn"]!.GetValue<string>());
                Assert.Empty(Outputs("listen")["Result"]!.AsArray());
                Assert.Equal(arn, Outputs("drop")["Result"]![0]!["load_balancer"]!.GetValue<string>());

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
