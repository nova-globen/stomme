#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Triggers;

namespace Stomme.Modules.Pipelines.Audit
{
    /// <summary>What a command did, as the ledger says it; the pipeline and run it concerns when the command alone says so.</summary>
    public sealed record PipelineAuditFact(string Action, string? DefinitionId = null, string? RunId = null, string? Detail = null, string? PipelineName = null);

    /// <summary>
    /// What was read BEFORE a command ran: the pipeline it concerns and its name (a delete takes both away), and what a change
    /// replaces (the site's variables, compared by name and equality only - their values are never kept).
    /// </summary>
    public sealed record PipelineAuditBefore(string? DefinitionId, string? PipelineName, IReadOnlyDictionary<string, string>? Variables = null)
    {
        public static readonly PipelineAuditBefore Nothing = new(null, null);
    }

    /// <summary>
    /// What the pipeline activity ledger calls each of the module's commands. EVERY command of the module is in
    /// <see cref="Actions"/> (recorded) or <see cref="NotRecorded"/> (with why): <c>PipelineAuditCoverageTests</c> fails the
    /// build for a command in neither, so a new change cannot skip the ledger by being forgotten.
    /// </summary>
    public static class PipelineAudit
    {
        public static readonly IReadOnlyDictionary<Type, string> Actions = new Dictionary<Type, string>
        {
            [typeof(CreatePipelineCommand)] = "created the pipeline",
            [typeof(ImportPipelineCommand)] = "imported the pipeline",
            [typeof(UpdatePipelineCommand)] = "changed the pipeline",
            [typeof(SaveGraphCommand)] = "saved",
            [typeof(SaveGraphMergedCommand)] = "saved",
            [typeof(PublishPipelineCommand)] = "published",
            [typeof(RetractPipelineCommand)] = "retracted",
            [typeof(ArchivePipelineCommand)] = "archived",
            [typeof(RestorePipelineCommand)] = "restored from the archive",
            [typeof(DeletePipelineCommand)] = "deleted for good",
            [typeof(SetPipelineFolderCommand)] = "moved to a folder",
            [typeof(MovePipelineFolderCommand)] = "moved a folder",
            [typeof(SetPipelineTagsCommand)] = "changed the tags",
            [typeof(StartPipelineCommand)] = "started a run",
            [typeof(RetryRunCommand)] = "ran a run again",
            [typeof(RetryFromFailedStepCommand)] = "went on from a failed step",
            [typeof(CancelRunCommand)] = "stopped a run",
            [typeof(DeleteRunCommand)] = "deleted a run",
            [typeof(SetRunReviewCommand)] = "reviewed a run",
            [typeof(DecideApprovalCommand)] = "decided an approval",
            [typeof(RaisePipelineEventCommand)] = "raised an event",
            [typeof(StartEvaluationCommand)] = "started an evaluation",
            [typeof(CancelEvaluationCommand)] = "stopped an evaluation",
            [typeof(DeleteEvaluationCommand)] = "deleted an evaluation",
            [typeof(SetSiteVariablesCommand)] = "changed the site's variables",
            [typeof(CreateSiteTableCommand)] = "made a data table",
            [typeof(UpdateSiteTableCommand)] = "changed a data table",
            [typeof(DeleteSiteTableCommand)] = "deleted a data table",
            [typeof(AddTableRowCommand)] = "added a row to a data table",
            [typeof(UpdateTableRowCommand)] = "changed a row of a data table",
            [typeof(DeleteTableRowCommand)] = "deleted a row of a data table",
            [typeof(ImportTableCsvCommand)] = "imported rows into a data table",
            [typeof(UploadPipelineFileCommand)] = "uploaded a file",
            [typeof(CreatePipelineFolderCommand)] = "made a folder of files",
            [typeof(DeletePipelineFileCommand)] = "deleted a file",
            [typeof(Comments.AddStepCommentCommand)] = "commented on a step",
            [typeof(Comments.SetStepCommentResolvedCommand)] = "resolved a comment",
            [typeof(Comments.DeleteStepCommentCommand)] = "deleted a comment",
            [typeof(RenewWebhookSecretCommand)] = "made a new secret for a webhook",
            [typeof(RemoveWebhookSecretCommand)] = "removed a webhook's own secret",
        };

