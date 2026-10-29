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
using Stomme.Infrastructure.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.MonicaCrm;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Monica DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway Monica 4.1 (scripts/monica-test-server.ps1) - fail-not-skip without it: a Monica connection made
    /// over the API, its token vaulted, tested; a published pipeline making a contact, a note on it and a task for it - each an
    /// expression over the first step's result - and finding the contact by name; the token in neither the graph nor the run. The
    /// contact is deleted again afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class MonicaCrmDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_a_contact_a_note_and_a_task_and_finds_the_contact()
        {
            var token = MonicaCrmTestEndpoint.Token;
            var marker = Guid.NewGuid().ToString("N")[..8];
            long? contact = null;
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(MonicaCrmTestEndpoint.Endpoint).Host }, "the Host allows Monica")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé kunder", ConnectorType = "monicacrm", Endpoint = MonicaCrmTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans kunder" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var person = Call("person", "contact_create", "v-contact");
                person.Inputs["Parameters"] = $"first_name = Ada\nlast_name = Lund{marker}\ngender = Woman";
                var note = Call("note", "note_create", "v-note");
                note.InputExpressions["Parameters"] = "contact = {{ contacts[0].id }}\nbody = Gillar kardemumma";
                var task = Call("task", "task_create", "v-task");
                task.InputExpressions["Parameters"] = "contact = {{ contacts[0].id }}\ntitle = Ring {{ contacts[0].first_name }}";
                var find = Call("find", "contacts", "v-found");
                find.Inputs["Parameters"] = $"query = Lund{marker}";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-contact", Name = "contacts" }, new PipelineGraphVariable { Id = "v-note", Name = "notes" },
                        new PipelineGraphVariable { Id = "v-task", Name = "tasks" }, new PipelineGraphVariable { Id = "v-found", Name = "found" },
                    },
                    Steps = { person, note, task, find },
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
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                contact = Outputs("person")["Result"]?[0]?["id"]?.GetValue<long>();
                Assert.True(run?.SubStatus == "Finished", steps);
                Assert.Equal(("Ada", "Woman"), (Outputs("person")["Result"]![0]!["first_name"]!.GetValue<string>(), Outputs("person")["Result"]![0]!["gender"]!.GetValue<string>()));
                Assert.Equal("Gillar kardemumma", Outputs("note")["Result"]![0]!["body"]!.GetValue<string>());
                Assert.Equal("Ring Ada", Outputs("task")["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal(contact, Assert.Single(Outputs("find")["Result"]!.AsArray())!["id"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                if (contact is { } id)
                {
                    await Contexts.DrainAsync(new MonicaCrmConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(MonicaCrmTestEndpoint.Endpoint).Host }), MonicaCrmTestEndpoint.Endpoint, "contact_delete",
                        new Dictionary<string, string> { ["contact"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, new Dictionary<string, string>(), new Dictionary<string, string> { ["api-key"] = token }, timeoutSeconds: 60));
                }
            }
        }
    }
}
