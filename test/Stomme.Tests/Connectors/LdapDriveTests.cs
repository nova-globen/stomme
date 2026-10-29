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
    /// LDAP DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real OpenLDAP (<see cref="LdapTestEndpoint"/>): an LDAP connection made over the API - the directory's address,
    /// its base DN a setting, the bind DN's password vaulted - refused while loopback is not allowlisted, then tested; a published
    /// pipeline makes a unit and a person in it, finds her by her mail, changes her title, compares it, moves her into a unit of
    /// former staff and deletes her and the units; the password in neither the graph nor the run. Fails, never skips, without a
    /// directory.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class LdapDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_a_person_in_the_directory_and_moves_her()
        {
            var password = LdapTestEndpoint.Password; // no directory: a failure that says how to get one
            var unit = $"ou=drive-{Guid.NewGuid().ToString("N")[..8]},{LdapTestEndpoint.Root}";
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

                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé katalog", ConnectorType = "ldap", Endpoint = $"ldap://{LdapTestEndpoint.Host}:{LdapTestEndpoint.Port}", TimeoutSeconds = 30,
                    Settings = new() { ["base_dn"] = unit },
                    Credentials = new() { ["username"] = LdapTestEndpoint.Admin, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = LdapTestEndpoint.Host }, "the Host allows the directory")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé staff" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };

                var ada = $"cn=Ada Kafé,{unit}";
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("unit", "create", $"dn = {unit}\nattributes = {{\"objectClass\": \"organizationalUnit\"}}"),
                        Call("alumni", "create", $"dn = ou=alumni,{unit}\nattributes = {{\"objectClass\": \"organizationalUnit\"}}"),
                        Call("ada", "create", $"dn = {ada}\nattributes = {{\"objectClass\": [\"inetOrgPerson\"], \"sn\": \"Kafé\", \"mail\": \"ada@kafe.example\"}}"),
                        Call("find", "search", "filter = (mail=ada@kafe.example)\nattributes = cn, mail"),
                        Call("title", "update", $"dn = {ada}\nreplace = {{\"title\": \"Barista\"}}"),
                        Call("barista", "compare", $"dn = {ada}\nattribute = title\nvalue = Barista"),
                        Call("move", "rename", $"dn = {ada}\nto = cn=Ada Kafé,ou=alumni,{unit}"),
                        Call("drop", "delete", $"dn = cn=Ada Kafé,ou=alumni,{unit}"),
                        Call("drop-alumni", "delete", $"dn = ou=alumni,{unit}"),
                        Call("drop-unit", "delete", $"dn = {unit}"),
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
                var found = Outputs("find")["Result"]!.AsArray().Single()!; // searched under the connection's base DN
                Assert.Equal((ada, "Ada Kafé", "ada@kafe.example"), (found["dn"]!.GetValue<string>(), found["cn"]!.GetValue<string>(), found["mail"]!.GetValue<string>()));
                Assert.Equal(1, Outputs("title")["Result"]![0]!["replaced"]!.GetValue<int>());
                Assert.True(Outputs("barista")["Result"]![0]!["matches"]!.GetValue<bool>());
                Assert.True(Outputs("move")["Result"]![0]!["moved"]!.GetValue<bool>());
                Assert.True(Outputs("drop-unit")["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                LdapTestEndpoint.DeleteTree(unit);
            }
        }
    }
}
