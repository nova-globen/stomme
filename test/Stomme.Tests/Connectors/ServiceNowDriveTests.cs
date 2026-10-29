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
    /// ServiceNow DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for a ServiceNow instance that answers an integration user's name and password alone.
    /// A ServiceNow connection made over the API, its password vaulted, tested; a published pipeline creates an incident by its
    /// own fields and a column of the instance's own, resolves it, gets it with its display values, reads the open incidents
    /// over two pages no further than asked, creates a record in a table of the instance's own and deletes it; the password in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ServiceNowDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string IncidentId = "9d385017c611228701d22104cc95c371";
        private const string OrderId = "46d44a5ba9fe1981014a2bd2ccb7e1e1";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_an_incident_and_a_record_with_an_integration_users_password()
        {
            var password = "sn-" + Guid.NewGuid().ToString("N");
            using var instance = new ServiceNowStandIn("kafe.integration", password);
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
                    Name = "Kafé service desk", ConnectorType = "servicenow", Endpoint = $"http://127.0.0.1:{instance.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "kafe.integration", ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé incidents" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("report", "Report", "create_incident", "short_description = Kassan är nere\nurgency = 2\ncaller_id = Ada Lovelace\nvalues = {\"u_site\": \"Kafé Söder\"}"),
                        Call("resolve", "Resolve", "update_incident", $"id = {IncidentId}\nstate = 6\nclose_code = Solved (Permanently)\nclose_notes = Omstartad"),
                        Call("incident", "Incident", "record", $"table = incident\nid = {IncidentId}\nfields = number,state,caller_id\ndisplay_value = all"),
                        Call("open", "Open", "table", "table = incident\nquery = active=true\nmax = 150\nlimit = 100"),
                        Call("order", "Order", "create_record", "table = u_kafe_order\nvalues = {\"u_name\": \"Fika & bröd\", \"u_count\": 3}"),
                        Call("drop", "Drop", "delete_record", $"table = u_kafe_order\nid = {OrderId}"),
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
                var received = instance.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/now/table/sys_user?sysparm_limit=1&sysparm_fields=sys_id",
                    "POST /api/now/table/incident?sysparm_exclude_reference_link=true&sysparm_display_value=false {\"u_site\":\"Kafé Söder\",\"short_description\":\"Kassan är nere\",\"caller_id\":\"Ada Lovelace\",\"urgency\":\"2\"}",
                    $"PATCH /api/now/table/incident/{IncidentId}?sysparm_exclude_reference_link=true&sysparm_display_value=false {{\"state\":\"6\",\"close_code\":\"Solved (Permanently)\",\"close_notes\":\"Omstartad\"}}",
                    $"GET /api/now/table/incident/{IncidentId}?sysparm_exclude_reference_link=true&sysparm_display_value=all&sysparm_fields=number%2Cstate%2Ccaller_id",
                    "GET /api/now/table/incident?sysparm_exclude_reference_link=true&sysparm_display_value=false&sysparm_limit=100&sysparm_offset=0&sysparm_query=active%3Dtrue",
                    "GET /api/now/table/incident?sysparm_exclude_reference_link=true&sysparm_display_value=false&sysparm_limit=100&sysparm_offset=100&sysparm_query=active%3Dtrue",
                    "POST /api/now/table/u_kafe_order?sysparm_exclude_reference_link=true&sysparm_display_value=false {\"u_name\":\"Fika & bröd\",\"u_count\":3}",
                    $"DELETE /api/now/table/u_kafe_order/{OrderId}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("INC0010002", JsonNode.Parse(Outputs("report"))!["Result"]![0]!["number"]!.GetValue<string>());
                Assert.Equal("Ada Lovelace", JsonNode.Parse(Outputs("incident"))!["Result"]![0]!["caller_id_value"]!.GetValue<string>());
                Assert.Equal(150, JsonNode.Parse(Outputs("open"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a ServiceNow instance: its Table API for an integration user's name and password alone.</summary>
        private sealed class ServiceNowStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _authorization;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ServiceNowStandIn(string username, string password)
            {
                _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Authorized)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

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

                    var raw = context.Request.RawUrl ?? "";
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var authorized = context.Request.Headers["Authorization"] == _authorization;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Incidents(int first, int count) => "{\"result\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"sys_id\":\"" + i.ToString("x32") + "\",\"number\":\"INC" + (10000 + i).ToString("D7") + "\",\"active\":\"true\"}")) + "]}";
                    const string Incident = "{\"result\":{\"sys_id\":\"" + IncidentId + "\",\"number\":\"INC0010002\",\"state\":\"1\",\"caller_id\":\"62826bf03710200044e0bfc8bcbe5df1\"}}";
                    var (status, answer) = !authorized ? (401, "{\"error\":{\"message\":\"User Not Authenticated\",\"detail\":\"Required to provide Auth information\"},\"status\":\"failure\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/now/table/sys_user") => (200, "{\"result\":[{\"sys_id\":\"62826bf03710200044e0bfc8bcbe5df1\"}]}"),
                            ("POST", "/api/now/table/incident") => (201, Incident),
                            ("PATCH", "/api/now/table/incident/" + IncidentId) => (200, Incident.Replace("\"state\":\"1\"", "\"state\":\"6\"")),
                            ("GET", "/api/now/table/incident/" + IncidentId) => (200, "{\"result\":{\"number\":{\"display_value\":\"INC0010002\",\"value\":\"INC0010002\"},\"state\":{\"display_value\":\"Resolved\",\"value\":\"6\"},\"caller_id\":{\"display_value\":\"Ada Lovelace\",\"value\":\"62826bf03710200044e0bfc8bcbe5df1\"}}}"),
                            ("GET", "/api/now/table/incident") => (200, raw.Contains("sysparm_offset=0&", StringComparison.Ordinal) ? Incidents(1, 100) : Incidents(101, 100)),
                            ("POST", "/api/now/table/u_kafe_order") => (201, "{\"result\":{\"sys_id\":\"" + OrderId + "\",\"u_name\":\"Fika & bröd\",\"u_count\":\"3\"}}"),
                            ("DELETE", "/api/now/table/u_kafe_order/" + OrderId) => (204, ""),
                            _ => (404, "{\"error\":{\"message\":\"No Record found\",\"detail\":\"Record doesn't exist or ACL restricts the record retrieval\"},\"status\":\"failure\"}"),
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
