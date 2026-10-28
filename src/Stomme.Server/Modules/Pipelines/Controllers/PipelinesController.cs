#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Comments;
using Stomme.Modules.Pipelines.Startup;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Pipelines;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Controllers
{
    /// <summary>
    /// The Pipelines module's API (BL-95 increment (b)). Router-gated on <c>Pipelines:Enabled</c> - with the switch off
    /// the routes do not exist (404), because a permission policy runs before any action body and would answer 403 to a
    /// caller without the key (the DataExport lesson). Every action carries its permission key; the request's site is
    /// the engine tenant the middleware entered, never a parameter.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    [PipelinesEnabled]
    public sealed class PipelinesController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public PipelinesController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        [HttpGet]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineView>> List([FromQuery] bool lastRuns, CancellationToken ct) =>
            await _mediator.Send(new ListPipelinesQuery(lastRuns), ct);

        [HttpGet("steps")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<StepCatalogEntry>> Steps(CancellationToken ct) =>
            await _mediator.Send(new ListStepsQuery(), ct);

        /// <summary>BL-229: the pipelines that call this one (Execute or Dispatch pipeline), by their latest versions.</summary>
        [HttpGet("{definitionId}/callers")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> Callers(string definitionId, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new ListCallersQuery(definitionId), ct)));

        [HttpGet("{definitionId}/graph")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> Graph(string definitionId, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var graph = await _mediator.Send(new GetGraphQuery(definitionId), ct);
                return graph is null ? NotFound() : Ok(graph);
            });

        [HttpPut("{definitionId}/graph")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> SaveGraph(string definitionId, [FromBody] PipelineGraph graph, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var view = await _mediator.Send(new SaveGraphCommand(definitionId, graph), ct);
                return view is null ? NotFound() : Ok(view);
            });

        /// <summary>
        /// The collaborating save (BL-97 increment 12): the graph with the one it was based on. 200 when it applied or merged,
        /// 409 - carrying the conflicts and the graph stored now - when a field it changed was changed by someone else.
        /// </summary>
        [HttpPut("{definitionId}/graph/merge")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> SaveGraphMerged(string definitionId, [FromBody] GraphSaveRequest request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var result = await _mediator.Send(new SaveGraphMergedCommand(definitionId, request.Graph, request.Base), ct);
                return result is null
                    ? NotFound()
                    : result.Status == nameof(Stomme.Collaboration.ConflictResolutionStatus.Rejected) ? Conflict(result) : Ok(result);
            });

        [HttpGet("{definitionId}")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> Get(string definitionId, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var detail = await _mediator.Send(new GetPipelineQuery(definitionId), ct);
                return detail is null ? NotFound() : Ok(detail);
            });

        [HttpPost]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Create([FromBody] CreatePipelineRequest request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new CreatePipelineCommand(request.Name, request.Description), ct)));

        /// <summary>BL-229: the site's variables - <c>{{ $vars.name }}</c> in every pipeline's expressions.</summary>
        [HttpGet("variables")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<Dictionary<string, string>> SiteVariables(CancellationToken ct) =>
            await _mediator.Send(new GetSiteVariablesQuery(), ct);

        /// <summary>BL-229: replaces the site's variables; 400 with the reason for a name or a value that cannot be one.</summary>
        [HttpPut("variables")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> SetSiteVariables([FromBody] Dictionary<string, string>? variables, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new SetSiteVariablesCommand(variables), ct)));

        /// <summary>BL-229 - n8n's Data tables: the site's tables, by name.</summary>
        [HttpGet("tables")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<List<PipelineSiteTableView>> SiteTables(CancellationToken ct) =>
            await _mediator.Send(new ListSiteTablesQuery(), ct);

        /// <summary>BL-229: makes a data table; 400 with the reason for a name or a column that cannot be one.</summary>
        [HttpPost("tables")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> CreateSiteTable([FromBody] PipelineSiteTableRequest? request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new CreateSiteTableCommand(request), ct)));

        /// <summary>BL-229: renames a data table or changes its columns.</summary>
        [HttpPut("tables/{tableId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> UpdateSiteTable([FromRoute] string tableId, [FromBody] PipelineSiteTableRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new UpdateSiteTableCommand(tableId, request), ct) is { } updated ? Ok(updated) : NotFound());

        /// <summary>BL-229: how many comments each pipeline has open - for the pipelines list.</summary>
        [HttpGet("comments/open")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyDictionary<string, int>> OpenCommentCounts(CancellationToken ct) =>
            await _mediator.Send(new ListOpenCommentCountsQuery(), ct);

        /// <summary>BL-229: the people a comment may mention - the site's people who can see its pipelines.</summary>
        [HttpGet("comments/people")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineMentionView>> MentionablePeople(CancellationToken ct) =>
            await _mediator.Send(new ListMentionablePeopleQuery(), ct);

        /// <summary>BL-229 - comments on a pipeline's steps, oldest first: for anyone who can see the pipeline.</summary>
        [HttpGet("{definitionId}/comments")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineStepCommentView>> StepComments([FromRoute] string definitionId, CancellationToken ct) =>
            await _mediator.Send(new ListStepCommentsQuery(definitionId), ct);

        /// <summary>BL-229: a comment added to a step - anyone who can see the pipeline may.</summary>
        [HttpPost("{definitionId}/comments")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> AddStepComment([FromRoute] string definitionId, [FromBody] PipelineStepCommentRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new AddStepCommentCommand(definitionId, request?.StepId ?? string.Empty, request?.Text ?? string.Empty), ct) is { } comment
                ? Ok(comment)
                : NotFound());

        /// <summary>BL-229: a comment dealt with (?resolved=true) or open again.</summary>
        [HttpPut("{definitionId}/comments/{id:guid}/resolved")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> SetStepCommentResolved([FromRoute] string definitionId, [FromRoute] Guid id, [FromQuery] bool resolved, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new SetStepCommentResolvedCommand(definitionId, id, resolved), ct) is { } comment ? Ok(comment) : NotFound());

        /// <summary>BL-229: a comment deleted - its author's, or anyone's with the manage key.</summary>
        [HttpDelete("{definitionId}/comments/{id:guid}")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> DeleteStepComment([FromRoute] string definitionId, [FromRoute] Guid id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeleteStepCommentCommand(definitionId, id), ct) is not null ? NoContent() : NotFound());

        /// <summary>BL-229: deletes a data table with its rows.</summary>
        [HttpDelete("tables/{tableId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DeleteSiteTable([FromRoute] string tableId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeleteSiteTableCommand(tableId), ct) ? NoContent() : NotFound());

        /// <summary>BL-229 - n8n's Evaluations: a pipeline's evaluations, newest first, each with its counts and averages.</summary>
        [HttpGet("{definitionId}/evaluations")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<List<PipelineEvaluationView>> Evaluations([FromRoute] string definitionId, CancellationToken ct) =>
            await _mediator.Send(new ListEvaluationsQuery(definitionId), ct);

        /// <summary>BL-229: starts an evaluation of the published pipeline over a data table's rows; 400 with the reason when it cannot.</summary>
        [HttpPost("{definitionId}/evaluations")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> StartEvaluation([FromRoute] string definitionId, [FromBody] PipelineEvaluationRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new StartEvaluationCommand(definitionId, request), ct) is { } started ? Ok(started) : NotFound());

        /// <summary>BL-229: one evaluation with every row.</summary>
        [HttpGet("evaluations/{id}")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IActionResult> Evaluation([FromRoute] string id, CancellationToken ct) =>
            await _mediator.Send(new GetEvaluationQuery(id), ct) is { } evaluation ? Ok(evaluation) : NotFound();

        /// <summary>BL-229: stops a running evaluation.</summary>
        [HttpPost("evaluations/{id}/cancel")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> CancelEvaluation([FromRoute] string id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new CancelEvaluationCommand(id), ct) is { } evaluation ? Ok(evaluation) : NotFound());

        /// <summary>BL-229: deletes an evaluation that has ended; 400 while it runs.</summary>
        [HttpDelete("evaluations/{id}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DeleteEvaluation([FromRoute] string id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeleteEvaluationCommand(id), ct) ? NoContent() : NotFound());

        /// <summary>BL-229: a page of a data table's rows, oldest first.</summary>
        [HttpGet("tables/{tableId}/rows")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> TableRows([FromRoute] string tableId, [FromQuery] int skip, [FromQuery] int? take, [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] bool descending, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new GetTableRowsQuery(tableId, skip, take ?? 100, search, sort, descending), ct) is { } rows ? Ok(rows) : NotFound());

        /// <summary>BL-229: adds a row by hand; 400 with the reason for a value that does not fit its column.</summary>
        [HttpPost("tables/{tableId}/rows")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> AddTableRow([FromRoute] string tableId, [FromBody] PipelineTableRowRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new AddTableRowCommand(tableId, request), ct) is { } row ? Ok(row) : NotFound());

        /// <summary>BL-229: changes a row's values by hand.</summary>
        [HttpPut("tables/{tableId}/rows/{rowId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> UpdateTableRow([FromRoute] string tableId, [FromRoute] string rowId, [FromBody] PipelineTableRowRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new UpdateTableRowCommand(tableId, rowId, request), ct) is { } row ? Ok(row) : NotFound());

        /// <summary>BL-229: a data table's rows as CSV, with a file name for the download.</summary>
        [HttpGet("tables/{tableId}/csv")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> ExportTableCsv([FromRoute] string tableId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new ExportTableCsvQuery(tableId), ct) is { } csv ? Ok(csv) : NotFound());

        /// <summary>BL-229: adds a CSV's rows to a data table, all or nothing; 400 naming the row and column that does not fit.</summary>
        [HttpPost("tables/{tableId}/csv")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        [RequestSizeLimit(16 * 1024 * 1024)]
        public Task<IActionResult> ImportTableCsv([FromRoute] string tableId, [FromBody] PipelineTableImportRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new ImportTableCsvCommand(tableId, request?.Csv), ct) is { } imported ? Ok(imported) : NotFound());

        /// <summary>BL-229: deletes a row by hand.</summary>
        [HttpDelete("tables/{tableId}/rows/{rowId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DeleteTableRow([FromRoute] string tableId, [FromRoute] string rowId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeleteTableRowCommand(tableId, rowId), ct) ? NoContent() : NotFound());

        /// <summary>BL-229: a folder of the site's pipeline folder - what the file steps read and write; 404 when there is none.</summary>
        [HttpGet("files")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Files([FromQuery] string? folder, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new ListPipelineFilesQuery(folder), ct) is { } listing ? Ok(listing) : NotFound());

        /// <summary>The room a multipart form takes around the file it carries.</summary>
        private const long FormRoom = 1024 * 1024;

        /// <summary>
        /// BL-229: a file taken into a folder of the site's pipeline folder - a multipart form, the file under <c>file</c>; 400
        /// saying why when it is refused, 413 when it is larger than a pipeline file may be. The request is bounded HERE, before
        /// its body is read, by the file steps' own limit (<c>Pipelines:Files:MaxFileBytes</c>) and room for the form - Kestrel's
        /// 30 MB default sits below that limit, and no author's upload is ever unbounded.
        /// </summary>
        [HttpPost("files")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> UploadFile([FromQuery] string? folder, [FromServices] IOptions<Modules.Pipelines.Steps.PipelineStepOptions> options, CancellationToken ct)
        {
            var maxFile = options.Value.Files.MaxFileBytes;
            if (HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = maxFile + FormRoom;
            }

            if (!Request.HasFormContentType)
            {
                return Problem(detail: "Send the file as a multipart form, under 'file'.", statusCode: StatusCodes.Status400BadRequest, title: "The pipelines request was refused.");
            }

            HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(
                new Microsoft.AspNetCore.Http.Features.FormFeature(Request, new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = maxFile + FormRoom }));
            IFormCollection form;
            try
            {
                form = await Request.ReadFormAsync(ct);
            }
            catch (Exception ex) when (ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } or System.IO.InvalidDataException)
            {
                return Problem(detail: $"The file is larger than the {PipelineFilePaths.Size(maxFile)} a pipeline file may be.", statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "The pipelines request was refused.");
            }

            if (form.Files.GetFile("file") is not { } file)
            {
                return Problem(detail: "Choose a file to upload.", statusCode: StatusCodes.Status400BadRequest, title: "The pipelines request was refused.");
            }

            await using var content = file.OpenReadStream();
            return await RunAsync(async () => Ok(await _mediator.Send(new UploadPipelineFileCommand(folder, file.FileName, content), ct)));
        }

        /// <summary>BL-229: a folder made in the site's pipeline folder.</summary>
        [HttpPost("files/folders")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> CreateFileFolder([FromBody] PipelineFileFolderRequest? request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new CreatePipelineFolderCommand(request?.Path), ct)));

        /// <summary>BL-229: a file, or a folder with what is in it, deleted from the site's pipeline folder; 404 when there is none.</summary>
        [HttpDelete("files")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DeleteFile([FromQuery] string? path, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeletePipelineFileCommand(path), ct) ? NoContent() : NotFound());

        /// <summary>
        /// BL-229: a file of the site's pipeline folder, given back to save - always as an attachment of no particular type, so a
        /// file a run wrote (an HTML page, an SVG) is never opened as the site's own page.
        /// </summary>
        [HttpGet("files/content")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DownloadFile([FromQuery] string? path, CancellationToken ct) =>
            RunAsync(async () =>
            {
                if (await _mediator.Send(new GetPipelineFileQuery(path), ct) is not { } file)
                {
                    return NotFound();
                }

                Response.Headers["X-Content-Type-Options"] = "nosniff"; // and never sniffed into a type that would open
                return PhysicalFile(file.FullPath, "application/octet-stream", file.Name);
            });

        /// <summary>BL-229: the site's published AI agent tools - for an AI agent step's Tools.</summary>
        [HttpGet("agent-tools")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<AgentToolView>> AgentTools(CancellationToken ct) =>
            await _mediator.Send(new ListAgentToolsQuery(), ct);

        /// <summary>BL-229 increment 5b: what this site lets the editor offer - JavaScript expressions, webhooks.</summary>
        [HttpGet("capabilities")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<PipelineCapabilitiesView> Capabilities(CancellationToken ct) =>
            await _mediator.Send(new GetCapabilitiesQuery(), ct);

        /// <summary>BL-229 increment 5b: a JavaScript expression's value in the bounded sandbox, for the editor's preview.</summary>
        [HttpPost("expressions/script/preview")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> PreviewScript([FromBody] ScriptPreviewRequest request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new PreviewScriptCommand(request.Script ?? string.Empty, ToValues(request.Values)), ct)));

        /// <summary>
        /// BL-229 - n8n's AI workflow builder: the canvas changed by the site's model as the author asks, judged by the save's rule
        /// and stored nowhere - or its answer alone, with no graph; 400 with the reason when the model cannot be asked, or made what
        /// the save refuses.
        /// </summary>
        [HttpPost("design")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Design([FromBody] PipelineGraphDesignRequest? request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new DesignPipelineGraphCommand(request?.Graph!, request?.Instruction ?? string.Empty, request?.Earlier), ct)));

        /// <summary>
        /// BL-229 - n8n's "Ask AI" in its Code node: a Run script step's script written by the site's model as the author asks,
        /// told what the script can see and never a value; stored nowhere and run by nothing. 400 with the reason when the model
        /// cannot be asked or scripts are off.
        /// </summary>
        [HttpPost("script")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> WriteScript([FromBody] PipelineScriptRequest? request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new WriteScriptCommand(request!), ct)));

        /// <summary>
        /// BL-229 - n8n's Code node linting: each script the editor holds checked to parse as its step runs it - nothing run,
        /// nothing stored; why each does not, in the order asked (null for one that does).
        /// </summary>
        [HttpPost("scripts/check")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> CheckScripts([FromBody] PipelineScriptCheckRequest? request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new CheckScriptsQuery(request!), ct)));

        private static IReadOnlyDictionary<string, string?> ToValues(Dictionary<string, string>? values)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in values ?? new Dictionary<string, string>())
            {
                result[name] = value;
            }

            return result;
        }

        /// <summary>BL-229 increment 10: what the editor says about a trigger step - a webhook's address, a schedule's next runs.</summary>
        [HttpGet("triggers/info")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<TriggerInfoView> TriggerInfo([FromQuery] string type, [FromQuery] string? value, [FromQuery] string? zone, CancellationToken ct) =>
            await _mediator.Send(new GetTriggerInfoQuery(type, value, zone), ct);

        /// <summary>BL-229: the newest message in a mailbox of one of the site's connections, for the Email received trigger's test (n8n's Fetch test event).</summary>
        [HttpGet("triggers/email/newest")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public async Task<PipelineTestEventView> NewestEmail([FromQuery] string? connection, [FromQuery] string? mailbox, CancellationToken ct) =>
            await _mediator.Send(new FetchNewestEmailQuery(connection, mailbox), ct);

        /// <summary>BL-229 increment 10: waits (at most two minutes) for the next authentic call to one of the site's webhooks.</summary>
        [HttpPost("triggers/webhooks/{name}/listen")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> ListenForWebhook(string name, [FromQuery] int seconds, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new ListenForWebhookCommand(name, seconds <= 0 ? 60 : seconds), ct)));

        /// <summary>D111a: a new secret of the webhook's own, replacing any it had - answered once, never shown again.</summary>
        [HttpPost("triggers/webhooks/{name}/secret")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> RenewWebhookSecret(string name, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new RenewWebhookSecretCommand(name), ct)));

        /// <summary>D111a: removes the webhook's own secret, so it takes the installation's again; 404 when it had none.</summary>
        [HttpDelete("triggers/webhooks/{name}/secret")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> RemoveWebhookSecret(string name, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new RemoveWebhookSecretCommand(name), ct) ? NoContent() : NotFound());

        /// <summary>BL-229 increment 10: a new pipeline with a body - a file imported, or a starter; refused (400) as the save would refuse it.</summary>
        [HttpPost("import")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Import([FromBody] ImportPipelineRequest request, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new ImportPipelineCommand(request.Name, request.Description, request.Graph), ct)));

        [HttpPut("{definitionId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Update(string definitionId, [FromBody] UpdatePipelineRequest request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var view = await _mediator.Send(new UpdatePipelineCommand(definitionId, request.Name, request.Description, request.Body), ct);
                return view is null ? NotFound() : Ok(view);
            });

        /// <summary>BL-229: replaces the pipeline's tags (an empty list clears them); 400 with the reason for a tag that cannot be one.</summary>
        [HttpPut("{definitionId}/tags")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> SetTags(string definitionId, [FromBody] List<string>? tags, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new SetPipelineTagsCommand(definitionId, tags), ct) is { } saved ? Ok(saved) : NotFound());

        /// <summary>BL-229 - n8n's folders: moves the pipeline to a folder (empty: the top); 400 with the reason for one that cannot be one.</summary>
        [HttpPut("{definitionId}/folder")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> SetFolder(string definitionId, [FromBody] PipelineFolderRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new SetPipelineFolderCommand(definitionId, request?.Folder), ct) is { } saved ? Ok(new PipelineFolderRequest { Folder = saved }) : NotFound());

        /// <summary>BL-229 - the map of the site's pipelines: which runs which.</summary>
        [HttpGet("map")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<PipelineMapView> Map(CancellationToken ct) => await _mediator.Send(new GetPipelineMapQuery(), ct);

        /// <summary>BL-229 - the site's pipeline activity, newest first: all of it, or one pipeline's (n8n's audit log).</summary>
        [HttpGet("audit")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public async Task<IReadOnlyList<PipelineAuditEntryView>> Audit(string? definitionId, int take = PipelineAuditLimits.DefaultTake, CancellationToken ct = default) =>
            await _mediator.Send(new Stomme.Modules.Pipelines.Audit.ListPipelineAuditQuery(definitionId, take), ct);

        /// <summary>BL-229 - find in every pipeline: where a text is used in the site's pipelines, a sensitive input never read.</summary>
        [HttpGet("find")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineFoundView>> Find([FromQuery] string? text, CancellationToken ct) =>
            await _mediator.Send(new FindInPipelinesQuery(text), ct);

        /// <summary>BL-229 - n8n's folder rename, move and delete: what is in the folder goes along, or up; 404 for a folder no pipeline is in, 400 with the reason for a move that cannot be.</summary>
        [HttpPut("folders")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> MoveFolder([FromBody] PipelineFolderMoveRequest? request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new MovePipelineFolderCommand(request?.Folder ?? "", request?.NewFolder, request?.Remove == true), ct) is { } moved ? Ok(moved) : NotFound());

        [HttpPost("{definitionId}/publish")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Publish(string definitionId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PublishPipelineRequest? request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                // BL-229: what changed, in the author's words - optional; a body-less publish is one with no note.
                var result = await _mediator.Send(new PublishPipelineCommand(definitionId, request?.Note), ct);
                return result is null ? NotFound() : Ok(result);
            });

        [HttpPost("{definitionId}/retract")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Retract(string definitionId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new RetractPipelineCommand(definitionId), ct) ? NoContent() : NotFound());

        /// <summary>BL-229 - n8n's archive: the pipeline set aside, unpublished.</summary>
        [HttpPost("{definitionId}/archive")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Archive(string definitionId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new ArchivePipelineCommand(definitionId), ct) ? NoContent() : NotFound());

        /// <summary>BL-229: the pipeline back from the archive, a draft.</summary>
        [HttpPost("{definitionId}/restore")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Restore(string definitionId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new RestorePipelineCommand(definitionId), ct) ? NoContent() : NotFound());

        [HttpDelete("{definitionId}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> Delete(string definitionId, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeletePipelineCommand(definitionId), ct) ? NoContent() : NotFound());

        [HttpPost("{definitionId}/runs")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> Start(string definitionId, [FromBody] StartPipelineRequest? request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var started = await _mediator.Send(new StartPipelineCommand(definitionId, request?.CorrelationId, request?.Input), ct);
                return started is null ? NotFound() : Ok(started);
            });

        [HttpGet("runs")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> Runs([FromQuery] string? definitionId, [FromQuery] int take, [FromQuery] string? state, [FromQuery] string? search,
            [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new ListRunsQuery(definitionId, take <= 0 ? 100 : take, state, search, from, to), ct)));

        /// <summary>A run; 204 for one just started and not stored yet (stored when its first burst of work ends), 404 for none.</summary>
        [HttpGet("runs/{id}")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> Run(string id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var run = await _mediator.Send(new GetRunQuery(id), ct);
                return run is not null ? Ok(run) : await _mediator.Send(new IsRunStartingQuery(id), ct) ? NoContent() : NotFound();
            });

        [HttpGet("runs/{id}/log")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineRunLogLine>> RunLog(string id, CancellationToken ct) =>
            await _mediator.Send(new GetRunLogQuery(id), ct);

        // BL-229 increment 2: each step execution of a run - states for a viewer, inputs and outputs for an author.
        [HttpGet("runs/{id}/steps")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineStepRunView>> RunSteps(string id, CancellationToken ct) =>
            await _mediator.Send(new ListRunStepsQuery(id), ct);

        // BL-229 increment 7: the pipeline's versions, and the graph of one of them.
        [HttpGet("{definitionId}/versions")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineVersionView>> Versions(string definitionId, CancellationToken ct) =>
            await _mediator.Send(new ListVersionsQuery(definitionId), ct);

        [HttpGet("{definitionId}/versions/{version:int}/graph")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public Task<IActionResult> VersionGraph(string definitionId, int version, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var graph = await _mediator.Send(new GetVersionGraphQuery(definitionId, version), ct);
                return graph is null ? NotFound() : Ok(graph);
            });

        // BL-229 increment 6: test one step of the editor's graph, on its own and for real - or every step leading to it, or the
        // whole pipeline as it stands (n8n's "Test workflow"); nothing is stored.
        [HttpPost("{definitionId}/test-step")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public async Task<StepTestResult> TestStep(string definitionId, [FromBody] StepTestRequest request, CancellationToken ct) =>
            await _mediator.Send(new TestStepCommand(request.Graph, request.StepId, request.Values, request.UpToHere, request.Whole, definitionId), ct);

        /// <summary>
        /// Raise a named event for this site's pipelines (increment (e)): starts every published pipeline whose Event
        /// trigger names it and resumes every run waiting on it. The body, if any, is the event's payload as text.
        /// </summary>
        [HttpPost("events/{name}")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> RaiseEvent([FromRoute] string name, [FromQuery] string? correlationId, CancellationToken ct)
        {
            string? payload = null;
            if (Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0)
            {
                using var reader = new System.IO.StreamReader(Request.Body);
                payload = await reader.ReadToEndAsync(ct);
            }

            return await _mediator.Send(new RaisePipelineEventCommand(name, payload, correlationId), ct) ? Accepted() : NotFound();
        }

        /// <summary>
        /// The webhook receiver (increment (e)): PUBLIC and unauthenticated, so it answers 404 unless
        /// <c>Pipelines:Webhooks:Enabled</c> (an action-level constraint was measured to fall through to the host's
        /// catch-all, which answers a JSON POST with 400 - the explicit 404 says what the switch means), verifies the
        /// body's HMAC under the configured secret (401 with no detail otherwise), is rate-limited per site and caller,
        /// and answers 202 whether or not a pipeline listens under the name. The run is DISPATCHED, never awaited here.
        /// Since 2026-09-28 (the predecessor platform's per-endpoint guards): a caller outside the name's allowlist is 403 before its body is
        /// read, and within the name's deduplication window a repeat of an AUTHENTIC delivery is answered 202 and not
        /// dispatched - the window is consulted only after the signature, so an unsigned caller cannot pre-empt a delivery.
        /// </summary>
        [HttpPost("webhooks/{name}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting(PipelineWebhooks.PolicyName)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> ReceiveWebhook([FromRoute] string name, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineWebhookReceiver receiver,
            [FromServices] ICacheManager cache, [FromServices] SiteState siteState, [FromServices] PipelineWebhookListeners listeners, [FromServices] PipelineWebhookSecrets ownSecrets,
            CancellationToken ct)
        {
            var settings = options.Value.Webhooks;
            if (!settings.Enabled || !Modules.Pipelines.Steps.Webhook.IsValidName(name))
            {
                return NotFound();
            }

            var normalized = Modules.Pipelines.Steps.Webhook.Normalize(name);
            var endpoint = settings.For(normalized);
            if (!PipelineWebhooks.IsCallerAllowed(endpoint.AllowedCallers, HttpContext.Connection.RemoteIpAddress))
            {
                _logger.Log(LogLevel.Warning, this, LogFunction.Security, "Pipeline webhook '{Name}' refused: the caller {Caller} is not on its allowlist.", normalized, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            if (Request.ContentLength is { } length && length > settings.MaxBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await Request.Body.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > settings.MaxBodyBytes)
                {
                    return StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            var body = buffer.ToArray();
            // The name's own secret when it has one (D111a); else the installation's - the mounted file's, or the setting's. None
            // refuses every call, and an own secret that cannot be read refuses rather than fall back to the installation's.
            var (secret, secretProblem, _) = await ownSecrets.ForCallAsync(siteState.Alias?.SiteId, normalized, settings, ct);
            if (!PipelineWebhooks.VerifySignature(secret, body, Request.Headers[PipelineWebhooks.SignatureHeaderName].ToString()))
            {
                _logger.Log(LogLevel.Warning, this, LogFunction.Security, "Pipeline webhook '{Name}' refused: {Reason}", name,
                    secretProblem ?? "the signature did not verify.");
                return Unauthorized();
            }

            if (endpoint.DeduplicationSeconds > 0 && siteState.Alias is { } alias)
            {
                // The delivery's own id when the name says which header carries it (and the request has it); else the body.
                var id = string.IsNullOrWhiteSpace(endpoint.DeduplicationHeader) ? string.Empty : Request.Headers[endpoint.DeduplicationHeader.Trim()].ToString();
                var material = id.Length > 0 ? "id:" + id : "body:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
                if (!await PipelineWebhooks.IsFirstDeliveryAsync(cache, alias, normalized, material, TimeSpan.FromSeconds(endpoint.DeduplicationSeconds)))
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Other, "Pipeline webhook '{Name}': a repeat delivery inside its {Seconds}-second window was dropped.", normalized, endpoint.DeduplicationSeconds);
                    return Accepted();
                }
            }

            // BL-229 increment 10: an editor listening to this name sees the call too - an authentic, admitted, first delivery only.
            if (siteState.Alias is { } site)
            {
                listeners.Offer(PipelineWebhookListeners.Key(site.TenantId, site.SiteId, normalized), DateTime.UtcNow, Request.ContentType, System.Text.Encoding.UTF8.GetString(body));
            }

            // BL-229, Respond to webhook: every pipeline on the name is started; when one answers from the pipeline, the call waits
            // for that answer (PipelineWebhookReceiver), else - or when it does not come in time - it is accepted.
            var answer = await receiver.ReceiveAsync(normalized, System.Text.Encoding.UTF8.GetString(body), Request.ContentType,
                Request.Headers["X-Correlation-Id"].ToString() is { Length: > 0 } correlation ? correlation : null, ct);
            if (answer is null)
            {
                return Accepted();
            }

            foreach (var (header, value) in answer.Headers)
            {
                Response.Headers.Append(header, value);
            }

            if (answer.File is { } file)
            {
                // A file: inline, or a download under its name (Content-Disposition, which no header line may set).
                var result = new FileContentResult(file, answer.ContentType) { FileDownloadName = answer.DownloadName ?? string.Empty };
                Response.StatusCode = answer.StatusCode;
                return result;
            }

            return new ContentResult { StatusCode = answer.StatusCode, ContentType = answer.ContentType, Content = answer.Body };
        }

        /// <summary>
        /// BL-229 - n8n's Form trigger: the form a published pipeline offers under the name, as a standalone page - PUBLIC, like the
        /// webhook receiver, and 404 unless <c>Pipelines:Forms:Enabled</c> and a published pipeline on the site has the form.
        /// Rate-limited per site and caller. <c>?sent=1</c> is the page after a submission (the post redirects there, so a reload
        /// does not send it again).
        /// </summary>
        [HttpGet("forms/{name}")]
        [AllowAnonymous]
        [EnableRateLimiting(PipelineFormPage.PolicyName)]
        public async Task<IActionResult> ShowForm([FromRoute] string name, [FromQuery] string? sent, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineFormReceiver receiver, CancellationToken ct)
        {
            if (!options.Value.Forms.Enabled || !Modules.Pipelines.Steps.Webhook.IsValidName(name))
            {
                return NotFound();
            }

            var form = await receiver.FindAsync(Modules.Pipelines.Steps.Webhook.Normalize(name), ct);
            if (form is null)
            {
                return NotFound();
            }

            // BL-229, n8n's prefilled fields: the address gives a field its value - a hidden field's above all (?source=newsletter).
            return FormPage(sent == "1" ? PipelineFormPage.Thanks(form) : PipelineFormPage.Render(form, PipelineFormPage.FromAddress(form, Prefill())), StatusCodes.Status200OK);
        }

        /// <summary>
        /// BL-229: a submission of a form - its answers judged by the form's fields (the page again, 400, with what is wrong), then
        /// sent on as the trigger's stimulus, which starts every published pipeline with the form; then a redirect to the form's
        /// thank-you. A submission that fills the field only robots see is thanked and dropped. Only a URL-encoded body, at most
        /// <c>Pipelines:Forms:MaxBodyBytes</c>.
        /// </summary>
        [HttpPost("forms/{name}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting(PipelineFormPage.PolicyName)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> SubmitForm([FromRoute] string name, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineFormReceiver receiver,
            [FromServices] IOptions<Modules.Pipelines.Steps.PipelineStepOptions> stepOptions, [FromServices] Stomme.Engine.Common.Multitenancy.ITenantAccessor tenants,
            [FromServices] PipelineFormContinuations continuations, CancellationToken ct)
        {
            var settings = options.Value.Forms;
            if (!settings.Enabled || !Modules.Pipelines.Steps.Webhook.IsValidName(name))
            {
                return NotFound();
            }

            var normalized = Modules.Pipelines.Steps.Webhook.Normalize(name);
            var form = await receiver.FindAsync(normalized, ct);
            if (form is null)
            {
                return NotFound();
            }

            // URL-encoded, or multipart with a file field's file (BL-229, n8n's File field) - its files held aside until it is accepted.
            var (submission, refusal) = await PipelineFormSubmissions.ReadAsync(Request, settings, form.Fields, PipelineSiteFiles.Incoming(stepOptions.Value), ct);
            if (submission is null)
            {
                return StatusCode(refusal ?? StatusCodes.Status400BadRequest);
            }

            using (submission)
            {
                var thanks = "?sent=1"; // relative to this address, so a site under a path keeps it
                if (!string.IsNullOrEmpty(submission.Posted(PipelineFormPage.HoneypotField)))
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Pipeline form '{Name}': a submission that filled the hidden field was dropped.", normalized);
                    return LocalRedirect303(thanks);
                }

                if (Judge(form, submission) is { } refused)
                {
                    return refused;
                }

                var answers = PipelineForms.Read(form.Fields, submission.Posted, out _)!;
                submission.Keep(answers, PipelineSiteFiles.Root(stepOptions.Value, tenants), normalized, DateTimeOffset.UtcNow);
                // BL-229, n8n's multi-page forms: where a next page can follow, the person is taken on to it once the run reaches it.
                var next = await continuations.SendAsync(form.LeadsOn, NextPageWithin(settings), continueTo => receiver.SubmitAsync(normalized, PipelineForms.ToJson(answers), continueTo, ct), ct);
                return LocalRedirect303(next is null ? thanks : "../calls/" + next + "/form"); // relative: forms/{name} and calls/{token}/form are siblings
            }
        }

        // A submission's answers judged by the form's fields - a file too large is its field's error as well - and the form sent back
        // with what is wrong (400); null when they are all right.
        private ContentResult? Judge(PipelineFormSpec form, PipelineFormSubmission submission)
        {
            var answers = PipelineForms.Read(form.Fields, submission.Posted, out var errors);
            foreach (var (key, error) in submission.FileErrors)
            {
                errors[key] = error;
            }

            if (answers is not null && errors.Count == 0)
            {
                return null;
            }

            var given = form.Fields.Where(field => field.Type != PipelineFormFieldTypes.File).Select(PipelineForms.KeyOf).Distinct().ToDictionary(key => key, submission.Posted);
            return FormPage(PipelineFormPage.Render(form, given, errors), StatusCodes.Status400BadRequest);
        }

        // What the address of a form's page gives - each query value once (the first); "sent" is the thank-you's own.
        private IEnumerable<KeyValuePair<string, string?>> Prefill() =>
            Request.Query.Where(entry => entry.Key != "sent").Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value.FirstOrDefault()));

        // The form's page: its own policy (inline style, posts to itself, nothing loaded, no frame around it), never cached.
        private ContentResult FormPage(string html, int status)
        {
            Response.Headers["Content-Security-Policy"] = PipelineFormPage.ContentSecurityPolicy;
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            Response.Headers.CacheControl = "no-store";
            return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
        }

        // How long a form submission waits to be taken on to its next page (Pipelines:Forms:NextPageWithinSeconds, 1 to 60 seconds).
        private static TimeSpan NextPageWithin(FormTriggerOptions settings) => TimeSpan.FromSeconds(Math.Clamp(settings.NextPageWithinSeconds, 1, 60));

        // 303: the browser follows a POST's answer with a GET, so a reload of the thank-you sends nothing again.
        private IActionResult LocalRedirect303(string url)
        {
            Response.Headers.Location = url;
            return StatusCode(StatusCodes.Status303SeeOther);
        }

        /// <summary>
        /// BL-229 - n8n's Chat trigger, hosted: the chat a published pipeline offers under the name, with this visitor's conversation
        /// so far - PUBLIC, 404 unless <c>Pipelines:Chats:Enabled</c> and a published pipeline on the site has the chat. The visitor's
        /// conversation is a random session in a cookie of this chat's own (HttpOnly, SameSite=Strict, its path the chat's).
        /// </summary>
        [HttpGet("chats/{name}")]
        [AllowAnonymous]
        [EnableRateLimiting(PipelineChatPage.PolicyName)]
        public async Task<IActionResult> ShowChat([FromRoute] string name, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineChatReceiver receiver, CancellationToken ct)
        {
            if (!options.Value.Chats.Enabled || !Modules.Pipelines.Steps.Webhook.IsValidName(name))
            {
                return NotFound();
            }

            var normalized = Modules.Pipelines.Steps.Webhook.Normalize(name);
            var chat = await receiver.FindAsync(normalized, ct);
            if (chat is null)
            {
                return NotFound();
            }

            var session = ChatSession(normalized, create: true)!;
            return FormPage(PipelineChatPage.Render(chat, await receiver.TranscriptAsync(normalized, session, ct)), StatusCodes.Status200OK);
        }

        /// <summary>
        /// BL-229: a visitor's message - the chat's pipeline RUN with it (its answer the result of the last step the run completes),
        /// both kept in the visitor's conversation, then a redirect to the page (303). A URL-encoded body only, at most
        /// <c>Pipelines:Chats:MaxBodyBytes</c>; a message of at most 2,000 characters. "New conversation" forgets this visitor's;
        /// a post that fills the hidden field is dropped.
        /// </summary>
        [HttpPost("chats/{name}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [DisableFormValueModelBinding]
        [EnableRateLimiting(PipelineChatPage.PolicyName)]
        public async Task<IActionResult> SendChat([FromRoute] string name, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineChatReceiver receiver, CancellationToken ct)
        {
            var settings = options.Value.Chats;
            if (!settings.Enabled || !Modules.Pipelines.Steps.Webhook.IsValidName(name))
            {
                return NotFound();
            }

            var normalized = Modules.Pipelines.Steps.Webhook.Normalize(name);
            var chat = await receiver.FindAsync(normalized, ct);
            if (chat is null)
            {
                return NotFound();
            }

            if (!string.Equals(Request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            if (Request.ContentLength is { } length && length > settings.MaxBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await Request.Body.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > settings.MaxBodyBytes)
                {
                    return StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            var posted = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            string? Posted(string key) => posted.TryGetValue(key, out var values) ? values.ToString() : null;
            var back = Uri.EscapeDataString(normalized); // relative to this address, so a site under a path keeps it
            var session = ChatSession(normalized, create: true)!;
            if (!string.IsNullOrEmpty(Posted(PipelineFormPage.HoneypotField)))
            {
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "Pipeline chat '{Name}': a message that filled the hidden field was dropped.", normalized);
                return LocalRedirect303(back);
            }

            if (!string.IsNullOrEmpty(Posted(PipelineChatPage.NewConversationField)))
            {
                await receiver.ForgetAsync(normalized, session, ct);
                ChatSession(normalized, create: true, fresh: true);
                return LocalRedirect303(back);
            }

            var message = (Posted("message") ?? "").Trim();
            if (message.Length == 0)
            {
                return LocalRedirect303(back);
            }

            if (message.Length > PipelineChatOptionsDefaults.MaxMessageLength)
            {
                return FormPage(PipelineChatPage.Render(chat, await receiver.TranscriptAsync(normalized, session, ct), $"A message is at most {PipelineChatOptionsDefaults.MaxMessageLength} characters.", message), StatusCodes.Status400BadRequest);
            }

            var answer = await receiver.AskAsync(normalized, session, message, ct);
            await receiver.AppendAsync(normalized, session, [new PipelineChatLine("user", message), new PipelineChatLine("assistant", answer)], ct);
            return LocalRedirect303(back);
        }

        // This chat's visitor session, from its own cookie; a new one when there is none (or a fresh one is asked for).
        private string? ChatSession(string chat, bool create, bool fresh = false)
        {
            const string cookie = "stomme_chat";
            if (!fresh && Request.Cookies.TryGetValue(cookie, out var existing) && PipelineChatReceiver.IsSession(existing))
            {
                return existing;
            }

            if (!create)
            {
                return null;
            }

            var session = PipelineChatReceiver.NewSession();
            Response.Cookies.Append(cookie, session, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = Request.PathBase.Add("/api/Pipelines/chats/" + chat),
                MaxAge = TimeSpan.FromDays(30),
                IsEssential = true,
            });
            return session;
        }

        /// <summary>
        /// BL-229: a call to a run's call address (Wait for a call) - PUBLIC like the webhook receiver and behind the same switch,
        /// rate limit and body limit (<c>Pipelines:Webhooks</c>): the random token in the address is what admits it, and a token
        /// that cannot be one is 404. 202 whether or not a run waits for it - the engine resumes the run, or queues the call for a
        /// minute when the run has not reached its wait yet.
        /// </summary>
        [HttpPost("calls/{token}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting(PipelineWebhooks.PolicyName)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> ReceiveCall([FromRoute] string token, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineCallReceiver receiver, CancellationToken ct)
        {
            var settings = options.Value.Webhooks;
            if (!settings.Enabled || !PipelineCalls.IsToken(token))
            {
                return NotFound();
            }

            if (Request.ContentLength is { } length && length > settings.MaxBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await Request.Body.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > settings.MaxBodyBytes)
                {
                    return StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            await receiver.ReceiveAsync(token, System.Text.Encoding.UTF8.GetString(buffer.ToArray()), Request.ContentType, ct);
            return Accepted();
        }

        /// <summary>
        /// BL-229 - a run's form address (Wait for a form): the form the run waits on, as a standalone page - PUBLIC like the Form
        /// trigger's and behind its switch and rate limit; the random token in the address is what admits a person. A form nobody
        /// waits on any more (answered, timed out) is a page saying it is closed. <c>?sent=1</c> is the page after a submission.
        /// </summary>
        [HttpGet("calls/{token}/form")]
        [AllowAnonymous]
        [EnableRateLimiting(PipelineFormPage.PolicyName)]
        public async Task<IActionResult> ShowWaitingForm([FromRoute] string token, [FromQuery] string? sent, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineFormWaits waits, CancellationToken ct)
        {
            if (!options.Value.Forms.Enabled || !PipelineCalls.IsToken(token))
            {
                return NotFound();
            }

            if (sent == "1")
            {
                return FormPage(PipelineFormPage.Notice("Thank you", "Your answers were sent."), StatusCodes.Status200OK);
            }

            var form = await waits.FindAsync(token, ct);
            return form is null
                ? FormPage(PipelineFormPage.Notice("This form is closed", "It was filled in already, or it is no longer open."), StatusCodes.Status404NotFound)
                : FormPage(PipelineFormPage.Render(form, PipelineFormPage.FromAddress(form, Prefill())), StatusCodes.Status200OK);
        }

        /// <summary>
        /// BL-229: a submission of a run's waiting form - judged by the form's fields (the page again, 400, with what is wrong), then
        /// the run resumed with the answers; then a redirect to the thank-you. Only a URL-encoded body, at most
        /// <c>Pipelines:Forms:MaxBodyBytes</c>; a submission that fills the field only robots see is thanked and dropped.
        /// </summary>
        [HttpPost("calls/{token}/form")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting(PipelineFormPage.PolicyName)]
        [DisableFormValueModelBinding]
        public async Task<IActionResult> SubmitWaitingForm([FromRoute] string token, [FromServices] IOptions<PipelineTriggerOptions> options, [FromServices] PipelineFormWaits waits,
            [FromServices] IOptions<Modules.Pipelines.Steps.PipelineStepOptions> stepOptions, [FromServices] Stomme.Engine.Common.Multitenancy.ITenantAccessor tenants,
            [FromServices] PipelineFormContinuations continuations, [FromServices] PipelineFormReceiver forms, CancellationToken ct)
        {
            var settings = options.Value.Forms;
            if (!settings.Enabled || !PipelineCalls.IsToken(token))
            {
                return NotFound();
            }

            var form = await waits.FindAsync(token, ct);
            if (form is null)
            {
                return FormPage(PipelineFormPage.Notice("This form is closed", "It was filled in already, or it is no longer open."), StatusCodes.Status404NotFound);
            }

            var (submission, refusal) = await PipelineFormSubmissions.ReadAsync(Request, settings, form.Fields, PipelineSiteFiles.Incoming(stepOptions.Value), ct);
            if (submission is null)
            {
                return StatusCode(refusal ?? StatusCodes.Status400BadRequest);
            }

            using (submission)
            {
                var thanks = "?sent=1"; // relative to this address, so a site under a path keeps it
                if (!string.IsNullOrEmpty(submission.Posted(PipelineFormPage.HoneypotField)))
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "A run's waiting form: a submission that filled the hidden field was dropped.");
                    return LocalRedirect303(thanks);
                }

                if (Judge(form, submission) is { } refused)
                {
                    return refused;
                }

                var answers = PipelineForms.Read(form.Fields, submission.Posted, out _)!;
                submission.Keep(answers, PipelineSiteFiles.Root(stepOptions.Value, tenants), "answers", DateTimeOffset.UtcNow);
                // BL-229: a page of a form with more pages - the person taken on to the next one once the run reaches it.
                var next = await continuations.SendAsync(form.LeadsOn, NextPageWithin(settings), continueTo => waits.SubmitAsync(token, PipelineForms.ToJson(answers), continueTo, ct), ct);
                if (next is not null)
                {
                    return LocalRedirect303("../" + next + "/form"); // relative: the run's next page, under calls/ as this one
                }

                // The last page of the Form trigger's form ends on that form's thank-you - its Answer, read from the published pipeline,
                // never from the address (n8n's form ending); a form no longer published, or any other wait, on the plain one.
                return LocalRedirect303(form.StartedBy is { } startedBy && Modules.Pipelines.Steps.Webhook.IsValidName(startedBy) && await forms.FindAsync(startedBy, ct) is not null
                    ? "../../forms/" + Uri.EscapeDataString(startedBy) + "?sent=1"
                    : thanks);
            }
        }

        /// <summary>BL-229 - n8n's execution annotations: says how the run went (good or bad, tags, a note); 400 with the reason past the rules.</summary>
        [HttpPut("runs/{id}/review")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> ReviewRun([FromRoute] string id, [FromBody] PipelineRunReview? review, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new SetRunReviewCommand(id, review), ct) is { } kept ? Ok(kept) : NotFound());

        [HttpPost("runs/{id}/cancel")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> Cancel(string id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new CancelRunCommand(id), ct) ? NoContent() : NotFound());

        /// <summary>BL-229: the run started again with its input, on the published version - the id of the new run.</summary>
        [HttpPost("runs/{id}/retry")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> Retry(string id, [FromQuery] bool onItsVersion, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new RetryRunCommand(id, onItsVersion), ct) is { } started ? Ok(started) : NotFound());

        /// <summary>BL-229: a failed run gone on from the step that failed, on the published version - the id of the new run.</summary>
        [HttpPost("runs/{id}/retry-from-failure")]
        [RequiresPermissionKey(PipelinesPermissions.Run)]
        public Task<IActionResult> RetryFromFailure(string id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new RetryFromFailedStepCommand(id), ct) is { } started ? Ok(started) : NotFound());

        [HttpDelete("runs/{id}")]
        [RequiresPermissionKey(PipelinesPermissions.Manage)]
        public Task<IActionResult> DeleteRun(string id, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DeleteRunCommand(id), ct) ? NoContent() : NotFound());

        /// <summary>BL-229: how the site's pipelines have been running over the last 1, 7 or 30 days (n8n's Insights).</summary>
        [HttpGet("insights")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<PipelineInsightsView> Insights([FromQuery] int days, CancellationToken ct) =>
            await _mediator.Send(new GetInsightsQuery(days), ct);

        /// <summary>BL-229: what runs next - the published pipelines' schedules and their next runs in the next 24 or 168 hours.</summary>
        [HttpGet("upcoming")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<PipelineUpcomingView> Upcoming([FromQuery] int hours, CancellationToken ct) =>
            await _mediator.Send(new ListUpcomingRunsQuery(hours), ct);

        /// <summary>BL-229: how each step of one pipeline has been doing over the last 1, 7 or 30 days.</summary>
        [HttpGet("{definitionId}/insights/steps")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<PipelineStepInsightsView> StepInsights(string definitionId, [FromQuery] int days, CancellationToken ct) =>
            await _mediator.Send(new GetStepInsightsQuery(definitionId, days), ct);

        /// <summary>BL-229: the runs waiting for a decision ("Ask for approval") - the site's, or one run's - oldest first.</summary>
        [HttpGet("approvals")]
        [RequiresPermissionKey(PipelinesPermissions.View)]
        public async Task<IReadOnlyList<PipelineApprovalView>> Approvals([FromQuery] string? runId, CancellationToken ct) =>
            await _mediator.Send(new ListApprovalsQuery(runId), ct);

        /// <summary>BL-229: approve or reject a run waiting for approval; 404 when it is decided already or not on this site.</summary>
        [HttpPost("approvals/{id}/decide")]
        [RequiresPermissionKey(PipelinesPermissions.Approve)]
        public Task<IActionResult> Decide(string id, [FromBody] ApprovalDecisionRequest request, CancellationToken ct) =>
            RunAsync(async () => await _mediator.Send(new DecideApprovalCommand(id, request?.Approve ?? false, request?.Comment), ct) is { } decided ? Ok(decided) : NotFound());

        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "The pipelines request was refused.");
            }
        }
    }

    /// <summary>
    /// The router's answer to a disabled module is 404: the actions do not exist unless the engine was composed. An action
    /// constraint on the CONTROLLER (the method-only selector attribute cannot sit there) covers every action at once.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class PipelinesEnabledAttribute : Attribute, Microsoft.AspNetCore.Mvc.ActionConstraints.IActionConstraint
    {
        public int Order => 0;

        public bool Accept(Microsoft.AspNetCore.Mvc.ActionConstraints.ActionConstraintContext context) =>
            context.RouteContext.HttpContext.RequestServices.GetService(typeof(ServerStartup.PipelinesComposed)) is not null;
    }
}
