#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Pipelines;

namespace Stomme.Modules.Pipelines.Mcp
{
    /// <summary>
    /// The pipelines' MCP surface (BL-95 increment (f), over BL-80's contributor seam): four resources (the site's
    /// pipelines, one pipeline, one run with its log, and - a residue of (f), 2026-09-28 - the step catalog an agent
    /// authors against) and eight tools - the fifth lists the runs waiting for a person's decision, read-only: the
    /// decision is a person's, on the run's page; the sixth says how the pipelines have been running (n8n's Insights); the
    /// seventh and eighth list the site's data tables and read a table's rows, read-only (n8n's Data tables). Every
    /// read and every call goes through the
    /// module's mediator messages, so the D99 keys decide a second time behind the MCP gates; the two tools that
    /// change something (`run_pipeline`, `send_pipeline_event`) carry the mutating prefixes and are `Confirm` - which
    /// the MCP server DENIES until an approval pipeline exists (its own posture, logged as such), so an agent can list,
    /// read and observe today and cannot start a run. the predecessor platform's `execute_pipeline` (ran through its concurrency policy)
    /// is not carried: the engine's activation strategy is the policy here. Since 10.30.0, `list_pipeline_activity` reads
    /// the site's pipeline activity - who did what, and when - under <c>pipelines:manage</c>, as the list's Activity does.
    /// </summary>
    public sealed class PipelinesMcpContributor : IMcpContributor, IMcpResourceReader
    {
        public const string PipelinesUri = "stomme://pipelines";
        public const string PipelineTemplate = "stomme://pipelines/{definitionId}";
        public const string RunTemplate = "stomme://pipelines/runs/{runId}";

        // NOT "stomme://pipelines/steps": that address matches PipelineTemplate with a definition id of "steps".
        public const string StepsUri = "stomme://pipeline-steps";
        public const string ListPipelines = "list_pipelines";
        public const string GetPipelineRun = "get_pipeline_run";
        public const string RunPipeline = "run_pipeline";
        public const string SendPipelineEvent = "send_pipeline_event";
        public const string ListPipelineApprovals = "list_pipeline_approvals";
        public const string GetPipelineInsights = "get_pipeline_insights";
        public const string GetPipelineStepInsights = "get_pipeline_step_insights";
        public const string ListUpcomingPipelineRuns = "list_upcoming_pipeline_runs";
        public const string ListDataTables = "list_data_tables";
        public const string ReadDataTable = "read_data_table";
        public const string ListEvaluations = "list_pipeline_evaluations";
        public const string GetEvaluation = "get_pipeline_evaluation";
        public const string StartEvaluation = "start_pipeline_evaluation";
        public const string RunPipelineAgain = "run_pipeline_again";
        public const string StopPipelineRun = "stop_pipeline_run";
        public const string ListPipelineActivity = "list_pipeline_activity";
        public const int LogLinesInRun = 200;

        private static readonly IReadOnlyList<McpAccessGate> ViewGate = new[] { McpAccessGate.ForPermission(PipelinesPermissions.View) };
        // The activity ledger is an author's to read, over MCP as on the list.
        private static readonly IReadOnlyList<McpAccessGate> ManageGate = new[] { McpAccessGate.ForPermission(PipelinesPermissions.Manage) };
        private static readonly IReadOnlyList<McpAccessGate> RunGate = new[] { McpAccessGate.ForPermission(PipelinesPermissions.Run) };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IMediator _mediator;

        public PipelinesMcpContributor(IMediator mediator)
        {
            _mediator = mediator;
        }

