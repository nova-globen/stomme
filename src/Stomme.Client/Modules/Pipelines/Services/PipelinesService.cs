using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Services
{
    /// <summary>
    /// The Pipelines module's service contract (BL-95 increment (b)). The client implementation calls the module's
    /// controller over HTTP; the server implementation dispatches the same requests through the mediator. Every call is
    /// decided by the server per permission key; a run is STARTED and then observed through <see cref="GetRunAsync"/>,
    /// never awaited to completion.
    /// </summary>
    public interface IPipelinesService
    {
        /// <summary>The site's pipelines by name; with each one's newest run when asked (the pipelines page).</summary>
        Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false);

        Task<PipelineDetail> GetPipelineAsync(string definitionId);

        Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request);

        /// <summary>BL-229 increment 10: a new pipeline with a body (a file, a starter); a refusal throws the server's reason.</summary>
        Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request);

        /// <summary>BL-229 increment 10: a webhook's address and signing, or a schedule's next runs.</summary>
        Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string zone = null);

        /// <summary>BL-229 increment 10: waits for the next call to one of the site's webhooks (at most two minutes).</summary>
        Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds);

        /// <summary>BL-229: the newest message in a mailbox of one of the site's connections, to test an Email received trigger's next steps with.</summary>
        Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox);

        /// <summary>D111a: a new secret of the webhook's own, replacing any it had - given out this once.</summary>
        Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name);

        /// <summary>D111a: removes the webhook's own secret, so it takes the installation's again.</summary>
        Task RemoveWebhookSecretAsync(string name);

        /// <summary>BL-229 increment 5b: what this site lets the editor offer.</summary>
        Task<PipelineCapabilitiesView> GetCapabilitiesAsync();

        /// <summary>BL-229 increment 5b: a JavaScript expression's value in the server's bounded sandbox.</summary>
        Task<ScriptPreviewView> PreviewScriptAsync(ScriptPreviewRequest request);

        /// <summary>
        /// n8n's AI workflow builder: the canvas changed by the site's model as the author asks - nothing stored - or its
        /// answer alone (no graph); when the model cannot be asked or made what the save refuses, throws the reason.
        /// </summary>
        Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request);

        /// <summary>
        /// n8n's "Ask AI" in its Code node: a Run script step's script written by the site's model as the author asks -
        /// nothing stored, nothing run; when the model cannot be asked or scripts are off, throws the reason.
        /// </summary>
        Task<PipelineScriptView> WriteScriptAsync(PipelineScriptRequest request);

        /// <summary>BL-229 - n8n's Code node linting: why each script does not parse as its step runs it, in order (null: it does).</summary>
        Task<PipelineScriptCheckView> CheckScriptsAsync(PipelineScriptCheckRequest request);

        /// <summary>BL-229 - comments on a pipeline's steps, oldest first.</summary>
        Task<List<PipelineStepCommentView>> GetStepCommentsAsync(string definitionId);

        /// <summary>BL-229: the people a comment may mention - the site's people who can see its pipelines.</summary>
        Task<List<PipelineMentionView>> GetMentionablePeopleAsync();

        /// <summary>BL-229: how many comments each pipeline has open, by definition id - none listed when 0.</summary>
        Task<Dictionary<string, int>> GetOpenCommentCountsAsync();

        /// <summary>BL-229: a comment added to a step; null when the site has no such pipeline (a refusal throws the server's reason).</summary>
        Task<PipelineStepCommentView> AddStepCommentAsync(string definitionId, PipelineStepCommentRequest request);

        /// <summary>BL-229: a comment dealt with, or open again; null when there is no such comment.</summary>
        Task<PipelineStepCommentView> SetStepCommentResolvedAsync(string definitionId, Guid id, bool resolved);

        /// <summary>BL-229: a comment deleted; false when there is no such comment.</summary>
        Task<bool> DeleteStepCommentAsync(string definitionId, Guid id);

        Task<PipelineView> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request);

        /// <summary>Publishes the latest version - with what changed in the author's words, when given.</summary>
        Task<PublishResult> PublishPipelineAsync(string definitionId, string note = null);

        Task RetractPipelineAsync(string definitionId);

        /// <summary>BL-229 - n8n's archive: the pipeline set aside, unpublished; and back as a draft.</summary>
        Task ArchivePipelineAsync(string definitionId);

        Task RestorePipelineAsync(string definitionId);

        Task DeletePipelineAsync(string definitionId);

        /// <summary>BL-229: replaces the pipeline's tags (an empty list clears them) and answers the tags kept; a tag that cannot be one throws the reason.</summary>
        Task<List<string>> SetTagsAsync(string definitionId, List<string> tags);

        /// <summary>BL-229 - n8n's folders: moves the pipeline to a folder (empty: the top) and answers the folder kept; one that cannot be a folder throws the reason.</summary>
        Task<string> SetFolderAsync(string definitionId, string folder);

        /// <summary>BL-229 - n8n's folder rename, move and delete: the folder becomes another path, or is removed with what was in it going up; a move that cannot be throws the reason, a folder no pipeline is in answers null.</summary>
        Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request);

        /// <summary>BL-229 - find in every pipeline: where a text is used in the site's pipelines (a sensitive input never read).</summary>
        Task<List<PipelineFoundView>> FindInPipelinesAsync(string text);

        /// <summary>BL-229 - the map of the site's pipelines: which runs which.</summary>
        Task<PipelineMapView> GetMapAsync();

        /// <summary>BL-229 - the site's pipeline activity, newest first: all of it, or one pipeline's (n8n's audit log). Authors only.</summary>
        Task<List<PipelineAuditEntryView>> GetAuditAsync(string definitionId = null, int take = PipelineAuditLimits.DefaultTake);

        /// <summary>BL-229: the site's published AI agent tools.</summary>
        Task<List<AgentToolView>> GetAgentToolsAsync();

        /// <summary>BL-229: the site's variables (<c>$vars</c>).</summary>
        Task<Dictionary<string, string>> GetSiteVariablesAsync();

        /// <summary>BL-229: replaces the site's variables; a name or value that cannot be one throws the reason.</summary>
        Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables);

        /// <summary>BL-229 - n8n's Data tables: the site's tables, by name.</summary>
        Task<List<PipelineSiteTableView>> GetSiteTablesAsync();

        /// <summary>BL-229: makes a data table; a name or a column that cannot be one throws the reason.</summary>
        Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request);

        /// <summary>BL-229: renames a data table or changes its columns; null when there is no such table.</summary>
        Task<PipelineSiteTableView> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request);

        /// <summary>BL-229: deletes a data table with its rows.</summary>
        Task DeleteSiteTableAsync(string tableId);

        /// <summary>BL-229: a page of a data table's rows, oldest first; null when there is no such table.</summary>
        Task<PipelineTableRowsView> GetTableRowsAsync(string tableId, int skip, int take, string search = null, string sort = null, bool descending = false);

        /// <summary>BL-229: adds a row by hand and answers it; a value that does not fit its column throws the reason.</summary>
        Task<Dictionary<string, string>> AddTableRowAsync(string tableId, PipelineTableRowRequest request);

        /// <summary>BL-229: changes a row's values by hand and answers it.</summary>
        Task<Dictionary<string, string>> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request);

        /// <summary>BL-229: deletes a row by hand.</summary>
        Task DeleteTableRowAsync(string tableId, string rowId);

        /// <summary>BL-229 - n8n's execution annotations: says how a run went and answers the review kept; past the rules it throws the reason.</summary>
        Task<PipelineRunReview> SetRunReviewAsync(string runId, PipelineRunReview review);

        /// <summary>BL-229 - n8n's Evaluations: a pipeline's evaluations, newest first (counts and averages, no rows).</summary>
        Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId);

        /// <summary>BL-229: one evaluation with every row; null when there is no such evaluation.</summary>
        Task<PipelineEvaluationView> GetEvaluationAsync(string evaluationId);

        /// <summary>BL-229: starts an evaluation over a data table's rows; when it cannot, throws the reason.</summary>
        Task<PipelineEvaluationView> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request);

        /// <summary>BL-229: stops a running evaluation.</summary>
        Task<PipelineEvaluationView> CancelEvaluationAsync(string evaluationId);

        /// <summary>BL-229: deletes an evaluation that has ended.</summary>
        Task DeleteEvaluationAsync(string evaluationId);

        /// <summary>BL-229: a data table's rows as CSV; null when there is no such table.</summary>
        Task<PipelineTableCsv> ExportTableCsvAsync(string tableId);

        /// <summary>BL-229: adds a CSV's rows to a data table, all or nothing; a value that does not fit throws the reason.</summary>
        Task<PipelineTableImportResult> ImportTableCsvAsync(string tableId, string csv);

        /// <summary>BL-229: a folder of the site's pipeline folder - what the file steps read and write; null when there is none.</summary>
        Task<PipelineFolderView> GetPipelineFilesAsync(string folder);

        /// <summary>BL-229: a file taken into a folder of the site's pipeline folder; a refusal throws its reason.</summary>
        Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content);

        /// <summary>BL-229: a folder made in the site's pipeline folder; a refusal throws its reason.</summary>
        Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path);

        /// <summary>BL-229: a file, or a folder with what is in it, deleted from the site's pipeline folder; false when there is none.</summary>
        Task<bool> DeletePipelineFileAsync(string path);

        Task<PipelineRunStarted> StartPipelineAsync(string definitionId, StartPipelineRequest request);

        /// <summary>The site's runs, newest first: one pipeline's or every one's, in one of <see cref="PipelineRunStates"/> or any.</summary>
        /// <summary>The site's runs, newest first; <paramref name="from"/> and <paramref name="to"/> are whole days in UTC, both kept (n8n's start-date filter).</summary>
        Task<List<PipelineRunView>> GetRunsAsync(string definitionId = null, int take = 100, string state = null, string search = null, DateOnly? from = null, DateOnly? to = null);

        Task<PipelineRunView> GetRunAsync(string runId);

        Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId);

        /// <summary>BL-229: each step execution of a run, in start order; inputs and outputs only for an author.</summary>
        Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId);

        /// <summary>BL-229 increment 6: test one step of the graph, on its own and for real; nothing is stored.</summary>
        Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request);

        /// <summary>BL-229 increment 7: the pipeline's versions, newest first.</summary>
        Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId);

        /// <summary>BL-229 increment 7: the graph of one version.</summary>
        Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version);

        Task CancelRunAsync(string runId);

        /// <summary>BL-229: the run started again with its input, on the published version (n8n's retry).</summary>
        Task<PipelineRunStarted> RetryRunAsync(string runId, bool onItsVersion = false);

        /// <summary>BL-229: a failed run gone on from the step that failed, on the published version, what had finished not run
        /// again (n8n's retry from the node with the error).</summary>
        Task<PipelineRunStarted> RetryFromFailedStepAsync(string runId);

        /// <summary>BL-229: the pipelines that call this one (Execute or Dispatch pipeline).</summary>
        Task<List<PipelineCallerView>> GetCallersAsync(string definitionId);

        /// <summary>BL-229: the runs waiting for a decision ("Ask for approval") - the site's, or one run's - oldest first.</summary>
        Task<List<PipelineApprovalView>> GetApprovalsAsync(string runId = null);

        /// <summary>BL-229: how the site's pipelines have been running over the last 1, 7 or 30 days (n8n's Insights).</summary>
        Task<PipelineInsightsView> GetInsightsAsync(int days);

        /// <summary>BL-229: how each step of one pipeline has been doing over the last 1, 7 or 30 days (beyond n8n's Insights).</summary>
        Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days);

        /// <summary>BL-229: what runs next - the published pipelines' schedules and their next runs in the next 24 or 168 hours.</summary>
        Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours);

        /// <summary>BL-229: approve or reject a waiting run; null when it was decided already (by someone else) or is gone.</summary>
        Task<PipelineApprovalDecided> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request);

        Task DeleteRunAsync(string runId);

        /// <summary>The step types this installation offers: the engine's registry, browsable entries only.</summary>
        Task<List<StepCatalogEntry>> GetStepsAsync();

        Task<PipelineGraph> GetGraphAsync(string definitionId);

        Task<PipelineView> SaveGraphAsync(string definitionId, PipelineGraph graph);

        /// <summary>
        /// The collaborating save: the graph and the graph it was based on. A rejection is an answer,
        /// not a failure - its conflicts and the graph stored now come back; null when the pipeline is gone.
        /// </summary>
        Task<GraphSaveResult> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded);
    }

    public class PipelinesService : ServiceBase, IPipelinesService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public PipelinesService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Pipelines");

        public async Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false) =>
            await GetJsonAsync<List<PipelineView>>(withLastRuns ? ApiUrl + "?lastRuns=true" : ApiUrl) ?? new List<PipelineView>();

        public Task<PipelineDetail> GetPipelineAsync(string definitionId) =>
            GetJsonAsync<PipelineDetail>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}");

        public Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request) =>
            PostJsonAsync<CreatePipelineRequest, PipelineView>(ApiUrl, request);

        public Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request) =>
            PostJsonAsync<ImportPipelineRequest, PipelineView>($"{ApiUrl}/import", request);

        public Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string zone = null) =>
            GetJsonAsync<TriggerInfoView>($"{ApiUrl}/triggers/info?type={Uri.EscapeDataString(type ?? "")}&value={Uri.EscapeDataString(value ?? "")}&zone={Uri.EscapeDataString(zone ?? "")}");

        public Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox) =>
            GetJsonAsync<PipelineTestEventView>($"{ApiUrl}/triggers/email/newest?connection={Uri.EscapeDataString(connection ?? "")}&mailbox={Uri.EscapeDataString(mailbox ?? "")}");

        public Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds) =>
            PostJsonAsync<object, WebhookCallView>($"{ApiUrl}/triggers/webhooks/{Uri.EscapeDataString(name ?? "")}/listen?seconds={seconds}", new { });

        public Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name) =>
            PostJsonAsync<object, PipelineWebhookSecretView>($"{ApiUrl}/triggers/webhooks/{Uri.EscapeDataString(name ?? "")}/secret", new { });

        public Task RemoveWebhookSecretAsync(string name) =>
            DeleteAsync($"{ApiUrl}/triggers/webhooks/{Uri.EscapeDataString(name ?? "")}/secret");

        public Task<PipelineCapabilitiesView> GetCapabilitiesAsync() =>
            GetJsonAsync<PipelineCapabilitiesView>($"{ApiUrl}/capabilities");

        public Task<ScriptPreviewView> PreviewScriptAsync(ScriptPreviewRequest request) =>
            PostJsonAsync<ScriptPreviewRequest, ScriptPreviewView>($"{ApiUrl}/expressions/script/preview", request);

        public Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request) =>
            PostJsonAsync<PipelineGraphDesignRequest, PipelineGraphDesignView>($"{ApiUrl}/design", request);

        public Task<PipelineScriptView> WriteScriptAsync(PipelineScriptRequest request) =>
            PostJsonAsync<PipelineScriptRequest, PipelineScriptView>($"{ApiUrl}/script", request);

        public Task<PipelineScriptCheckView> CheckScriptsAsync(PipelineScriptCheckRequest request) =>
            PostJsonAsync<PipelineScriptCheckRequest, PipelineScriptCheckView>($"{ApiUrl}/scripts/check", request);

        public async Task<Dictionary<string, int>> GetOpenCommentCountsAsync() =>
            await GetJsonAsync<Dictionary<string, int>>($"{ApiUrl}/comments/open") ?? new Dictionary<string, int>();

        public async Task<List<PipelineMentionView>> GetMentionablePeopleAsync() =>
            await GetJsonAsync<List<PipelineMentionView>>($"{ApiUrl}/comments/people") ?? new List<PipelineMentionView>();

        public async Task<List<PipelineStepCommentView>> GetStepCommentsAsync(string definitionId) =>
            await GetJsonAsync<List<PipelineStepCommentView>>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/comments") ?? new List<PipelineStepCommentView>();

        // A comment's 404 is an answer - the pipeline or the comment is gone (someone else deleted it) - not a refusal.
        public async Task<PipelineStepCommentView> AddStepCommentAsync(string definitionId, PipelineStepCommentRequest request)
        {
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/comments", request ?? new PipelineStepCommentRequest());
            return await CommentAnswerAsync(response);
        }

        public async Task<PipelineStepCommentView> SetStepCommentResolvedAsync(string definitionId, Guid id, bool resolved)
        {
            using var response = await GetHttpClient().PutAsJsonAsync(
                $"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/comments/{id}/resolved?resolved={(resolved ? "true" : "false")}", new { });
            return await CommentAnswerAsync(response);
        }

        public async Task<bool> DeleteStepCommentAsync(string definitionId, Guid id)
        {
            using var response = await GetHttpClient().DeleteAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/comments/{id}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return true;
        }

        private static async Task<PipelineStepCommentView> CommentAnswerAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<PipelineStepCommentView>();
        }

        public Task<PipelineView> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request) =>
            PutJsonAsync<UpdatePipelineRequest, PipelineView>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}", request);

        public Task<PublishResult> PublishPipelineAsync(string definitionId, string note = null) =>
            PostJsonAsync<PublishPipelineRequest, PublishResult>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/publish", new PublishPipelineRequest { Note = note });

        public Task RetractPipelineAsync(string definitionId) =>
            PostAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/retract");

        public Task ArchivePipelineAsync(string definitionId) =>
            PostAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/archive");

        public Task RestorePipelineAsync(string definitionId) =>
            PostAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/restore");

        public Task DeletePipelineAsync(string definitionId) =>
            DeleteAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}");

        public Task<List<string>> SetTagsAsync(string definitionId, List<string> tags) =>
            PutJsonAsync<List<string>, List<string>>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/tags", tags ?? new List<string>());

        public async Task<string> SetFolderAsync(string definitionId, string folder) =>
            (await PutJsonAsync<PipelineFolderRequest, PipelineFolderRequest>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/folder", new PipelineFolderRequest { Folder = folder ?? "" }))?.Folder ?? "";

        public Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request) =>
            PutJsonAsync<PipelineFolderMoveRequest, PipelineFolderMoveResult>($"{ApiUrl}/folders", request);

        public async Task<PipelineMapView> GetMapAsync() =>
            await GetJsonAsync<PipelineMapView>($"{ApiUrl}/map") ?? new PipelineMapView();

        public async Task<List<PipelineAuditEntryView>> GetAuditAsync(string definitionId = null, int take = PipelineAuditLimits.DefaultTake) =>
            await GetJsonAsync<List<PipelineAuditEntryView>>($"{ApiUrl}/audit?definitionId={Uri.EscapeDataString(definitionId ?? string.Empty)}&take={take}") ?? new List<PipelineAuditEntryView>();

        public async Task<List<PipelineFoundView>> FindInPipelinesAsync(string text) =>
            await GetJsonAsync<List<PipelineFoundView>>($"{ApiUrl}/find?text={Uri.EscapeDataString(text ?? "")}") ?? new List<PipelineFoundView>();

        public async Task<List<AgentToolView>> GetAgentToolsAsync() =>
            await GetJsonAsync<List<AgentToolView>>($"{ApiUrl}/agent-tools") ?? new List<AgentToolView>();

        public async Task<Dictionary<string, string>> GetSiteVariablesAsync() =>
            await GetJsonAsync<Dictionary<string, string>>($"{ApiUrl}/variables") ?? new Dictionary<string, string>();

        public Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables) =>
            PutJsonAsync<Dictionary<string, string>, Dictionary<string, string>>($"{ApiUrl}/variables", variables ?? new Dictionary<string, string>());

        public async Task<List<PipelineSiteTableView>> GetSiteTablesAsync() =>
            await GetJsonAsync<List<PipelineSiteTableView>>($"{ApiUrl}/tables") ?? new List<PipelineSiteTableView>();

        public Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request) =>
            PostJsonAsync<PipelineSiteTableRequest, PipelineSiteTableView>($"{ApiUrl}/tables", request ?? new PipelineSiteTableRequest());

        public Task<PipelineSiteTableView> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request) =>
            PutJsonAsync<PipelineSiteTableRequest, PipelineSiteTableView>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}", request ?? new PipelineSiteTableRequest());

        public Task DeleteSiteTableAsync(string tableId) =>
            DeleteAsync($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}");

        public Task<PipelineTableRowsView> GetTableRowsAsync(string tableId, int skip, int take, string search = null, string sort = null, bool descending = false) =>
            GetJsonAsync<PipelineTableRowsView>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/rows?skip={skip}&take={take}&search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort ?? "")}&descending={(descending ? "true" : "false")}");

        public Task<Dictionary<string, string>> AddTableRowAsync(string tableId, PipelineTableRowRequest request) =>
            PostJsonAsync<PipelineTableRowRequest, Dictionary<string, string>>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/rows", request ?? new PipelineTableRowRequest());

        public Task<Dictionary<string, string>> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request) =>
            PutJsonAsync<PipelineTableRowRequest, Dictionary<string, string>>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/rows/{Uri.EscapeDataString(rowId)}", request ?? new PipelineTableRowRequest());

        public Task DeleteTableRowAsync(string tableId, string rowId) =>
            DeleteAsync($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/rows/{Uri.EscapeDataString(rowId)}");

        public Task<PipelineRunReview> SetRunReviewAsync(string runId, PipelineRunReview review) =>
            PutJsonAsync<PipelineRunReview, PipelineRunReview>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/review", review ?? new PipelineRunReview());

        public async Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId) =>
            await GetJsonAsync<List<PipelineEvaluationView>>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/evaluations") ?? new List<PipelineEvaluationView>();

        public Task<PipelineEvaluationView> GetEvaluationAsync(string evaluationId) =>
            GetJsonAsync<PipelineEvaluationView>($"{ApiUrl}/evaluations/{Uri.EscapeDataString(evaluationId)}");

        public Task<PipelineEvaluationView> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request) =>
            PostJsonAsync<PipelineEvaluationRequest, PipelineEvaluationView>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/evaluations", request ?? new PipelineEvaluationRequest());

        public Task<PipelineEvaluationView> CancelEvaluationAsync(string evaluationId) =>
            PostJsonAsync<object, PipelineEvaluationView>($"{ApiUrl}/evaluations/{Uri.EscapeDataString(evaluationId)}/cancel", new { });

        public Task DeleteEvaluationAsync(string evaluationId) =>
            DeleteAsync($"{ApiUrl}/evaluations/{Uri.EscapeDataString(evaluationId)}");

        public Task<PipelineTableCsv> ExportTableCsvAsync(string tableId) =>
            GetJsonAsync<PipelineTableCsv>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/csv");

        public Task<PipelineTableImportResult> ImportTableCsvAsync(string tableId, string csv) =>
            PostJsonAsync<PipelineTableImportRequest, PipelineTableImportResult>($"{ApiUrl}/tables/{Uri.EscapeDataString(tableId)}/csv", new PipelineTableImportRequest { Csv = csv ?? "" });

        public Task<PipelineFolderView> GetPipelineFilesAsync(string folder) =>
            GetJsonAsync<PipelineFolderView>($"{ApiUrl}/files?folder={Uri.EscapeDataString(folder ?? "")}");

        public async Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content)
        {
            using var form = new MultipartFormDataContent { { new StreamContent(content), "file", name ?? "" } };
            using var response = await GetHttpClient().PostAsync($"{ApiUrl}/files?folder={Uri.EscapeDataString(folder ?? "")}", form);
            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<PipelineFileEntryView>();
        }

        public Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path) =>
            PostJsonAsync<PipelineFileFolderRequest, PipelineFileEntryView>($"{ApiUrl}/files/folders", new PipelineFileFolderRequest { Path = path ?? "" });

        public async Task<bool> DeletePipelineFileAsync(string path)
        {
            using var response = await GetHttpClient().DeleteAsync($"{ApiUrl}/files?path={Uri.EscapeDataString(path ?? "")}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return true;
        }

        public Task<PipelineRunStarted> StartPipelineAsync(string definitionId, StartPipelineRequest request) =>
            PostJsonAsync<StartPipelineRequest, PipelineRunStarted>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/runs", request ?? new StartPipelineRequest());

        public async Task<List<PipelineRunView>> GetRunsAsync(string definitionId = null, int take = 100, string state = null, string search = null, DateOnly? from = null, DateOnly? to = null) =>
            await GetJsonAsync<List<PipelineRunView>>($"{ApiUrl}/runs?definitionId={Uri.EscapeDataString(definitionId ?? string.Empty)}&take={take}&state={Uri.EscapeDataString(state ?? string.Empty)}&search={Uri.EscapeDataString(search ?? string.Empty)}"
                + (from is { } first ? "&from=" + first.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "")
                + (to is { } last ? "&to=" + last.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : ""))
            ?? new List<PipelineRunView>();

        public Task<PipelineRunView> GetRunAsync(string runId) =>
            GetJsonAsync<PipelineRunView>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}");

        public async Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId) =>
            await GetJsonAsync<List<PipelineRunLogLine>>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/log") ?? new List<PipelineRunLogLine>();

        public async Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId) =>
            await GetJsonAsync<List<PipelineStepRunView>>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/steps") ?? new List<PipelineStepRunView>();

        public Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request) =>
            PostJsonAsync<StepTestRequest, StepTestResult>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/test-step", request);

        public async Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId) =>
            await GetJsonAsync<List<PipelineVersionView>>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/versions") ?? new List<PipelineVersionView>();

        public Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version) =>
            GetJsonAsync<PipelineGraph>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/versions/{version}/graph");

        public Task CancelRunAsync(string runId) =>
            PostAsync($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/cancel");

        public Task<PipelineRunStarted> RetryRunAsync(string runId, bool onItsVersion = false) =>
            PostJsonAsync<object, PipelineRunStarted>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/retry" + (onItsVersion ? "?onItsVersion=true" : ""), new { });

        public Task<PipelineRunStarted> RetryFromFailedStepAsync(string runId) =>
            PostJsonAsync<object, PipelineRunStarted>($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}/retry-from-failure", new { });

        public Task<List<PipelineCallerView>> GetCallersAsync(string definitionId) =>
            GetJsonAsync<List<PipelineCallerView>>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/callers");

        public Task DeleteRunAsync(string runId) =>
            DeleteAsync($"{ApiUrl}/runs/{Uri.EscapeDataString(runId)}");

        public Task<PipelineInsightsView> GetInsightsAsync(int days) =>
            GetJsonAsync<PipelineInsightsView>($"{ApiUrl}/insights?days={days}");

        public Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days) =>
            GetJsonAsync<PipelineStepInsightsView>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/insights/steps?days={days}");

        public Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours) =>
            GetJsonAsync<PipelineUpcomingView>($"{ApiUrl}/upcoming?hours={hours}");

        public async Task<List<PipelineApprovalView>> GetApprovalsAsync(string runId = null) =>
            await GetJsonAsync<List<PipelineApprovalView>>($"{ApiUrl}/approvals?runId={Uri.EscapeDataString(runId ?? string.Empty)}") ?? new List<PipelineApprovalView>();

        public async Task<PipelineApprovalDecided> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request)
        {
            // 404 is an answer here - decided already, by someone else - not a refusal.
            using var response = await GetHttpClient().PostAsJsonAsync($"{ApiUrl}/approvals/{Uri.EscapeDataString(approvalId)}/decide", request ?? new ApprovalDecisionRequest());
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceRefusalException(await ReadRefusalReasonAsync(response), response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<PipelineApprovalDecided>();
        }

        public async Task<List<StepCatalogEntry>> GetStepsAsync() =>
            await GetJsonAsync<List<StepCatalogEntry>>($"{ApiUrl}/steps") ?? new List<StepCatalogEntry>();

        public Task<PipelineGraph> GetGraphAsync(string definitionId) =>
            GetJsonAsync<PipelineGraph>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/graph");

        public Task<PipelineView> SaveGraphAsync(string definitionId, PipelineGraph graph) =>
            PutJsonAsync<PipelineGraph, PipelineView>($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/graph", graph);

        public async Task<GraphSaveResult> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded)
        {
            using var response = await GetHttpClient().PutAsJsonAsync($"{ApiUrl}/{Uri.EscapeDataString(definitionId)}/graph/merge", new GraphSaveRequest { Graph = graph, Base = loaded });
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            // 200 (applied or merged) and 409 (rejected) both carry the result; anything else is a refusal of the graph itself.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                return await response.Content.ReadFromJsonAsync<GraphSaveResult>();
            }

            throw new HttpRequestException($"The pipeline was refused ({(int)response.StatusCode}).", null, response.StatusCode);
        }
    }
}
