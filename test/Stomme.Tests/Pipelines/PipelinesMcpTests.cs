#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Mcp;
using Stomme.Tests.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Driven over the MCP endpoint of a scratch install with BOTH modules on: the pipelines
    /// contributor's resources and tools are listed for a key holder and not for a registered-only user; the read-only
    /// tools and resources answer through the module's mediator; the two mutating tools are listed as Confirm and a call
    /// is DENIED by the MCP server (no approval pipeline exists - its posture, not this module's). With Pipelines off the
    /// contributor is not composed and nothing of it is listed.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelinesMcpTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_pipelines_are_readable_over_MCP_and_the_mutating_tools_are_confirm_only()
        {
            var settings = McpCatalogDriveTests.McpEnabled();
            settings["Pipelines"] = new JsonObject { ["Enabled"] = true };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // Listed for the holder of pipelines:view / pipelines:run, with the mutating ones marked as such.
                var tools = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/list", null, output, "host tools");
                var names = tools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()).ToList() ?? new();
                Assert.Contains(PipelinesMcpContributor.ListPipelines, names);
                Assert.Contains(PipelinesMcpContributor.GetPipelineRun, names);
                Assert.Contains(PipelinesMcpContributor.RunPipeline, names);
                Assert.Contains(PipelinesMcpContributor.SendPipelineEvent, names);
                Assert.Contains(PipelinesMcpContributor.ListPipelineApprovals, names);
                Assert.Contains(PipelinesMcpContributor.GetPipelineInsights, names);
                Assert.Contains(PipelinesMcpContributor.GetPipelineStepInsights, names);
                Assert.Contains(PipelinesMcpContributor.ListUpcomingPipelineRuns, names);
                Assert.Contains(PipelinesMcpContributor.ListDataTables, names);
                Assert.Contains(PipelinesMcpContributor.ReadDataTable, names);
                Assert.Contains(PipelinesMcpContributor.ListEvaluations, names);
                Assert.Contains(PipelinesMcpContributor.GetEvaluation, names);
                Assert.Contains(PipelinesMcpContributor.StartEvaluation, names);
                Assert.Contains(PipelinesMcpContributor.RunPipelineAgain, names);
                Assert.Contains(PipelinesMcpContributor.StopPipelineRun, names);
                Assert.Contains(PipelinesMcpContributor.ListPipelineActivity, names);
                var resources =await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/list", null, output, "host resources");
                var resourceUris = resources?["result"]?["resources"]?.AsArray().Select(r => r?["uri"]?.GetValue<string>()).ToList() ?? new();
                Assert.Contains(PipelinesMcpContributor.PipelinesUri, resourceUris);
                Assert.Contains(PipelinesMcpContributor.StepsUri, resourceUris);

                // The step catalog an agent authors against: the editor's, with the step families and the sensitive flag.
                var steps = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = PipelinesMcpContributor.StepsUri }, output, "read the step catalog");
                var catalog = JsonNode.Parse(steps?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "[]")!.AsArray();
                var http = Assert.Single(catalog, step => step?["type"]?.GetValue<string>() == "Stomme.SendHttpRequest")!;
                Assert.Equal(("HTTP", "HTTP request"), (http["category"]?.GetValue<string>(), http["displayName"]?.GetValue<string>()));
                Assert.Contains(http["inputs"]!.AsArray(), input => input?["name"]?.GetValue<string>() == "Authorization" && input?["isSensitive"]?.GetValue<bool>() == true);
                Assert.DoesNotContain(catalog, step => step?["type"]?.GetValue<string>() == "Stomme.Flowchart");
                var templates = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/templates/list", null, output, "host templates");
                var templateUris = templates?["result"]?["resourceTemplates"]?.AsArray().Select(t => t?["uriTemplate"]?.GetValue<string>()).ToList() ?? new();
                Assert.Contains(PipelinesMcpContributor.PipelineTemplate, templateUris);
                Assert.Contains(PipelinesMcpContributor.RunTemplate, templateUris);

                // Author and publish over the API, run it, then read it all through MCP.
                var created = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Over MCP" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish");
                var started = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest { CorrelationId = "mcp-read" }, output, "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                var listed = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListPipelines, ["arguments"] = new JsonObject() }, output, "list_pipelines");
                Assert.NotEqual(true, listed?["result"]?["isError"]?.GetValue<bool>());
                var listedText = listed?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "[]";
                Assert.Contains(JsonNode.Parse(listedText)!.AsArray(), p => p?["definitionId"]?.GetValue<string>() == definitionId && p?["isPublished"]?.GetValue<bool>() == true);

                // The runs waiting for a decision, read-only - none here, answered as an empty list.
                var approvals = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListPipelineApprovals, ["arguments"] = new JsonObject() }, output, "list_pipeline_approvals");
                Assert.NotEqual(true, approvals?["result"]?["isError"]?.GetValue<bool>());
                Assert.Empty(JsonNode.Parse(approvals?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "null")!.AsArray());

                JsonNode? run = null;
                for (var attempt = 0; attempt < 100 && run?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>()?.Contains("\"isFinished\":true") != true; attempt++)
                {
                    run = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineRun, ["arguments"] = new JsonObject { ["runId"] = runId } }, output, "get_pipeline_run");
                    await Task.Delay(100);
                }

                var runText = run?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}";
                var runJson = JsonNode.Parse(runText)!;
                Assert.Equal(("Finished", "mcp-read"), (runJson["run"]?["status"]?.GetValue<string>(), runJson["run"]?["correlationId"]?.GetValue<string>()));
                Assert.True(runJson["log"]?.AsArray().Count > 0, "the run's log came back empty over MCP");

                // n8n's Insights over MCP - the finished run counted in the day's figures, its pipeline named; "days" optional.
                var insights = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineInsights, ["arguments"] = new JsonObject { ["days"] = 1 } }, output, "get_pipeline_insights");
                Assert.NotEqual(true, insights?["result"]?["isError"]?.GetValue<bool>());
                var insightsJson = JsonNode.Parse(insights?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!;
                Assert.Equal(1, insightsJson["days"]?.GetValue<int>());
                Assert.Contains(insightsJson["pipelines"]!.AsArray(), p => p?["pipelineName"]?.GetValue<string>() == "Over MCP" && p?["runs"]?.GetValue<int>() >= 1);
                var weekly = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineInsights, ["arguments"] = new JsonObject() }, output, "get_pipeline_insights (no days)");
                Assert.Equal(7, JsonNode.Parse(weekly?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!["days"]?.GetValue<int>());

                // Each step's figures over MCP - the run read (its pipeline is empty: no step of its own); without a
                // pipeline, refused saying so.
                var stepInsights = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineStepInsights, ["arguments"] = new JsonObject { ["definitionId"] = definitionId, ["days"] = 1 } }, output, "get_pipeline_step_insights");
                Assert.NotEqual(true, stepInsights?["result"]?["isError"]?.GetValue<bool>());
                var stepInsightsJson = JsonNode.Parse(stepInsights?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!;
                Assert.Equal((definitionId, 1, 1), (stepInsightsJson["definitionId"]?.GetValue<string>(), stepInsightsJson["days"]?.GetValue<int>(), stepInsightsJson["runs"]?.GetValue<int>()));
                var noPipeline = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineStepInsights, ["arguments"] = new JsonObject() }, output, "get_pipeline_step_insights (no pipeline)");
                Assert.Equal(true, noPipeline?["result"]?["isError"]?.GetValue<bool>());

                // What runs next over MCP - the published pipeline has no schedule; the window as asked, a day when not.
                var upcoming = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListUpcomingPipelineRuns, ["arguments"] = new JsonObject { ["hours"] = 168 } }, output, "list_upcoming_pipeline_runs");
                Assert.NotEqual(true, upcoming?["result"]?["isError"]?.GetValue<bool>());
                var upcomingJson = JsonNode.Parse(upcoming?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!;
                Assert.Equal(168, upcomingJson["hours"]?.GetValue<int>());
                Assert.Empty(upcomingJson["schedules"]!.AsArray());

                // The site's data tables over MCP, read-only - made and filled over the API (its tables/... routes beside
                // {definitionId}/...), a CSV imported, then listed and read by name.
                var table = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, "/api/Pipelines/tables",
                    new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "email" }, new PipelineTableColumn { Name = "visits", Type = PipelineTableColumnType.Number } } }, output, "create a table");
                var tableId = JsonNode.Parse(table.Body)!["id"]!.GetValue<string>();
                await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, $"/api/Pipelines/tables/{tableId}/rows",
                    new PipelineTableRowRequest { Values = { ["email"] = "ada@example.com", ["visits"] = "3" } }, output, "add a row");
                var imported = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, System.Net.Http.HttpMethod.Post, $"/api/Pipelines/tables/{tableId}/csv",
                    new PipelineTableImportRequest { Csv = "email;visits;source\nbob@example.com;1;web\n" }, output, "import a CSV");
                Assert.Equal((1, "source"), (JsonNode.Parse(imported.Body)!["imported"]!.GetValue<int>(), JsonNode.Parse(imported.Body)!["ignored"]!.AsArray().Single()!.GetValue<string>()));
                var exported = await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Pipelines/tables/{tableId}/csv", output, "export the CSV");
                var exportedCsv = JsonNode.Parse(exported.Body)!["csv"]!.GetValue<string>();
                Assert.StartsWith("id,createdAt,updatedAt,email,visits", exportedCsv);
                Assert.Contains(",ada@example.com,3", exportedCsv);
                Assert.Contains(",bob@example.com,1", exportedCsv);
                var tablesListed = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListDataTables, ["arguments"] = new JsonObject() }, output, "list_data_tables");
                Assert.Contains(JsonNode.Parse(tablesListed?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "[]")!.AsArray(), t => t?["name"]?.GetValue<string>() == "Subscribers");
                var tableRead = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ReadDataTable, ["arguments"] = new JsonObject { ["table"] = "subscribers", ["take"] = 1 } }, output, "read_data_table");
                Assert.NotEqual(true, tableRead?["result"]?["isError"]?.GetValue<bool>());
                var readJson = JsonNode.Parse(tableRead?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!;
                Assert.Equal((2, "ada@example.com"), (readJson["total"]!.GetValue<int>(), Assert.Single(readJson["rows"]!.AsArray())!["email"]!.GetValue<string>()));
                var noTable = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ReadDataTable, ["arguments"] = new JsonObject { ["table"] = "nope" } }, output, "read_data_table (none)");
                Assert.True(noTable?["result"]?["isError"]?.GetValue<bool>());

                // n8n's Evaluations over MCP: none yet, listed as none; one not on the site an error; starting one is Confirm,
                // denied here like run_pipeline, so none is started.
                var evaluations = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListEvaluations, ["arguments"] = new JsonObject { ["definitionId"] = definitionId } }, output, "list_pipeline_evaluations");
                Assert.NotEqual(true, evaluations?["result"]?["isError"]?.GetValue<bool>());
                Assert.Empty(JsonNode.Parse(evaluations?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "null")!.AsArray());
                var noEvaluation = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetEvaluation, ["arguments"] = new JsonObject { ["evaluationId"] = "nope" } }, output, "get_pipeline_evaluation (none)");
                Assert.True(noEvaluation?["result"]?["isError"]?.GetValue<bool>());
                var startDenied = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.StartEvaluation, ["arguments"] = new JsonObject { ["definitionId"] = definitionId, ["table"] = "Subscribers" } }, output, "start_pipeline_evaluation (confirm)");
                Assert.True(startDenied?["result"]?["isError"]?.GetValue<bool>());
                var stillNone = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListEvaluations, ["arguments"] = new JsonObject { ["definitionId"] = definitionId } }, output, "list_pipeline_evaluations (after the denied start)");
                Assert.Empty(JsonNode.Parse(stillNone?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "null")!.AsArray());

                var pipeline = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = $"stomme://pipelines/{definitionId}" }, output, "read pipeline");
                var pipelineJson = JsonNode.Parse(pipeline?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}")!;
                Assert.Equal(("Over MCP", true), (pipelineJson["name"]?.GetValue<string>(), pipelineJson["isPublished"]?.GetValue<bool>()));
                var runResource = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = $"stomme://pipelines/runs/{runId}" }, output, "read run");
                Assert.Contains("\"status\":\"Finished\"", runResource?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "");
                var unknownRun = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.GetPipelineRun, ["arguments"] = new JsonObject { ["runId"] = "no-such-run" } }, output, "unknown run");
                Assert.True(unknownRun?["result"]?["isError"]?.GetValue<bool>());

                // A mutating tool is Confirm, and the MCP server denies a Confirm call (no approval pipeline): no run started.
                var denied = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.RunPipeline, ["arguments"] = new JsonObject { ["definitionId"] = definitionId } }, output, "run_pipeline (confirm)");
                Assert.True(denied?["result"]?["isError"]?.GetValue<bool>());
                // Running a run again - from its failed step, or from the start - is Confirm too.
                var againDenied = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.RunPipelineAgain, ["arguments"] = new JsonObject { ["runId"] = runId, ["from"] = "start" } }, output, "run_pipeline_again (confirm)");
                Assert.True(againDenied?["result"]?["isError"]?.GetValue<bool>());
                // Stopping a run is Confirm as well.
                var stopDenied = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.StopPipelineRun, ["arguments"] = new JsonObject { ["runId"] = runId } }, output, "stop_pipeline_run (confirm)");
                Assert.True(stopDenied?["result"]?["isError"]?.GetValue<bool>());
                var runs = await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs after the denied call");
                Assert.Single(JsonNode.Parse(runs.Body)!.AsArray());

                // The pipeline activity over MCP - what was done over the API, by whom, newest first; the denied calls did
                // nothing and are not in it.
                var activity = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListPipelineActivity, ["arguments"] = new JsonObject { ["definitionId"] = definitionId } }, output, "list_pipeline_activity");
                Assert.NotEqual(true, activity?["result"]?["isError"]?.GetValue<bool>());
                var lines = JsonNode.Parse(activity?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "[]")!.AsArray();
                Assert.Equal(new[] { "started a run", "published", "created the pipeline" }, lines.Select(line => line?["action"]?.GetValue<string>()).ToArray());
                Assert.All(lines, line => Assert.Equal((GateDrive.HostUsername, "Over MCP"), (line?["userName"]?.GetValue<string>(), line?["pipelineName"]?.GetValue<string>())));
                Assert.Equal(runId, lines[0]?["runId"]?.GetValue<string>());

                // A registered-only user sees none of it.
                // Unknown and denied are one answer - a JSON-RPC error, never the catalog.
                var plainSteps = await McpRpc.PostAsync(plainClient, plainCookies, Host, McpRpc.Body("resources/read", new JsonObject { ["uri"] = PipelinesMcpContributor.StepsUri }), output, "plain step catalog");
                Assert.Contains("\"error\"", plainSteps.Body, System.StringComparison.Ordinal);
                Assert.DoesNotContain("SendHttpRequest", plainSteps.Body, System.StringComparison.Ordinal);
                var plainTools = await McpRpc.CallAsync(plainClient, plainCookies, Host, "tools/list", null, output, "plain tools");
                Assert.DoesNotContain(PipelinesMcpContributor.ListPipelines, plainTools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
                Assert.DoesNotContain(PipelinesMcpContributor.ListPipelineActivity, plainTools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
                var plainCall = await McpRpc.CallAsync(plainClient, plainCookies, Host, "tools/call", new JsonObject { ["name"] = PipelinesMcpContributor.ListPipelines, ["arguments"] = new JsonObject() }, output, "plain call");
                Assert.True(plainCall?["result"]?["isError"]?.GetValue<bool>());
            }
            finally
            {
                await app.DisposeAsync();
            }

            // With Pipelines off, the contributor is not composed: nothing of it is listed over MCP.
            using var without = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var offApp = await GateDrive.InstallAsync(without, output);
            try
            {
                using var client = GateDrive.NewClient(offApp, Host);
                var cookies = await GateDrive.LoginAsync(offApp, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var tools = await McpRpc.CallAsync(client, cookies, Host, "tools/list", null, output, "tools without pipelines");
                Assert.DoesNotContain(PipelinesMcpContributor.ListPipelines, tools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
            }
            finally
            {
                await offApp.DisposeAsync();
            }
        }
    }
}
