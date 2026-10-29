#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// n8n's Evaluations, run: each site's running evaluations moved on, per site tenant, on one node at a time, every
    /// <c>Pipelines:Evaluations:IntervalSeconds</c>. A row whose run has ended is marked by how it ended (finished, failed,
    /// cancelled); one running past <c>RowTimeoutMinutes</c> is cancelled and marked timed out; then waiting rows are started, up
    /// to <c>RowsAtOnce</c>, each a run of the version the evaluation began with, from its "When evaluating" step with the row as
    /// input. A start the pipeline's "runs at once" setting refuses waits for the next tick. With no row left, the evaluation is
    /// finished. A run is stored only when its burst of work ends, so a row's run not found yet is still going.
    /// </summary>
    [SingleNodeTask]
    public sealed class PipelineEvaluationTask(
        IKeyValueStore keyValues,
        IPipelineDefinitionStore definitions,
        IPipelineInstanceStore instances,
        IPipelineDispatcher dispatcher,
        IPipelineRuntime runtime,
        IIdentityGenerator identities,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        IOptions<PipelineTriggerOptions> options,
        ILogger<PipelineEvaluationTask> logger,
        Reliability.PipelineRunStops stops) : RecurringTask
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var tenantId = tenantAccessor.TenantId;
            foreach (var listed in (await PipelineEvaluationStore.ListAsync(keyValues, tenantId, cancellationToken).ConfigureAwait(false))
                         .Where(each => each.State == PipelineEvaluations.Running).OrderBy(each => each.StartedOn))
            {
                try
                {
                    await AdvanceAsync(listed.Id, tenantId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The evaluation {Evaluation} on tenant {Tenant} could not be moved on; it is tried again at the next tick.", listed.Id, tenantId);
                }
            }
        }

        private async Task AdvanceAsync(string id, string? tenantId, CancellationToken ct)
        {
            // Read again just before the work: a cancel between the list and here must not be undone by this save.
            var evaluation = await PipelineEvaluationStore.FindAsync(keyValues, tenantId, id, ct).ConfigureAwait(false);
            if (evaluation is null || evaluation.State != PipelineEvaluations.Running)
            {
                return;
            }

            var settings = options.Value.Evaluations;
            var now = clock.UtcNow;
            var changed = false;
            foreach (var row in evaluation.Rows.Where(row => row.State == PipelineEvaluations.RowRunning))
            {
                changed |= await SettleAsync(row, now, TimeSpan.FromMinutes(Math.Max(1, settings.RowTimeoutMinutes)), ct).ConfigureAwait(false);
            }

            var room = Math.Clamp(settings.RowsAtOnce, 1, 10) - evaluation.Rows.Count(row => row.State == PipelineEvaluations.RowRunning);
            var version = room > 0 && evaluation.Rows.Any(row => row.State == PipelineEvaluations.Waiting)
                ? await definitions.FindAsync(new PipelineDefinitionFilter { Id = evaluation.DefinitionVersionId }, ct).ConfigureAwait(false)
                : null;
            foreach (var row in evaluation.Rows.Where(row => row.State == PipelineEvaluations.Waiting).Take(Math.Max(0, room)).ToList())
            {
                if (version is null)
                {
                    row.State = PipelineEvaluations.RowCancelled;
                    row.Error = "The pipeline version the evaluation began with is gone.";
                    changed = true;
                    continue;
                }

                if (await PipelineRunPolicy.RefusalAsync(instances, version, PipelineEvaluationRuns.CorrelationOf(evaluation), ct).ConfigureAwait(false) is not null)
                {
                    break; // the pipeline runs one at a time and a run of it is going: the next tick tries again
                }

                var runId = identities.GenerateId();
                var response = await dispatcher.DispatchAsync(new DispatchPipelineDefinitionRequest(version.Id)
                {
                    InstanceId = runId,
                    CorrelationId = PipelineEvaluationRuns.CorrelationOf(evaluation),
                    TriggerStepId = evaluation.TriggerStepId,
                    Input = PipelineEvaluationRuns.InputOf(evaluation, row),
                }, options: null, ct).ConfigureAwait(false);
                if (response.Fault is not null)
                {
                    row.State = PipelineEvaluations.RowFailed;
                    row.Error = "The run could not be dispatched: " + response.Fault.Message;
                }
                else
                {
                    row.State = PipelineEvaluations.RowRunning;
                    row.RunId = runId;
                    row.StartedOn = now;
                }

                changed = true;
            }

            if (evaluation.Rows.All(row => PipelineEvaluations.IsDone(row.State)))
            {
                evaluation.State = PipelineEvaluations.Finished;
                evaluation.FinishedOn = now;
                changed = true;
                logger.LogInformation("The evaluation {Evaluation} of '{Pipeline}' finished: {Rows} row(s).", evaluation.Id, evaluation.PipelineName, evaluation.Rows.Count);
            }

            if (changed)
            {
                await PipelineEvaluationStore.SaveAsync(keyValues, tenantId, evaluation, ct).ConfigureAwait(false);
            }
        }

        // A running row: marked by how its run ended, or cancelled once past its time; whether it changed.
        private async Task<bool> SettleAsync(StoredEvaluationRow row, DateTimeOffset now, TimeSpan timeout, CancellationToken ct)
        {
            var run = row.RunId is null ? null : await instances.FindAsync(new PipelineInstanceFilter { Id = row.RunId }, ct).ConfigureAwait(false);
            if (run is { Status: PipelineStatus.Finished })
            {
                (row.State, row.Error) = run.SubStatus switch
                {
                    PipelineSubStatus.Faulted => (PipelineEvaluations.RowFailed, run.PipelineState?.Incidents?.FirstOrDefault()?.Message ?? "The run failed."),
                    PipelineSubStatus.Cancelled => (PipelineEvaluations.RowCancelled, (string?)"The run was cancelled."),
                    _ => (PipelineEvaluations.RowFinished, (string?)null),
                };
                return true;
            }

            if (row.StartedOn is { } started && now - started > timeout)
            {
                await PipelineEvaluationRuns.CancelAsync(runtime, stops, now, row.RunId!, ct).ConfigureAwait(false);
                row.State = PipelineEvaluations.RowTimedOut;
                row.Error = $"The run took longer than {timeout.TotalMinutes:0} minute(s) - cancelled.";
                return true;
            }

            return false;
        }
    }
}