        public string ContributorId => "stomme.pipelines";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(PipelinesUri, "Pipelines", "The site's pipelines: the latest version of each, with whether it is published.", "application/json", ViewGate),
            new McpResourceDescriptor(PipelineTemplate, "Pipeline", "One pipeline by definition id: name, version, state and its definition body.", "application/json", ViewGate),
            new McpResourceDescriptor(RunTemplate, "Pipeline run", "One run by id: status, timings, incidents and the last lines of its execution log.", "application/json", ViewGate),
            new McpResourceDescriptor(StepsUri, "Pipeline steps", "The step types a pipeline can place - the editor's catalog: type, version, category, description, outcomes and inputs (with their types and which are sensitive).", "application/json", ViewGate),
        };

        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
        {
            new McpToolDescriptor(ListPipelines, "List the site's pipelines (latest version of each, published or draft).",
                """{"type":"object","properties":{}}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(GetPipelineRun, "Read one pipeline run: its status and the last lines of its execution log.",
                """{"type":"object","properties":{"runId":{"type":"string","description":"The run's id."}},"required":["runId"]}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(RunPipeline, "Start a run of a published pipeline; the run is dispatched and observed, never awaited.",
                """{"type":"object","properties":{"definitionId":{"type":"string","description":"The pipeline's definition id."},"correlationId":{"type":"string"},"input":{"type":"object","additionalProperties":{"type":"string"},"description":"Named string inputs for the run."}},"required":["definitionId"]}""", RunGate, McpToolApproval.Confirm),
            new McpToolDescriptor(ListPipelineApprovals, "List the runs waiting for a person's decision (an 'Ask for approval' step): what is asked, by which pipeline and step, since when and by when. A person decides on the run's page.",
                """{"type":"object","properties":{"runId":{"type":"string","description":"Only this run's; leave it out for the site's."}}}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(GetPipelineInsights, "How the site's pipelines have been running over the last 1, 7 or 30 days: runs, failed, cancelled and unfinished, the failure rate and the median run time - in all and per pipeline, with each one's last failed run.",
                """{"type":"object","properties":{"days":{"type":"integer","enum":[1,7,30],"description":"The window in days; 7 when left out."}}}""", ViewGate, McpToolApproval.Auto),
            // Beyond n8n's Insights - which step of a pipeline fails, or takes long.
            new McpToolDescriptor(GetPipelineStepInsights, "How each step of one pipeline has been doing over the last 1, 7 or 30 days: per step (its id and name), how many times it ran, how many of those failed, and its median time - which step fails, which takes long. Reads the newest 500 runs.",
                """{"type":"object","properties":{"definitionId":{"type":"string","description":"The pipeline's definition id."},"days":{"type":"integer","enum":[1,7,30],"description":"The window in days; 7 when left out."}},"required":["definitionId"]}""", ViewGate, McpToolApproval.Auto),
            // What runs next - the published pipelines' schedules, the soonest first.
            new McpToolDescriptor(ListUpcomingPipelineRuns, "What runs next: every published pipeline's schedules with their next runs (UTC) in the next 24 or 168 hours, the soonest first - its cron and time zone, how many runs in the window - and whether the site runs schedules at all.",
                """{"type":"object","properties":{"hours":{"type":"integer","enum":[24,168],"description":"The window in hours; 24 when left out."}}}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(ListDataTables, "List the site's data tables - the rows its pipelines keep between runs: each table's id, name and columns (name and type).",
                """{"type":"object","properties":{}}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(ReadDataTable, "Read a data table's rows, oldest first, at most 100 at a time: each row's id, createdAt, updatedAt and its columns' values.",
                """{"type":"object","properties":{"table":{"type":"string","description":"The table's id or name."},"skip":{"type":"integer","description":"Rows to pass over; 0 when left out."},"take":{"type":"integer","description":"At most so many rows, up to 100; 100 when left out."}},"required":["table"]}""", ViewGate, McpToolApproval.Auto),
            // n8n's Evaluations: a pipeline's runs over a data table's rows, scored - listed, read and started.
            new McpToolDescriptor(ListEvaluations, "List a pipeline's evaluations, newest first - runs of its published version over a data table's rows as test cases: each one's state, rows done and failed, and each metric's average.",
                """{"type":"object","properties":{"definitionId":{"type":"string","description":"The pipeline's definition id."}},"required":["definitionId"]}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(GetEvaluation, "Read one evaluation with every row: the row's values, its run, its state, and the metrics and outputs its Evaluation steps recorded.",
                """{"type":"object","properties":{"evaluationId":{"type":"string","description":"The evaluation's id."}},"required":["evaluationId"]}""", ViewGate, McpToolApproval.Auto),
            new McpToolDescriptor(StartEvaluation, "Start an evaluation of a published pipeline over a data table's rows (at most 100): each row runs the pipeline from its 'When evaluating' step. Observed with get_pipeline_evaluation, never awaited.",
                """{"type":"object","properties":{"definitionId":{"type":"string","description":"The pipeline's definition id."},"table":{"type":"string","description":"The data table's id or name."},"maxRows":{"type":"integer","description":"At most so many rows from the top; 100 when left out."}},"required":["definitionId","table"]}""", RunGate, McpToolApproval.Confirm),
            // n8n's retry: a run again on the published version - a failed one from the step that failed, or any from the start.
            new McpToolDescriptor(RunPipelineAgain, "Run a pipeline's run again on its published version: a failed run from the step that failed ('failed-step' - the steps that had finished are not run again, what they saved is kept), or any run from the start with what it was started with ('start') - from the start also on the version the run ran ('version': 'its-own', n8n's retry with the original workflow). Observed with get_pipeline_run, never awaited.",
                """{"type":"object","properties":{"runId":{"type":"string","description":"The run's id."},"from":{"type":"string","enum":["failed-step","start"],"description":"Where the new run begins."},"version":{"type":"string","enum":["published","its-own"],"description":"From the start: the pipeline's published version (the default) or the version the run ran."}},"required":["runId","from"]}""", RunGate, McpToolApproval.Confirm),
            // n8n's Stop execution: what an assistant started, it can stop - with what that run started.
            new McpToolDescriptor(StopPipelineRun, "Stop a run where it is - a step that is waiting is cancelled, one at work finishes first, nothing after it runs - and every run it started with it. Observed with get_pipeline_run.",
                """{"type":"object","properties":{"runId":{"type":"string","description":"The run's id."}},"required":["runId"]}""", RunGate, McpToolApproval.Confirm),
            new McpToolDescriptor(SendPipelineEvent, "Raise a named event for the site's pipelines: starts every pipeline whose Event trigger names it and resumes every run waiting on it.",
                """{"type":"object","properties":{"name":{"type":"string"},"payload":{"type":"string"},"correlationId":{"type":"string"}},"required":["name"]}""", RunGate, McpToolApproval.Confirm),
            // The site's pipeline activity (n8n Enterprise's audit log), read-only.
            new McpToolDescriptor(ListPipelineActivity, "Who did what to the site's pipelines, and when - newest first: created, saved, published (with what changed), archived, deleted, a run started or stopped, a folder moved, the site's variables changed (by name, never a value). All of it, or one pipeline's.",
                """{"type":"object","properties":{"definitionId":{"type":"string","description":"Only this pipeline's; leave it out for the site's."},"take":{"type":"integer","description":"At most so many lines, up to 500; 100 when left out."}}}""", ManageGate, McpToolApproval.Auto),
        };

        public IMcpResourceReader? ReaderFor(string uriTemplate) =>
            uriTemplate is PipelinesUri or PipelineTemplate or RunTemplate or StepsUri ? this : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => toolName switch
        {
            ListPipelines => new Invoker(async (_, ct) => Ok(await _mediator.Send(new ListPipelinesQuery(), ct))),
            GetPipelineRun => new Invoker(async (arguments, ct) =>
            {
                var runId = Argument(arguments, "runId");
                if (string.IsNullOrWhiteSpace(runId))
                {
                    return Error("The arguments need a 'runId'.");
                }

                var run = await _mediator.Send(new GetRunQuery(runId), ct);
                return run is null ? Error($"No run '{runId}' on this site.") : Ok(new { run, log = await LogAsync(runId, ct) });
            }),
            RunPipeline => new Invoker(async (arguments, ct) =>
            {
                var definitionId = Argument(arguments, "definitionId");
                if (string.IsNullOrWhiteSpace(definitionId))
                {
                    return Error("The arguments need a 'definitionId'.");
                }

                IDictionary<string, string>? input = null;
                if (arguments is not null && arguments.TryGetValue("input", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
                {
                    input = inputs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
                }

                var started = await _mediator.Send(new StartPipelineCommand(definitionId, Argument(arguments, "correlationId"), input), ct);
                return started is null ? Error($"'{definitionId}' is not a published pipeline on this site.") : Ok(started);
            }),
            ListPipelineApprovals => new Invoker(async (arguments, ct) => Ok(await _mediator.Send(new ListApprovalsQuery(Argument(arguments, "runId")), ct))),
            GetPipelineInsights => new Invoker(async (arguments, ct) => Ok(await _mediator.Send(new GetInsightsQuery(
                arguments is not null && arguments.TryGetValue("days", out var days) && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var value) ? value : 7), ct))),
            GetPipelineStepInsights => new Invoker(async (arguments, ct) => Argument(arguments, "definitionId") is { Length: > 0 } definitionId
                ? Ok(await _mediator.Send(new GetStepInsightsQuery(definitionId,
                    arguments is not null && arguments.TryGetValue("days", out var days) && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var value) ? value : 7), ct))
                : Error("definitionId is required.")),
            ListUpcomingPipelineRuns => new Invoker(async (arguments, ct) => Ok(await _mediator.Send(new ListUpcomingRunsQuery(
                arguments is not null && arguments.TryGetValue("hours", out var hours) && hours.ValueKind == JsonValueKind.Number && hours.TryGetInt32(out var value) ? value : 24), ct))),
            ListDataTables => new Invoker(async (_, ct) => Ok(await _mediator.Send(new ListSiteTablesQuery(), ct))),
            ReadDataTable => new Invoker(async (arguments, ct) =>
            {
                var wanted = Argument(arguments, "table")?.Trim();
                if (string.IsNullOrEmpty(wanted))
                {
                    return Error("The arguments need a 'table': its id or name.");
                }

                var tables = await _mediator.Send(new ListSiteTablesQuery(), ct);
                var table = tables.FirstOrDefault(t => t.Id == wanted) ?? tables.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (table is null)
                {
                    return Error($"No data table '{wanted}' on this site.");
                }

                var skip = arguments is not null && arguments.TryGetValue("skip", out var given) && given.ValueKind == JsonValueKind.Number && given.TryGetInt32(out var s) ? Math.Max(0, s) : 0;
                var take = arguments is not null && arguments.TryGetValue("take", out var wantedTake) && wantedTake.ValueKind == JsonValueKind.Number && wantedTake.TryGetInt32(out var t2) ? Math.Clamp(t2, 1, 100) : 100;
                var rows = await _mediator.Send(new GetTableRowsQuery(table.Id, skip, take), ct);
                return rows is null ? Error($"No data table '{wanted}' on this site.") : Ok(rows);
            }),
            ListEvaluations => new Invoker(async (arguments, ct) =>
            {
                var definitionId = Argument(arguments, "definitionId");
                return string.IsNullOrWhiteSpace(definitionId)
                    ? Error("The arguments need a 'definitionId'.")
                    : Ok(await _mediator.Send(new ListEvaluationsQuery(definitionId), ct));
            }),
            GetEvaluation => new Invoker(async (arguments, ct) =>
            {
                var evaluationId = Argument(arguments, "evaluationId");
                if (string.IsNullOrWhiteSpace(evaluationId))
                {
                    return Error("The arguments need an 'evaluationId'.");
                }

                var evaluation = await _mediator.Send(new GetEvaluationQuery(evaluationId), ct);
                return evaluation is null ? Error($"No evaluation '{evaluationId}' on this site.") : Ok(evaluation);
            }),
            StartEvaluation => new Invoker(async (arguments, ct) =>
            {
                var definitionId = Argument(arguments, "definitionId");
                var table = Argument(arguments, "table");
                if (string.IsNullOrWhiteSpace(definitionId) || string.IsNullOrWhiteSpace(table))
                {
                    return Error("The arguments need a 'definitionId' and a 'table'.");
                }

                int? maxRows = arguments is not null && arguments.TryGetValue("maxRows", out var rows) && rows.ValueKind == JsonValueKind.Number && rows.TryGetInt32(out var count) ? count : null;
                var started = await _mediator.Send(new StartEvaluationCommand(definitionId, new PipelineEvaluationRequest { TableId = table, MaxRows = maxRows }), ct);
                return started is null ? Error($"No pipeline '{definitionId}' on this site.") : Ok(started);
            }),
            RunPipelineAgain => new Invoker(async (arguments, ct) =>
            {
                var runId = Argument(arguments, "runId");
                var from = Argument(arguments, "from");
                var version = Argument(arguments, "version");
                if (string.IsNullOrWhiteSpace(runId) || from is not ("failed-step" or "start") || version is not (null or "" or "published" or "its-own")
                    || (from == "failed-step" && version == "its-own"))
                {
                    return Error("The arguments need a 'runId', and 'from' as 'failed-step' or 'start'; 'version', from the start only, as 'published' or 'its-own'.");
                }

                var started = from == "failed-step"
                    ? await _mediator.Send(new RetryFromFailedStepCommand(runId), ct)
                    : await _mediator.Send(new RetryRunCommand(runId, version == "its-own"), ct);
                return started is null ? Error($"No run '{runId}' on this site.") : Ok(started);
            }),
            StopPipelineRun => new Invoker(async (arguments, ct) =>
            {
                var runId = Argument(arguments, "runId");
                if (string.IsNullOrWhiteSpace(runId))
                {
                    return Error("The arguments need a 'runId'.");
                }

                if (!await _mediator.Send(new CancelRunCommand(runId), ct))
                {
                    return Error($"No run '{runId}' on this site to stop.");
                }

                // The run as stored now - none yet for one still in its first burst of work, which is stored once it stops.
                return Ok(new { stopped = true, runId, run = await _mediator.Send(new GetRunQuery(runId), ct) });
            }),
            SendPipelineEvent => new Invoker(async (arguments, ct) =>
            {
                var name = Argument(arguments, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Error("The arguments need a 'name'.");
                }

                var raised = await _mediator.Send(new RaisePipelineEventCommand(name, Argument(arguments, "payload"), Argument(arguments, "correlationId")), ct);
                return raised ? Ok(new { name, raised = true }) : Error("The event could not be raised: no site is resolved.");
            }),
            ListPipelineActivity => new Invoker(async (arguments, ct) => Ok(await _mediator.Send(new Stomme.Modules.Pipelines.Audit.ListPipelineAuditQuery(
                Argument(arguments, "definitionId"),
                arguments is not null && arguments.TryGetValue("take", out var take) && take.ValueKind == JsonValueKind.Number && take.TryGetInt32(out var value) ? value : PipelineAuditLimits.DefaultTake), ct))),
            _ => null,
        };

        public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            if (string.Equals(uri, PipelinesUri, StringComparison.Ordinal))
            {
                return Content(uri, await _mediator.Send(new ListPipelinesQuery(), ct));
            }

            if (string.Equals(uri, StepsUri, StringComparison.Ordinal))
            {
                return Content(uri, await _mediator.Send(new ListStepsQuery(), ct)); // the editor's own catalog, under pipelines:view
            }

            if (arguments.TryGetValue("runId", out var runId) && !string.IsNullOrWhiteSpace(runId))
            {
                var run = await _mediator.Send(new GetRunQuery(runId), ct);
                return run is null ? null : Content(uri, new { run, log = await LogAsync(runId, ct) });
            }

            if (arguments.TryGetValue("definitionId", out var definitionId) && !string.IsNullOrWhiteSpace(definitionId))
            {
                var pipeline = await _mediator.Send(new GetPipelineQuery(definitionId), ct);
                return pipeline is null ? null : Content(uri, pipeline);
            }

            return null;
        }

        private async Task<IReadOnlyList<PipelineRunLogLine>> LogAsync(string runId, CancellationToken ct)
        {
            var log = await _mediator.Send(new GetRunLogQuery(runId), ct);
            return log.Count <= LogLinesInRun ? log : log.Skip(log.Count - LogLinesInRun).ToList();
        }

        private static string? Argument(IReadOnlyDictionary<string, JsonElement>? arguments, string name) =>
            arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static McpResourceContent Content(string uri, object payload) => new(uri, "application/json", JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Ok(object payload) => new(JsonSerializer.Serialize(payload, Json));

        private static McpToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Json), isError: true);

        private sealed class Invoker(Func<IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<McpToolResult>> invoke) : IMcpToolInvoker
        {
            public async Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
            {
                IReadOnlyDictionary<string, JsonElement>? arguments = null;
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    try
                    {
                        arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
                    }
                    catch (JsonException)
                    {
                        return Error("The arguments must be a JSON object.");
                    }
                }

                try
                {
                    return await invoke(arguments, ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Error(ex.Message);
                }
            }
        }
    }
}