        public static readonly IReadOnlyDictionary<Type, string> NotRecorded = new Dictionary<Type, string>
        {
            [typeof(DesignPipelineGraphCommand)] = "the AI proposes a canvas and stores nothing; the author's save of it is recorded",
            [typeof(WriteScriptCommand)] = "the AI proposes a script and stores nothing; the author's save of it is recorded",
            [typeof(TestStepCommand)] = "a step tested on the canvas runs stored nowhere and changes nothing",
            [typeof(PreviewScriptCommand)] = "a JavaScript preview stores nothing and changes nothing",
            [typeof(ListenForWebhookCommand)] = "listens for one test call; nothing is stored and nothing runs",
        };

        /// <summary>
        /// What <paramref name="request"/> did, answered <paramref name="response"/>; null when it did nothing - nothing found
        /// (false or null), a publish the engine refused, a merge rejected, the variables saved as they were.
        /// </summary>
        public static PipelineAuditFact? Describe(object request, object? response, PipelineAuditBefore before)
        {
            if (!Actions.TryGetValue(request.GetType(), out var action) || response is null or false
                || response is PublishResult { Succeeded: false } || response is GraphSaveResult { Status: "Rejected" })
            {
                return null;
            }

            return request switch
            {
                CreatePipelineCommand => response is PipelineView made ? new(action, made.DefinitionId, PipelineName: made.Name) : null,
                ImportPipelineCommand => response is PipelineView imported ? new(action, imported.DefinitionId, PipelineName: imported.Name) : null,
                UpdatePipelineCommand update => new(action, update.DefinitionId, Detail: Changed(update, before), PipelineName: update.Name),
                SaveGraphMergedCommand merged => new(action, merged.DefinitionId,
                    Detail: response is GraphSaveResult { Status: "Merged" } ? "merged with a collaborator's changes" : null),
                PublishPipelineCommand publish => new(action, publish.DefinitionId,
                    Detail: string.IsNullOrWhiteSpace(publish.Note) ? null : "what changed: " + publish.Note.Trim()),
                SetPipelineFolderCommand moved => new(action, moved.DefinitionId,
                    Detail: response is string { Length: > 0 } folder ? "to " + folder : "to the top of the list"),
                MovePipelineFolderCommand folder => Folder(folder, response as PipelineFolderMoveResult),
                SetPipelineTagsCommand tags => new(action, tags.DefinitionId,
                    Detail: response is IReadOnlyList<string> { Count: > 0 } now ? "now: " + string.Join(", ", now) : "none now"),
                StartPipelineCommand start => new(action, start.DefinitionId, (response as PipelineRunStarted)?.RunId,
                    string.IsNullOrWhiteSpace(start.CorrelationId) ? null : "correlation id " + start.CorrelationId.Trim()),
                RetryRunCommand retry => new(action, before.DefinitionId, (response as PipelineRunStarted)?.RunId, "of run " + retry.Id + (retry.OnItsVersion ? ", on the version it ran" : "")),
                RetryFromFailedStepCommand onward => new(action, before.DefinitionId, (response as PipelineRunStarted)?.RunId, "of run " + onward.Id),
                CancelRunCommand cancel => new(action, before.DefinitionId, cancel.Id),
                DeleteRunCommand delete => new(action, before.DefinitionId, delete.Id),
                SetRunReviewCommand review => new(review.Review is null ? "cleared a run's review" : action, before.DefinitionId, review.RunId,
                    review.Review?.Rating is { Length: > 0 } rating ? "rated " + rating : null),
                DecideApprovalCommand decide => new(decide.Approve ? "approved a run's question" : "rejected a run's question", null,
                    (response as PipelineApprovalDecided)?.RunId),
                RaisePipelineEventCommand raised => new(action, Detail: "'" + raised.Name.Trim() + "'"
                    + (string.IsNullOrWhiteSpace(raised.CorrelationId) ? "" : ", correlation id " + raised.CorrelationId.Trim())),
                StartEvaluationCommand evaluation => new(action, evaluation.DefinitionId,
                    Detail: response is PipelineEvaluationView started ? Evaluated(started) : null),
                CancelEvaluationCommand => response is PipelineEvaluationView stopped ? new(action, stopped.DefinitionId, Detail: Evaluated(stopped)) : null,
                DeleteEvaluationCommand deleted => new(action, Detail: "evaluation " + deleted.Id),
                SetSiteVariablesCommand variables => Variables(action, before.Variables, response as IReadOnlyDictionary<string, string>),
                CreateSiteTableCommand => response is PipelineSiteTableView table ? new(action, Detail: Table(table)) : null,
                UpdateSiteTableCommand => response is PipelineSiteTableView table ? new(action, Detail: Table(table)) : null,
                DeleteSiteTableCommand table => new(action, Detail: "table " + table.Id),
                AddTableRowCommand row => new(action, Detail: "table " + row.Id),
                UpdateTableRowCommand row => new(action, Detail: "row " + row.RowId + " of table " + row.Id),
                DeleteTableRowCommand row => new(action, Detail: "row " + row.RowId + " of table " + row.Id),
                ImportTableCsvCommand rows => new(action, Detail: (response is PipelineTableImportResult result ? result.Imported + " rows into " : "into ") + "table " + rows.Id),
                // A file by its path in the site's pipeline folder - never its content.
                UploadPipelineFileCommand => response is PipelineFileEntryView uploaded ? new(action, Detail: "'" + uploaded.Path + "'") : null,
                CreatePipelineFolderCommand => response is PipelineFileEntryView made ? new(action, Detail: "'" + made.Path + "'") : null,
                DeletePipelineFileCommand deleted => new(action, Detail: "'" + PipelineFilePaths.Normalize(deleted.Path ?? "") + "'"),
                // A comment's step - by its name, as people know it - never its text: the ledger is read by others, and the text
                // is its author's.
                Comments.AddStepCommentCommand added => new(action, added.DefinitionId, Detail: OnStep(response as PipelineStepCommentView)),
                Comments.SetStepCommentResolvedCommand set => new(set.Resolved ? action : "reopened a comment", set.DefinitionId,
                    Detail: OnStep(response as PipelineStepCommentView)),
                Comments.DeleteStepCommentCommand removed => new(action, removed.DefinitionId, Detail: OnStep(response as PipelineStepCommentView)),
                // A webhook by its name - never its secret, which the answer carries and the ledger must not.
                RenewWebhookSecretCommand => new(action, Detail: "'" + (response as PipelineWebhookSecretView)?.Name + "'"),
                RemoveWebhookSecretCommand unset => new(action, Detail: "'" + Steps.Webhook.Normalize(unset.Name ?? "") + "'"),
                // Every other recorded command names its pipeline and says nothing more: save, retract, archive, restore, delete.
                _ => new(action, before.DefinitionId),
            };
        }

