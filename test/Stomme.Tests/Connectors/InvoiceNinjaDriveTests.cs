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
using Stomme.Modules.Connectors.InvoiceNinja;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Invoice Ninja DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against this machine's throwaway Invoice Ninja 5.13 (scripts/invoiceninja-test-server.ps1) - fail-not-skip without
    /// it: an Invoice Ninja connection made over the API, its token vaulted, tested; a published pipeline making a client,
    /// an invoice for it marked sent, a payment of the invoice's whole amount - each an expression over the step before - and
    /// finding the invoice paid by its number; the token in neither the graph nor the run. Everything made is deleted again.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class InvoiceNinjaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_invoices_a_new_client_and_books_its_payment()
        {
            var token = InvoiceNinjaTestEndpoint.Token;
            var marker = Guid.NewGuid().ToString("N")[..8];
            var made = new List<(string Operation, string Key, string Id)>();
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(InvoiceNinjaTestEndpoint.Endpoint).Host }, "the Host allows Invoice Ninja")).Status);
                var connectionMade = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé fakturor", ConnectorType = "invoiceninja", Endpoint = InvoiceNinjaTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(connectionMade.Status == HttpStatusCode.OK, connectionMade.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(connectionMade.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fakturera kunden" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var customer = Call("customer", "client_create", "v-client");
                customer.Inputs["Parameters"] = $"name = Kafé Lund {marker}\ncountry = SE";
                var invoice = Call("invoice", "invoice_create", "v-invoice");
                invoice.InputExpressions["Parameters"] = $"client = {{{{ clients[0].id }}}}\nnumber = F-{marker}\nmark_sent = true";
                invoice.Inputs["Query"] = "[{\"notes\": \"Bryggkaffe\", \"cost\": 120, \"quantity\": 3}]";
                var payment = Call("payment", "payment_create", "v-payment");
                payment.InputExpressions["Parameters"] = "invoice = {{ invoices[0].id }}\namount = {{ invoices[0].amount }}\ntype = Bank Transfer";
                var find = Call("find", "invoices", "v-found");
                find.Inputs["Parameters"] = $"number = F-{marker}\nclient_status = paid";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-client", Name = "clients" }, new PipelineGraphVariable { Id = "v-invoice", Name = "invoices" },
                        new PipelineGraphVariable { Id = "v-payment", Name = "payments" }, new PipelineGraphVariable { Id = "v-found", Name = "found" },
                    },
                    Steps = { customer, invoice, payment, find },
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
                foreach (var (id, operation, key) in new[] { ("customer", "client_delete", "client"), ("invoice", "invoice_delete", "invoice"), ("payment", "payment_delete", "payment") })
                {
                    if (Outputs(id)?["Result"]?[0]?["id"]?.GetValue<string>() is { } madeId)
                    {
                        made.Add((operation, key, madeId));
                    }
                }

                Assert.True(run?.SubStatus == "Finished", steps);
                Assert.Equal(("Kafé Lund " + marker, "752"), (Outputs("customer")!["Result"]![0]!["name"]!.GetValue<string>(), Outputs("customer")!["Result"]![0]!["country_id"]!.GetValue<string>()));
                Assert.Equal(360, Outputs("payment")!["Result"]![0]!["amount"]!.GetValue<double>());
                var found = Assert.Single(Outputs("find")!["Result"]!.AsArray())!;
                Assert.Equal((Outputs("invoice")!["Result"]![0]!["id"]!.GetValue<string>(), 0d), (found["id"]!.GetValue<string>(), found["balance"]!.GetValue<double>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                foreach (var (operation, key, id) in Enumerable.Reverse(made))
                {
                    await Contexts.DrainAsync(new InvoiceNinjaConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(InvoiceNinjaTestEndpoint.Endpoint).Host }), InvoiceNinjaTestEndpoint.Endpoint, operation,
                        new Dictionary<string, string> { [key] = id }, new Dictionary<string, string>(), new Dictionary<string, string> { ["api-key"] = token }, timeoutSeconds: 60));
                }
            }
        }
    }
}
