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
using Stomme.Modules.Connectors.ErpNext;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - ERPNext DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against this machine's throwaway ERPNext 16 (scripts/erpnext-test-server.ps1) - fail-not-skip without it (D46): an ERPNext
    /// connection made over the API, its key and secret vaulted, tested; a published pipeline making a contact with its e-mail
    /// row, a ToDo naming it - an expression over the first step's result - and finding the ToDo by a filter; the secret in neither
    /// the graph nor the run. Both documents are deleted again.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ErpNextDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_a_contact_and_a_todo_for_it_and_finds_the_todo()
        {
            var (key, secret) = (ErpNextTestEndpoint.Key, ErpNextTestEndpoint.Secret);
            var marker = Guid.NewGuid().ToString("N")[..8];
            var made = new List<(string DocType, string Name)>();
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(ErpNextTestEndpoint.Endpoint).Host }, "the Host allows ERPNext")).Status);
                var connectionMade = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé ERP", ConnectorType = "erpnext", Endpoint = ErpNextTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["access-key"] = key, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(connectionMade.Status == HttpStatusCode.OK, connectionMade.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(connectionMade.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Ring kunden" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var person = Call("person", "document_create", "v-contact");
                person.Inputs["Parameters"] = "doctype = Contact";
                person.Inputs["Query"] = $"{{\"first_name\": \"Ada {marker}\", \"email_ids\": [{{\"email_id\": \"ada.{marker}@kafe.example\", \"is_primary\": 1}}]}}";
                var todo = Call("todo", "document_create", "v-todo");
                todo.InputExpressions["Parameters"] = $"doctype = ToDo\nfields = {{\"description\": \"Ring {{{{ contacts[0].name }}}} {marker}\", \"priority\": \"High\"}}";
                var find = Call("find", "documents", "v-found");
                find.Inputs["Parameters"] = $"doctype = ToDo\nfields = name, description, priority\nfilters = [[\"description\", \"like\", \"%{marker}%\"]]";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-contact", Name = "contacts" }, new PipelineGraphVariable { Id = "v-todo", Name = "todos" },
                        new PipelineGraphVariable { Id = "v-found", Name = "found" },
                    },
                    Steps = { person, todo, find },
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
                JsonNode? Outputs(string id) => JsonNode.Parse(steps)!.AsArray().SingleOrDefault(step => step!["stepId"]!.GetValue<string>() == id) is { } step ? JsonNode.Parse(step["outputsJson"]!.GetValue<string>()) : null;
                foreach (var (id, doctype) in new[] { ("person", "Contact"), ("todo", "ToDo") })
                {
                    if (Outputs(id)?["Result"]?[0]?["name"]?.GetValue<string>() is { } name)
                    {
                        made.Add((doctype, name));
                    }
                }

                Assert.True(run?.SubStatus == "Finished", steps);
                var contactName = Outputs("person")!["Result"]![0]!["name"]!.GetValue<string>();
                Assert.Contains($"ada.{marker}@kafe.example", Outputs("person")!["Result"]![0]!["email_ids"]!.GetValue<string>(), StringComparison.Ordinal);
                var found = Assert.Single(Outputs("find")!["Result"]!.AsArray())!;
                Assert.Equal((Outputs("todo")!["Result"]![0]!["name"]!.GetValue<string>(), "High"), (found["name"]!.GetValue<string>(), found["priority"]!.GetValue<string>()));
                Assert.Contains("Ring " + contactName, found["description"]!.GetValue<string>(), StringComparison.Ordinal);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                foreach (var (doctype, name) in Enumerable.Reverse(made))
                {
                    await Contexts.DrainAsync(new ErpNextConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(ErpNextTestEndpoint.Endpoint).Host }), ErpNextTestEndpoint.Endpoint, "document_delete",
                        new Dictionary<string, string> { ["doctype"] = doctype, ["name"] = name }, new Dictionary<string, string>(), new Dictionary<string, string> { ["access-key"] = key, ["secret-key"] = secret }, timeoutSeconds: 60));
                }
            }
        }
    }
}
