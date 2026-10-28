#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>An evaluation as it is kept: its pipeline version, its table, its state and each row's run.</summary>
    public sealed class StoredEvaluation
    {
        public string Id { get; set; } = "";

        public string DefinitionId { get; set; } = "";

        public string? PipelineName { get; set; }

        /// <summary>The published version the evaluation runs - every row the same, whatever is published meanwhile.</summary>
        public string DefinitionVersionId { get; set; } = "";

        public int Version { get; set; }

        /// <summary>The "When evaluating" step's id: where each row's run starts.</summary>
        public string TriggerStepId { get; set; } = "";

        public string TableId { get; set; } = "";

        public string? TableName { get; set; }

        public string State { get; set; } = PipelineEvaluations.Running;

        public string? StartedBy { get; set; }

        public DateTimeOffset StartedOn { get; set; }

        public DateTimeOffset? FinishedOn { get; set; }

        public List<StoredEvaluationRow> Rows { get; set; } = new();
    }

    public sealed class StoredEvaluationRow
    {
        public string RowId { get; set; } = "";

        /// <summary>The table's columns as text, for the page.</summary>
        public Dictionary<string, string> Values { get; set; } = new();

        /// <summary>The row as a run reads it - its id and typed values, a JSON object - this test case's input.</summary>
        public string Json { get; set; } = "{}";

        public string? RunId { get; set; }

        public string State { get; set; } = PipelineEvaluations.Waiting;

        public string? Error { get; set; }

        public DateTimeOffset? StartedOn { get; set; }
    }

    /// <summary>What a row's run recorded with its Evaluation steps.</summary>
    public sealed class StoredEvaluationResult
    {
        public Dictionary<string, decimal> Metrics { get; set; } = new();

        public Dictionary<string, string> Outputs { get; set; } = new();
    }

    /// <summary>
    /// BL-229 - n8n's Evaluations, kept in the engine's key-value store under the SITE: an evaluation one key, what each row's run
    /// recorded one key per row (the run writes its own, the task the evaluation - never the same key). Deleted together.
    /// </summary>
    public static class PipelineEvaluationStore
    {
        public const string KeyPrefix = "pipelines:evaluation:";

        public const string ResultKeyPrefix = "pipelines:evalresult:";

        /// <summary>How many evaluations a pipeline keeps; starting one more deletes the oldest finished ones.</summary>
        public const int KeepPerPipeline = 20;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string id) => Prefix(tenantId) + id;

        public static string ResultsPrefix(string? tenantId, string id) => ResultKeyPrefix + tenantId + ":" + id + ":";

        public static string ResultKey(string? tenantId, string id, string rowId) => ResultsPrefix(tenantId, id) + rowId;

        public static async Task<List<StoredEvaluation>> ListAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct) =>
            (await keyValues.FindManyAsync(new KeyValueFilter { Key = Prefix(tenantId), StartsWith = true }, ct))
                .Where(pair => pair.Key.StartsWith(Prefix(tenantId), StringComparison.Ordinal))
                .Select(pair => Read<StoredEvaluation>(pair.SerializedValue))
                .OfType<StoredEvaluation>()
                .ToList();

        public static async Task<StoredEvaluation?> FindAsync(IKeyValueStore keyValues, string? tenantId, string id, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(id) ? null : Read<StoredEvaluation>((await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, id) }, ct))?.SerializedValue);

        public static Task SaveAsync(IKeyValueStore keyValues, string? tenantId, StoredEvaluation evaluation, CancellationToken ct) =>
            keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId, evaluation.Id), SerializedValue = JsonSerializer.Serialize(evaluation, Json) }, ct);

        /// <summary>What each row's run recorded, by the row's id.</summary>
        public static async Task<Dictionary<string, StoredEvaluationResult>> ReadResultsAsync(IKeyValueStore keyValues, string? tenantId, string id, CancellationToken ct)
        {
            var prefix = ResultsPrefix(tenantId, id);
            var results = new Dictionary<string, StoredEvaluationResult>(StringComparer.Ordinal);
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct))
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && Read<StoredEvaluationResult>(pair.SerializedValue) is { } result)
                {
                    results[pair.Key[prefix.Length..]] = result;
                }
            }

            return results;
        }

        /// <summary>
        /// Adds what a row's run recorded - metrics and outputs given again replace those names; refused, with the reason, past the
        /// limits. Nothing is recorded for an evaluation or a row that is no longer there (deleted while its run went on).
        /// </summary>
        public static async Task RecordAsync(IKeyValueStore keyValues, string? tenantId, string id, string rowId, IReadOnlyDictionary<string, decimal>? metrics, IReadOnlyDictionary<string, string>? outputs, CancellationToken ct)
        {
            var evaluation = await FindAsync(keyValues, tenantId, id, ct);
            if (evaluation is null || evaluation.Rows.All(row => row.RowId != rowId))
            {
                return;
            }

            var key = ResultKey(tenantId, id, rowId);
            var result = Read<StoredEvaluationResult>((await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct))?.SerializedValue) ?? new StoredEvaluationResult();
            foreach (var metric in metrics ?? new Dictionary<string, decimal>())
            {
                result.Metrics[metric.Key] = metric.Value;
            }

            foreach (var output in outputs ?? new Dictionary<string, string>())
            {
                result.Outputs[output.Key] = output.Value;
            }

            if (result.Metrics.Count > PipelineEvaluations.MaxMetrics)
            {
                throw new ArgumentException($"An evaluation keeps at most {PipelineEvaluations.MaxMetrics} metrics per row.");
            }

            if (result.Outputs.Count > PipelineEvaluations.MaxOutputs)
            {
                throw new ArgumentException($"An evaluation keeps at most {PipelineEvaluations.MaxOutputs} outputs per row.");
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = JsonSerializer.Serialize(result, Json) }, ct);
        }

        /// <summary>Deletes the evaluation and what its rows recorded (the runs stay, as runs).</summary>
        public static async Task DeleteAsync(IKeyValueStore keyValues, string? tenantId, string id, CancellationToken ct)
        {
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = ResultsPrefix(tenantId, id), StartsWith = true }, ct))
            {
                await keyValues.DeleteAsync(pair.Key, ct);
            }

            await keyValues.DeleteAsync(Key(tenantId, id), ct);
        }

        public static PipelineEvaluationView ToView(StoredEvaluation evaluation, IReadOnlyDictionary<string, StoredEvaluationResult> results, bool withRows)
        {
            var rows = evaluation.Rows.Select(row =>
            {
                var result = results.TryGetValue(row.RowId, out var recorded) ? recorded : null;
                return new PipelineEvaluationRowView
                {
                    RowId = row.RowId,
                    Values = row.Values,
                    RunId = row.RunId,
                    State = row.State,
                    Error = row.Error,
                    Metrics = result?.Metrics ?? new Dictionary<string, decimal>(),
                    Outputs = result?.Outputs ?? new Dictionary<string, string>(),
                };
            }).ToList();

            return new PipelineEvaluationView
            {
                Id = evaluation.Id,
                DefinitionId = evaluation.DefinitionId,
                PipelineName = evaluation.PipelineName,
                Version = evaluation.Version,
                TableId = evaluation.TableId,
                TableName = evaluation.TableName,
                State = evaluation.State,
                StartedBy = evaluation.StartedBy,
                StartedOnUtc = evaluation.StartedOn.UtcDateTime,
                FinishedOnUtc = evaluation.FinishedOn?.UtcDateTime,
                RowCount = rows.Count,
                RowsDone = rows.Count(row => PipelineEvaluations.IsDone(row.State)),
                RowsFailed = rows.Count(row => PipelineEvaluations.IsFailed(row.State)),
                Averages = PipelineEvaluations.Averages(rows.Select(row => (IReadOnlyDictionary<string, decimal>)row.Metrics)),
                Rows = withRows ? rows : new List<PipelineEvaluationRowView>(),
            };
        }

        // Written by this class only; anything unreadable is nothing rather than a broken page.
        private static T? Read<T>(string? json) where T : class
        {
            try
            {
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Starts an evaluation of the pipeline's PUBLISHED version over the data table's rows (the first <see cref="PipelineEvaluationRequest.MaxRows"/>,
    /// at most <see cref="PipelineEvaluations.MaxRows"/>); the evaluations task then runs them, each from the "When evaluating"
    /// step. Null when there is no such pipeline; refused, saying why, when it is not published, has no such step, is being
    /// evaluated already, or the table is missing or empty.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record StartEvaluationCommand(string DefinitionId, PipelineEvaluationRequest? Request) : ICommand<PipelineEvaluationView?>;

    public sealed class StartEvaluationCommandHandler(
        IPipelineDefinitionStore definitions,
        IKeyValueStore keyValues,
        ITenantAccessor tenants,
        IIdentityGenerator identities,
        ICurrentUser currentUser,
        ISystemClock clock) : ICommandHandler<StartEvaluationCommand, PipelineEvaluationView?>
    {
        public async Task<PipelineEvaluationView?> Handle(StartEvaluationCommand request, CancellationToken ct)
        {
            var latest = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (latest is null)
            {
                return null;
            }

            var published = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Published }, ct)
                ?? throw new InvalidOperationException("The pipeline has no published version to evaluate - publish it first.");
            var trigger = PipelineGraphCodec.ToGraph(published.StringData).Steps
                .FirstOrDefault(step => step.Type == PipelineEvaluations.TriggerType && !step.Disabled)
                ?? throw new InvalidOperationException("The published pipeline has no 'When evaluating' step - add one where the test cases should enter, and publish it.");

            var tenantId = tenants.TenantId;
            var evaluations = await PipelineEvaluationStore.ListAsync(keyValues, tenantId, ct);
            if (evaluations.Any(each => each.DefinitionId == request.DefinitionId && each.State == PipelineEvaluations.Running))
            {
                throw new InvalidOperationException("An evaluation of this pipeline is still running - wait for it to end, or cancel it.");
            }

            var table = await PipelineSiteTableStore.FindAsync(keyValues, tenantId, request.Request?.TableId, ct)
                ?? throw new ArgumentException("There is no such data table on the site.");
            var take = Math.Clamp(request.Request?.MaxRows ?? PipelineEvaluations.MaxRows, 1, PipelineEvaluations.MaxRows);
            var rows = (await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenantId, table.Id, ct)).Take(take).ToList();
            if (rows.Count == 0)
            {
                throw new InvalidOperationException($"The data table '{table.Name}' has no rows - its rows are the test cases.");
            }

            var evaluation = new StoredEvaluation
            {
                Id = identities.GenerateId(),
                DefinitionId = published.DefinitionId,
                PipelineName = latest.Name,
                DefinitionVersionId = published.Id,
                Version = published.Version,
                TriggerStepId = trigger.Id,
                TableId = table.Id,
                TableName = table.Name,
                StartedBy = !string.IsNullOrWhiteSpace(currentUser.UserName) ? currentUser.UserName : currentUser.UserId is { } id ? "user " + id : null,
                StartedOn = clock.UtcNow,
                Rows = rows.Select(row => new StoredEvaluationRow
                {
                    RowId = PipelineSiteTableStore.IdOf(row),
                    Values = table.Columns.ToDictionary(column => column.Name, column => PipelineSiteTableStore.Text(table, row).GetValueOrDefault(column.Name) ?? "", StringComparer.Ordinal),
                    Json = PipelineSiteTableStore.Project(table, row).ToJsonString(),
                }).ToList(),
            };
            await PipelineEvaluationStore.SaveAsync(keyValues, tenantId, evaluation, ct);

            // The oldest finished ones beyond what a pipeline keeps go, with what their rows recorded.
            foreach (var old in evaluations.Where(each => each.DefinitionId == request.DefinitionId && each.State != PipelineEvaluations.Running)
                         .OrderByDescending(each => each.StartedOn).Skip(PipelineEvaluationStore.KeepPerPipeline - 1))
            {
                await PipelineEvaluationStore.DeleteAsync(keyValues, tenantId, old.Id, ct);
            }

            return PipelineEvaluationStore.ToView(evaluation, new Dictionary<string, StoredEvaluationResult>(), withRows: true);
        }
    }

    /// <summary>A pipeline's evaluations, newest first - each with its counts and averages, not its rows.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListEvaluationsQuery(string DefinitionId) : IQuery<List<PipelineEvaluationView>>;

    public sealed class ListEvaluationsQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<ListEvaluationsQuery, List<PipelineEvaluationView>>
    {
        public async Task<List<PipelineEvaluationView>> Handle(ListEvaluationsQuery request, CancellationToken ct)
        {
            var views = new List<PipelineEvaluationView>();
            foreach (var evaluation in (await PipelineEvaluationStore.ListAsync(keyValues, tenants.TenantId, ct))
                         .Where(each => each.DefinitionId == request.DefinitionId).OrderByDescending(each => each.StartedOn))
            {
                views.Add(PipelineEvaluationStore.ToView(evaluation, await PipelineEvaluationStore.ReadResultsAsync(keyValues, tenants.TenantId, evaluation.Id, ct), withRows: false));
            }

            return views;
        }
    }

    /// <summary>One evaluation with every row; null when there is no such evaluation on the site.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetEvaluationQuery(string Id) : IQuery<PipelineEvaluationView?>;

    public sealed class GetEvaluationQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<GetEvaluationQuery, PipelineEvaluationView?>
    {
        public async Task<PipelineEvaluationView?> Handle(GetEvaluationQuery request, CancellationToken ct) =>
            await PipelineEvaluationStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct) is { } evaluation
                ? PipelineEvaluationStore.ToView(evaluation, await PipelineEvaluationStore.ReadResultsAsync(keyValues, tenants.TenantId, evaluation.Id, ct), withRows: true)
                : null;
    }

    /// <summary>Stops a running evaluation: the row running is cancelled with its run, the waiting ones are never run. Null when there is no such evaluation.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record CancelEvaluationCommand(string Id) : ICommand<PipelineEvaluationView?>;

    public sealed class CancelEvaluationCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants, IPipelineRuntime runtime, ISystemClock clock, Reliability.PipelineRunStops stops)
        : ICommandHandler<CancelEvaluationCommand, PipelineEvaluationView?>
    {
        public async Task<PipelineEvaluationView?> Handle(CancelEvaluationCommand request, CancellationToken ct)
        {
            var evaluation = await PipelineEvaluationStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct);
            if (evaluation is null)
            {
                return null;
            }

            if (evaluation.State == PipelineEvaluations.Running)
            {
                foreach (var row in evaluation.Rows.Where(row => !PipelineEvaluations.IsDone(row.State)))
                {
                    if (row.State == PipelineEvaluations.RowRunning && row.RunId is { } runId)
                    {
                        await PipelineEvaluationRuns.CancelAsync(runtime, stops, clock.UtcNow, runId, ct);
                    }

                    row.State = PipelineEvaluations.RowCancelled;
                }

                evaluation.State = PipelineEvaluations.Cancelled;
                evaluation.FinishedOn = clock.UtcNow;
                await PipelineEvaluationStore.SaveAsync(keyValues, tenants.TenantId, evaluation, ct);
            }

            return PipelineEvaluationStore.ToView(evaluation, await PipelineEvaluationStore.ReadResultsAsync(keyValues, tenants.TenantId, evaluation.Id, ct), withRows: true);
        }
    }

    /// <summary>Deletes an evaluation that has ended, with what its rows recorded (the runs stay). Refused while it runs.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeleteEvaluationCommand(string Id) : ICommand<bool>;

    public sealed class DeleteEvaluationCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : ICommandHandler<DeleteEvaluationCommand, bool>
    {
        public async Task<bool> Handle(DeleteEvaluationCommand request, CancellationToken ct)
        {
            var evaluation = await PipelineEvaluationStore.FindAsync(keyValues, tenants.TenantId, request.Id, ct);
            if (evaluation is null)
            {
                return false;
            }

            if (evaluation.State == PipelineEvaluations.Running)
            {
                throw new InvalidOperationException("The evaluation is still running - cancel it first.");
            }

            await PipelineEvaluationStore.DeleteAsync(keyValues, tenants.TenantId, evaluation.Id, ct);
            return true;
        }
    }

    /// <summary>The evaluation's side of a row's run.</summary>
    public static class PipelineEvaluationRuns
    {
        /// <summary>The input a row's run starts with: which evaluation and row it is, and the row as a JSON object.</summary>
        public static Dictionary<string, object> InputOf(StoredEvaluation evaluation, StoredEvaluationRow row) => new()
        {
            [Steps.EvaluationTrigger.EvaluationInputKey] = evaluation.Id,
            [Steps.EvaluationTrigger.RowIdInputKey] = row.RowId,
            [Steps.EvaluationTrigger.RowInputKey] = row.Json,
        };

        public static string CorrelationOf(StoredEvaluation evaluation) => "evaluation:" + evaluation.Id;

        /// <summary>
        /// Stops a row's run where it is: asked to stop before its next step - a row's run is busy in its first burst of work, which
        /// the engine stores only when the burst ends, so a timed-out or cancelled row's run went on to its end (BL-229, found with
        /// Stop that stops) - and cancelled where the engine has it stored.
        /// </summary>
        public static async Task CancelAsync(IPipelineRuntime runtime, Reliability.PipelineRunStops stops, DateTimeOffset now, string runId, CancellationToken ct)
        {
            stops.Ask(runId, now);
            var client = await runtime.CreateClientAsync(runId, ct);
            if (await client.InstanceExistsAsync(ct))
            {
                await client.CancelAsync(ct);
            }
        }
    }
}
