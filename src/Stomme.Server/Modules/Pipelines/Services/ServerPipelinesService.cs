#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Services
{
    /// <summary>
    /// The server-side twin of the client service: the same contract, dispatched through the mediator (so the permission bridge
    /// decides). Deliberately NOT an <c>ITransientService</c>: that marker is what the convention scan registers on every
    /// host, and this service exists only where the engine was composed - the module startup registers it behind the
    /// switch (measured: with the marker, a stock host carried it as its one engine-module type).
    /// </summary>
    public sealed class ServerPipelinesService(IMediator mediator) : IPipelinesService
    {
        public async Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false) =>
            (await mediator.Send(new ListPipelinesQuery(withLastRuns))).ToList();

        public Task<PipelineDetail?> GetPipelineAsync(string definitionId) =>
            mediator.Send(new GetPipelineQuery(definitionId));

        public Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request) =>
            mediator.Send(new CreatePipelineCommand(request.Name, request.Description));

        public Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request) =>
            mediator.Send(new ImportPipelineCommand(request.Name, request.Description, request.Graph));

        public Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string? zone = null) =>
            mediator.Send(new GetTriggerInfoQuery(type, value, zone));

        public Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds) =>
            mediator.Send(new ListenForWebhookCommand(name, seconds));

        public Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox) =>
            mediator.Send(new FetchNewestEmailQuery(connection, mailbox));

        public Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name) =>
            mediator.Send(new RenewWebhookSecretCommand(name));

        public Task RemoveWebhookSecretAsync(string name) =>
            mediator.Send(new RemoveWebhookSecretCommand(name));

        public Task<PipelineCapabilitiesView> GetCapabilitiesAsync() =>
            mediator.Send(new GetCapabilitiesQuery());

        public Task<ScriptPreviewView> PreviewScriptAsync(ScriptPreviewRequest request) =>
            mediator.Send(new PreviewScriptCommand(request.Script ?? string.Empty,
                (request.Values ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => (string?)pair.Value, System.StringComparer.Ordinal)));

        public Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request) =>
            mediator.Send(new DesignPipelineGraphCommand(request?.Graph!, request?.Instruction ?? string.Empty, request?.Earlier));

        public Task<PipelineScriptView> WriteScriptAsync(PipelineScriptRequest request) =>
            mediator.Send(new WriteScriptCommand(request));

        public Task<PipelineScriptCheckView> CheckScriptsAsync(PipelineScriptCheckRequest request) =>
            mediator.Send(new CheckScriptsQuery(request));

        public async Task<Dictionary<string, int>> GetOpenCommentCountsAsync() =>
            new(await mediator.Send(new Comments.ListOpenCommentCountsQuery()), StringComparer.Ordinal);

        public async Task<List<PipelineMentionView>> GetMentionablePeopleAsync() =>
            (await mediator.Send(new Comments.ListMentionablePeopleQuery())).ToList();

        public async Task<List<PipelineStepCommentView>> GetStepCommentsAsync(string definitionId) =>
            (await mediator.Send(new Comments.ListStepCommentsQuery(definitionId))).ToList();

        public async Task<PipelineStepCommentView> AddStepCommentAsync(string definitionId, PipelineStepCommentRequest request) =>
            (await mediator.Send(new Comments.AddStepCommentCommand(definitionId, request?.StepId ?? string.Empty, request?.Text ?? string.Empty)))!;

        public async Task<PipelineStepCommentView> SetStepCommentResolvedAsync(string definitionId, Guid id, bool resolved) =>
            (await mediator.Send(new Comments.SetStepCommentResolvedCommand(definitionId, id, resolved)))!;

        public async Task<bool> DeleteStepCommentAsync(string definitionId, Guid id) =>
            await mediator.Send(new Comments.DeleteStepCommentCommand(definitionId, id)) is not null;

        public Task<PipelineView?> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request) =>
            mediator.Send(new UpdatePipelineCommand(definitionId, request.Name, request.Description, request.Body));

        public Task<PublishResult?> PublishPipelineAsync(string definitionId, string? note = null) =>
            mediator.Send(new PublishPipelineCommand(definitionId, note));

        public Task RetractPipelineAsync(string definitionId) =>
            mediator.Send(new RetractPipelineCommand(definitionId));

        public Task ArchivePipelineAsync(string definitionId) =>
            mediator.Send(new ArchivePipelineCommand(definitionId));

        public Task RestorePipelineAsync(string definitionId) =>
            mediator.Send(new RestorePipelineCommand(definitionId));

        public Task DeletePipelineAsync(string definitionId) =>
            mediator.Send(new DeletePipelineCommand(definitionId));

        public Task<Dictionary<string, string>> GetSiteVariablesAsync() =>
            mediator.Send(new GetSiteVariablesQuery());

        public Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables) =>
            mediator.Send(new SetSiteVariablesCommand(variables));

        public Task<List<PipelineSiteTableView>> GetSiteTablesAsync() =>
            mediator.Send(new ListSiteTablesQuery());

        public Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request) =>
            mediator.Send(new CreateSiteTableCommand(request));

        public Task<PipelineSiteTableView?> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request) =>
            mediator.Send(new UpdateSiteTableCommand(tableId, request));

        public Task DeleteSiteTableAsync(string tableId) =>
            mediator.Send(new DeleteSiteTableCommand(tableId));

        public Task<PipelineTableRowsView?> GetTableRowsAsync(string tableId, int skip, int take, string? search = null, string? sort = null, bool descending = false) =>
            mediator.Send(new GetTableRowsQuery(tableId, skip, take, search, sort, descending));

        public Task<Dictionary<string, string>?> AddTableRowAsync(string tableId, PipelineTableRowRequest request) =>
            mediator.Send(new AddTableRowCommand(tableId, request));

        public Task<Dictionary<string, string>?> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request) =>
            mediator.Send(new UpdateTableRowCommand(tableId, rowId, request));

        public Task DeleteTableRowAsync(string tableId, string rowId) =>
            mediator.Send(new DeleteTableRowCommand(tableId, rowId));

        public Task<PipelineRunReview?> SetRunReviewAsync(string runId, PipelineRunReview review) =>
            mediator.Send(new SetRunReviewCommand(runId, review));

        public Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId) =>
            mediator.Send(new ListEvaluationsQuery(definitionId));

        public Task<PipelineEvaluationView?> GetEvaluationAsync(string evaluationId) =>
            mediator.Send(new GetEvaluationQuery(evaluationId));

        public Task<PipelineEvaluationView?> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request) =>
            mediator.Send(new StartEvaluationCommand(definitionId, request));

        public Task<PipelineEvaluationView?> CancelEvaluationAsync(string evaluationId) =>
            mediator.Send(new CancelEvaluationCommand(evaluationId));

        public Task DeleteEvaluationAsync(string evaluationId) =>
            mediator.Send(new DeleteEvaluationCommand(evaluationId));

        public Task<PipelineTableCsv?> ExportTableCsvAsync(string tableId) =>
            mediator.Send(new ExportTableCsvQuery(tableId));

        public Task<PipelineTableImportResult?> ImportTableCsvAsync(string tableId, string csv) =>
            mediator.Send(new ImportTableCsvCommand(tableId, csv));

        public Task<PipelineFolderView?> GetPipelineFilesAsync(string folder) =>
            mediator.Send(new ListPipelineFilesQuery(folder));

        public Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content) =>
            mediator.Send(new UploadPipelineFileCommand(folder, name, content));

        public Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path) =>
            mediator.Send(new CreatePipelineFolderCommand(path));

        public Task<bool> DeletePipelineFileAsync(string path) =>
            mediator.Send(new DeletePipelineFileCommand(path));

        public async Task<List<AgentToolView>> GetAgentToolsAsync() =>
            (await mediator.Send(new ListAgentToolsQuery())).ToList();

        public async Task<List<string>> SetTagsAsync(string definitionId, List<string> tags) =>
            (await mediator.Send(new SetPipelineTagsCommand(definitionId, tags)))?.ToList() ?? new List<string>();

        public async Task<string> SetFolderAsync(string definitionId, string folder) =>
            await mediator.Send(new SetPipelineFolderCommand(definitionId, folder)) ?? "";

        public Task<PipelineMapView> GetMapAsync() => mediator.Send(new GetPipelineMapQuery());

        public async Task<List<PipelineAuditEntryView>> GetAuditAsync(string? definitionId = null, int take = PipelineAuditLimits.DefaultTake) =>
            (await mediator.Send(new Stomme.Modules.Pipelines.Audit.ListPipelineAuditQuery(definitionId, take))).ToList();

        public async Task<List<PipelineFoundView>> FindInPipelinesAsync(string text) =>
            (await mediator.Send(new FindInPipelinesQuery(text))).ToList();

        public async Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request) =>
            await mediator.Send(new MovePipelineFolderCommand(request?.Folder ?? "", request?.NewFolder, request?.Remove == true))!;

        public Task<PipelineRunStarted?> StartPipelineAsync(string definitionId, StartPipelineRequest request) =>
            mediator.Send(new StartPipelineCommand(definitionId, request?.CorrelationId, request?.Input));

        public async Task<List<PipelineRunView>> GetRunsAsync(string? definitionId = null, int take = 100, string? state = null, string? search = null, DateOnly? from = null, DateOnly? to = null) =>
            (await mediator.Send(new ListRunsQuery(definitionId, take, state, search, from, to))).ToList();

        public Task<PipelineRunView?> GetRunAsync(string runId) =>
            mediator.Send(new GetRunQuery(runId));

        public async Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId) =>
            (await mediator.Send(new GetRunLogQuery(runId))).ToList();

        public async Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId) =>
            (await mediator.Send(new ListRunStepsQuery(runId))).ToList();

        public async Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId) =>
            (await mediator.Send(new ListVersionsQuery(definitionId))).ToList();

        public Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version) =>
            mediator.Send(new GetVersionGraphQuery(definitionId, version))!;

        public Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request) =>
            mediator.Send(new TestStepCommand(request.Graph, request.StepId, request.Values, request.UpToHere, request.Whole, definitionId));

        public Task CancelRunAsync(string runId) =>
            mediator.Send(new CancelRunCommand(runId));

        public Task<PipelineRunStarted?> RetryRunAsync(string runId, bool onItsVersion = false) =>
            mediator.Send(new RetryRunCommand(runId, onItsVersion));

        public Task<PipelineRunStarted?> RetryFromFailedStepAsync(string runId) =>
            mediator.Send(new RetryFromFailedStepCommand(runId));

        public async Task<List<PipelineCallerView>> GetCallersAsync(string definitionId) =>
            (await mediator.Send(new ListCallersQuery(definitionId))).ToList();

        public Task DeleteRunAsync(string runId) =>
            mediator.Send(new DeleteRunCommand(runId));

        public Task<PipelineInsightsView> GetInsightsAsync(int days) =>
            mediator.Send(new GetInsightsQuery(days));

        public Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days) =>
            mediator.Send(new GetStepInsightsQuery(definitionId, days));

        public Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours) =>
            mediator.Send(new ListUpcomingRunsQuery(hours));

        public async Task<List<PipelineApprovalView>> GetApprovalsAsync(string? runId = null) =>
            (await mediator.Send(new ListApprovalsQuery(runId))).ToList();

        public Task<PipelineApprovalDecided?> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request) =>
            mediator.Send(new DecideApprovalCommand(approvalId, request?.Approve ?? false, request?.Comment));

        public async Task<List<StepCatalogEntry>> GetStepsAsync() =>
            (await mediator.Send(new ListStepsQuery())).ToList();

        public Task<PipelineGraph?> GetGraphAsync(string definitionId) =>
            mediator.Send(new GetGraphQuery(definitionId));

        public Task<PipelineView?> SaveGraphAsync(string definitionId, PipelineGraph graph) =>
            mediator.Send(new SaveGraphCommand(definitionId, graph));

        public Task<GraphSaveResult?> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded) =>
            mediator.Send(new SaveGraphMergedCommand(definitionId, graph, loaded));
    }
}