        private static string? Changed(UpdatePipelineCommand update, PipelineAuditBefore before)
        {
            var said = new List<string>();
            if (before.PipelineName is { } was && !string.Equals(was, update.Name?.Trim(), StringComparison.Ordinal))
            {
                said.Add($"renamed from '{was}'");
            }

            if (update.Body is not null)
            {
                said.Add("its body replaced");
            }

            return said.Count == 0 ? "its description" : string.Join("; ", said);
        }

        private static PipelineAuditFact Folder(MovePipelineFolderCommand folder, PipelineFolderMoveResult? result)
        {
            var moved = result is null ? "" : $" - {result.Moved} pipeline{(result.Moved == 1 ? "" : "s")}";
            return folder.Remove
                ? new("removed a folder", Detail: $"'{folder.Folder}', what was in it moved up{moved}")
                : new("moved a folder", Detail: $"'{folder.Folder}' to '{(string.IsNullOrEmpty(folder.NewFolder) ? "the top" : folder.NewFolder)}'{moved}");
        }

        private static string Evaluated(PipelineEvaluationView evaluation) =>
            "evaluation " + evaluation.Id + (string.IsNullOrWhiteSpace(evaluation.TableName) ? "" : " on table '" + evaluation.TableName + "'");

        private static string Table(PipelineSiteTableView table) => "'" + table.Name + "' (table " + table.Id + ")";

        // The step a comment is on: its name, or its id while it has none (a step not saved yet).
        private static string? OnStep(PipelineStepCommentView? comment) =>
            comment is null ? null : "on step '" + (string.IsNullOrWhiteSpace(comment.StepName) ? comment.StepId : comment.StepName) + "'";

        // The names added, changed and removed - never a value: a site variable may hold a key, and the ledger is read by others.
        private static PipelineAuditFact? Variables(string action, IReadOnlyDictionary<string, string>? was, IReadOnlyDictionary<string, string>? now)
        {
            was ??= new Dictionary<string, string>();
            now ??= new Dictionary<string, string>();
            var added = now.Keys.Where(name => !was.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var removed = was.Keys.Where(name => !now.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var changed = now.Keys.Where(name => was.TryGetValue(name, out var value) && !string.Equals(value, now[name], StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).ToList();
            var said = new[] { ("added", added), ("changed", changed), ("removed", removed) }.Where(part => part.Item2.Count > 0)
                .Select(part => part.Item1 + " " + string.Join(", ", part.Item2)).ToList();
            return said.Count == 0 ? null : new(action, Detail: string.Join("; ", said));
        }
    }
}
