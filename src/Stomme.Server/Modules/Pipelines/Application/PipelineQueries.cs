#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    // Every query runs inside the request's engine tenant (EngineTenantMiddleware), so the stores answer for the
    // request's SITE and nothing here takes a site or tenant from the caller.

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    /// <summary>
    /// The site's pipelines, latest version each, by name - and, when asked (the pipelines page, n8n's workflow list), each one's
    /// last run: one read per pipeline of the newest run's summary, so a list that does not show it does not pay for it.
    /// </summary>
    public sealed record ListPipelinesQuery(bool WithLastRuns = false) : IQuery<IReadOnlyList<PipelineView>>;

    public sealed class ListPipelinesQueryHandler(IPipelineDefinitionStore definitions, IPipelineInstanceStore instances, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<ListPipelinesQuery, IReadOnlyList<PipelineView>>
    {
        public async Task<IReadOnlyList<PipelineView>> Handle(ListPipelinesQuery request, CancellationToken ct)
        {
            var latest = await definitions.FindSummariesAsync(
                new PipelineDefinitionFilter { VersionOptions = VersionOptions.Latest },
                new PipelineDefinitionOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending), ct);
            var views = latest.Select(PipelineWire.ToView).OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // Each pipeline's tags, one read for the site - and whether it is archived (n8n's archive), one more.
            var tags = await PipelineTagStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var archived = await PipelineArchiveStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var folders = await PipelineFolderStore.ReadAllAsync(keyValues, tenants.TenantId, ct); // n8n's folders, one more
            foreach (var view in views)
            {
                if (tags.TryGetValue(view.DefinitionId, out var tagged))
                {
                    view.Tags = tagged;
                }

                view.Folder = folders.GetValueOrDefault(view.DefinitionId) ?? "";

                PipelineArchiveStore.Apply(view, archived.GetValueOrDefault(view.DefinitionId));
            }

            if (request.WithLastRuns)
            {
                // The runs waiting their turn, one read for the site - stored and not started, the engine's Pending.
                var queued = (await instances.SummarizeManyAsync(new PipelineInstanceFilter { PipelineStatus = PipelineStatus.Running, PipelineSubStatus = PipelineSubStatus.Pending }, ct))
                    .GroupBy(run => run.DefinitionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                foreach (var view in views)
                {
                    view.Queued = queued.GetValueOrDefault(view.DefinitionId);
                }

                foreach (var view in views)
                {
                    var newest = await instances.SummarizeManyAsync(
                        new PipelineInstanceFilter { DefinitionId = view.DefinitionId },
                        PageArgs.FromRange(0, 1),
                        new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending), ct);
                    if (newest.Items.FirstOrDefault() is { } run)
                    {
                        view.LastRun = PipelineWire.ToView(run);
                        view.LastRun.PipelineName = view.Name;
                    }
                }
            }

            return views;
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetPipelineQuery(string DefinitionId) : IQuery<PipelineDetail?>;

    public sealed class GetPipelineQueryHandler(IPipelineDefinitionStore definitions, IAccessGuard guard, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<GetPipelineQuery, PipelineDetail?>
    {
        public async Task<PipelineDetail?> Handle(GetPipelineQuery request, CancellationToken ct)
        {
            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (definition is null)
            {
                return null;
            }

            // The body's pinned test values are an author's; anyone else's body has none. And whether it is archived - the
            // editor opens an archived pipeline read-only.
            var detail = PipelineWire.ToDetail(definition);
            PipelineArchiveStore.Apply(detail, await PipelineArchiveStore.FindAsync(keyValues, tenants.TenantId, request.DefinitionId, ct));
            detail.Folder = await PipelineFolderStore.FindAsync(keyValues, tenants.TenantId, request.DefinitionId, ct); // where a pipeline made from it goes
            if (!await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct))
            {
                detail.Body = PipelineGraphCodec.WithoutTestValues(detail.Body);
            }

            return detail;
        }
    }

    /// <summary>
    /// Every version of a pipeline the engine keeps, newest first - one per publish, and the draft being
    /// worked on (a draft is saved over itself until it is published). Who saved each is recorded from 10.11.0 on.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListVersionsQuery(string DefinitionId) : IQuery<IReadOnlyList<PipelineVersionView>>;

    public sealed class ListVersionsQueryHandler(IPipelineDefinitionStore definitions) : IQueryHandler<ListVersionsQuery, IReadOnlyList<PipelineVersionView>>
    {
        public async Task<IReadOnlyList<PipelineVersionView>> Handle(ListVersionsQuery request, CancellationToken ct)
        {
            var versions = await definitions.FindManyAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.All }, ct);
            return versions.OrderByDescending(version => version.Version).Select(PipelineWire.ToVersion).ToList();
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    /// <summary>
    /// The site's runs, newest first - one pipeline's or every one's, in one state or any (n8n's execution filters:
    /// <see cref="PipelineRunStates"/>). A state the list does not know is refused rather than read as "any": a filter that
    /// silently shows everything is the wrong answer to a typo. And started on or after <see cref="From"/>, on or before
    /// <see cref="To"/> - whole days in UTC, as the list shows times - n8n's start-date filter.
    /// </summary>
    public sealed record ListRunsQuery(string? DefinitionId = null, int Take = 100, string? State = null, string? Search = null, DateOnly? From = null, DateOnly? To = null)
        : IQuery<IReadOnlyList<PipelineRunView>>;

    public sealed class ListRunsQueryHandler(IPipelineInstanceStore instances, IPipelineDefinitionStore definitions, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<ListRunsQuery, IReadOnlyList<PipelineRunView>>
    {
        /// <summary>The longest search the runs list takes.</summary>
        public const int MaxSearchLength = 200;

        public async Task<IReadOnlyList<PipelineRunView>> Handle(ListRunsQuery request, CancellationToken ct)
        {
            var filter = new PipelineInstanceFilter { DefinitionId = string.IsNullOrWhiteSpace(request.DefinitionId) ? null : request.DefinitionId };

            // n8n's execution search - a run by its id, or the runs of a correlation id (an order, a ticket).
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim();
                if (search.Length > MaxSearchLength)
                {
                    throw new ArgumentException($"A search is at most {MaxSearchLength} characters.", nameof(request.Search));
                }

                filter.SearchTerm = search;
            }
            if (!string.IsNullOrWhiteSpace(request.State))
            {
                if (!PipelineRunStates.IsKnown(request.State))
                {
                    throw new ArgumentException($"'{request.State}' is not a state runs are listed by - {string.Join(", ", PipelineRunStates.All.Select(entry => entry.State))}.", nameof(request.State));
                }

                filter.PipelineSubStatuses = PipelineRunStates.SubStatusesOf(request.State).Select(Enum.Parse<PipelineSubStatus>).ToList();
            }

            if (request.From is { } from && request.To is { } to && to < from)
            {
                throw new ArgumentException($"The runs cannot have started after {from:yyyy-MM-dd} and on or before {to:yyyy-MM-dd}: the second day comes first.", nameof(request.To));
            }

            filter.TimestampFilters = Started(request.From, request.To);

            var take = Math.Clamp(request.Take, 1, 1000);
            var order = new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending);
            var page = await instances.SummarizeManyAsync(filter, PageArgs.FromRange(0, take), order, ct);
            var runs = page.Items.ToList();

            // What the runs saved about themselves - shown with each, and searched ("order: 42", or "42") beside the store's
            // search of ids and correlation ids. One read for the site.
            var data = await PipelineRunDataStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var reviews = await PipelineRunReviewStore.ReadAllAsync(keyValues, tenants.TenantId, ct); // Found by its review too
            if (filter.SearchTerm is { } term)
            {
                var found = data.Where(pair => PipelineRunData.Matches(pair.Value, term)).Select(pair => pair.Key)
                    .Concat(reviews.Where(pair => PipelineRunReviews.Matches(pair.Value, term)).Select(pair => pair.Key))
                    .Distinct().Except(runs.Select(run => run.Id)).ToList();
                if (found.Count > 0)
                {
                    filter.SearchTerm = null;
                    filter.Ids = found;
                    runs.AddRange((await instances.SummarizeManyAsync(filter, PageArgs.FromRange(0, take), order, ct)).Items);
                    runs = runs.OrderByDescending(run => run.CreatedAt).Take(take).ToList();
                }
            }

            var names = await PipelineRunDetails.NamesAsync(definitions, ct);
            return runs.Select(PipelineWire.ToView).Select(view =>
            {
                view.PipelineName = names.TryGetValue(view.DefinitionId ?? "", out var name) ? name : null;
                view.Data = data.TryGetValue(view.Id, out var saved) ? saved : new Dictionary<string, string>();
                view.Review = reviews.TryGetValue(view.Id, out var review) ? review : null;
                return view;
            }).ToList();
        }

        // Started from the first day's midnight (UTC) to before the midnight after the last. The engine's filter reads a
        // timestamp at midnight as a whole DAY, built from the date in the SERVER's zone - so a boundary is never handed over at
        // midnight: one tick before it, with the operator that keeps the same meaning (> t-1 is >= t; <= t-1 is < t).
        internal static List<Stomme.Engine.Pipelines.Management.Models.TimestampFilter>? Started(DateOnly? from, DateOnly? to)
        {
            var filters = new List<Stomme.Engine.Pipelines.Management.Models.TimestampFilter>();
            if (from is { } first)
            {
                var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                filters.Add(new Stomme.Engine.Pipelines.Management.Models.TimestampFilter { Column = nameof(PipelineInstance.CreatedAt), Operator = Stomme.Engine.Pipelines.Management.Enums.TimestampFilterOperator.GreaterThan, Timestamp = start.AddTicks(-1) });
            }

            if (to is { } last)
            {
                var end = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                filters.Add(new Stomme.Engine.Pipelines.Management.Models.TimestampFilter { Column = nameof(PipelineInstance.CreatedAt), Operator = Stomme.Engine.Pipelines.Management.Enums.TimestampFilterOperator.LessThanOrEqual, Timestamp = end.AddTicks(-1) });
            }

            return filters.Count == 0 ? null : filters;
        }
    }

    /// <summary>
    /// One run, as n8n's execution view has it: its pipeline's name; what it was started with, for an author only (a webhook's
    /// body, a failure's error - the data rule of a run's steps); and, for a run a "Pipeline failed" trigger started, the run
    /// that failed - for anyone who may view runs, as the run's own log names it.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetRunQuery(string Id) : IQuery<PipelineRunView?>;

    public sealed class GetRunQueryHandler(IPipelineInstanceStore instances, IPipelineDefinitionStore definitions, IAccessGuard guard, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<GetRunQuery, PipelineRunView?>
    {
        /// <summary>The most of a run's sub-pipeline runs its page lists - a loop over Dispatch Pipeline can start many.</summary>
        public const int MaxChildRuns = 50;

        public async Task<PipelineRunView?> Handle(GetRunQuery request, CancellationToken ct)
        {
            var instance = await instances.FindAsync(new PipelineInstanceFilter { Id = request.Id }, ct);
            if (instance is null)
            {
                return null;
            }

            var view = PipelineWire.ToView(instance);
            var input = KeepRunInput.Of(instance.PipelineState);
            if (!string.IsNullOrEmpty(instance.DefinitionId))
            {
                var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = instance.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
                view.PipelineName = definition?.Name;
                view.PublishedVersion = definition is { IsPublished: true }
                    ? definition.Version
                    : (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = instance.DefinitionId, VersionOptions = VersionOptions.Published }, ct))?.Version;
            }

            (view.FailedRunId, view.FailedPipelineName) = PipelineRunDetails.FailureOf(input);
            view.RetryOfRunId = instance.PipelineState.Properties.TryGetValue(PipelineRetryFromFailure.RetryOfProperty, out var retryOf) ? retryOf?.ToString() : null;
            view.Data = await PipelineRunDataStore.ReadAsync(keyValues, tenants.TenantId, instance.Id, ct);
            view.Review = await PipelineRunReviewStore.ReadAsync(keyValues, tenants.TenantId, instance.Id, ct);

            // A sub-pipeline's run says whose run started it, and a run the runs it started (Execute / Dispatch Pipeline).
            if (!string.IsNullOrEmpty(instance.ParentPipelineInstanceId))
            {
                view.ParentRunId = instance.ParentPipelineInstanceId;
                var parent = await instances.FindAsync(new PipelineInstanceFilter { Id = instance.ParentPipelineInstanceId }, ct);
                if (parent is not null && !string.IsNullOrEmpty(parent.DefinitionId))
                {
                    view.ParentPipelineName = (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = parent.DefinitionId, VersionOptions = VersionOptions.Latest }, ct))?.Name;
                }
            }

            var children = await instances.SummarizeManyAsync(
                new PipelineInstanceFilter { ParentPipelineInstanceIds = new[] { instance.Id } },
                PageArgs.FromRange(0, MaxChildRuns),
                new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending), ct);
            if (children.Items.Count > 0)
            {
                var names = await PipelineRunDetails.NamesAsync(definitions, ct);
                view.ChildRuns = children.Items.Select(PipelineWire.ToView).Select(child =>
                {
                    child.PipelineName = names.TryGetValue(child.DefinitionId ?? "", out var name) ? name : null;
                    return child;
                }).ToList();
            }
            if (input.Count > 0 && await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct))
            {
                view.InputJson = PipelineRunDetails.Json(input);
            }

            view.InputNotKept = KeepRunInput.WasEmptied(instance.PipelineState);

            return view;
        }
    }

    /// <summary>
    /// Whether a run the store does not hold is one this process started, or works on, for the site and the engine has
    /// not stored yet - a run is stored when its first burst of work ends, and the editor and the Run page read a run as soon as it
    /// is started. Such a run is "not yet" (the API's 204); any other id - a run stored and since deleted too - is none (404).
    /// Until then a WebAssembly page logged the first read of every run it started as a failed request, measured driving the
    /// editor.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record IsRunStartingQuery(string Id) : IQuery<bool>;

    public sealed class IsRunStartingQueryHandler(Reliability.PipelineRunStops stops, ITenantAccessor tenants, Stomme.Engine.Common.ISystemClock clock)
        : IQueryHandler<IsRunStartingQuery, bool>
    {
        public Task<bool> Handle(IsRunStartingQuery request, CancellationToken ct) =>
            Task.FromResult(!string.IsNullOrWhiteSpace(request.Id) && stops.IsStarting(request.Id, tenants.TenantId, clock.UtcNow));
    }

    /// <summary>
    /// The pipelines that call one (Execute or Dispatch pipeline naming it), in their latest versions, by name: so the
    /// author of a sub-pipeline sees whom a change to what it expects touches. A body the editor did not draw has no graph to read.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListCallersQuery(string DefinitionId) : IQuery<IReadOnlyList<PipelineCallerView>>;

    public sealed class ListCallersQueryHandler(IPipelineDefinitionStore definitions) : IQueryHandler<ListCallersQuery, IReadOnlyList<PipelineCallerView>>
    {
        public async Task<IReadOnlyList<PipelineCallerView>> Handle(ListCallersQuery request, CancellationToken ct)
        {
            var callers = new List<PipelineCallerView>();
            foreach (var definition in await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Latest }, ct))
            {
                if (definition.DefinitionId == request.DefinitionId || string.IsNullOrEmpty(definition.StringData))
                {
                    continue;
                }

                Stomme.Models.Pipelines.PipelineGraph graph;
                try
                {
                    graph = PipelineGraphCodec.ToGraph(definition.StringData);
                }
                catch (Exception)
                {
                    continue; // not a body this editor reads
                }

                callers.AddRange(graph.Steps
                    .Where(step => PipelineCallContracts.IsCall(step.Type) && step.Inputs.TryGetValue(PipelineCallContracts.CalledInput, out var called) && called == request.DefinitionId)
                    .Select(step => new PipelineCallerView { DefinitionId = definition.DefinitionId, Name = definition.Name, StepId = step.Id, StepName = step.Name, Waits = step.Type == "Stomme.ExecutePipeline" }));
            }

            return callers.OrderBy(caller => caller.Name, StringComparer.OrdinalIgnoreCase).ThenBy(caller => caller.StepName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>What a run's view is filled with beyond the engine's summary.</summary>
    internal static class PipelineRunDetails
    {
        private static readonly System.Text.Json.JsonSerializerOptions Readable = new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Every pipeline's name on the site, by definition id - one read for a list of runs.</summary>
        public static async Task<IReadOnlyDictionary<string, string>> NamesAsync(IPipelineDefinitionStore definitions, CancellationToken ct)
        {
            var latest = await definitions.FindSummariesAsync(
                new PipelineDefinitionFilter { VersionOptions = VersionOptions.Latest },
                new PipelineDefinitionOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending), ct);
            return latest
                .Where(summary => !string.IsNullOrEmpty(summary.DefinitionId))
                .GroupBy(summary => summary.DefinitionId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name ?? group.Key, StringComparer.Ordinal);
        }

        /// <summary>
        /// The input as indented JSON, cut where a step's value would be. A value that is JSON text - a failure a handler is told
        /// of, a webhook's JSON body - is shown as the JSON it is: escaped as a string it read as a wall of backslashes (found
        /// driving).
        /// </summary>
        public static string Json(IDictionary<string, object> input)
        {
            var shown = new System.Text.Json.Nodes.JsonObject();
            foreach (var pair in input.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                shown[pair.Key] = Node(pair.Value);
            }

            var json = shown.ToJsonString(Readable);
            return json.Length > ListRunStepsQueryHandler.MaxValueLength ? json[..ListRunStepsQueryHandler.MaxValueLength] + "… (cut)" : json;
        }

        private static System.Text.Json.Nodes.JsonNode? Node(object? value)
        {
            var text = value switch
            {
                string plain => plain,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
            if (text is null)
            {
                return System.Text.Json.JsonSerializer.SerializeToNode(value, Readable);
            }

            if (text.TrimStart() is ['{' or '[', ..])
            {
                try
                {
                    return System.Text.Json.Nodes.JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    // text that only looks like JSON stays text
                }
            }

            return System.Text.Json.Nodes.JsonValue.Create(text);
        }

        /// <summary>The failed run a "Pipeline failed" trigger's input names, if it is one.</summary>
        public static (string? Run, string? Pipeline) FailureOf(IDictionary<string, object> input)
        {
            if (!input.TryGetValue(Stomme.Modules.Pipelines.Steps.PipelineFailed.FailureInputKey, out var value))
            {
                return (null, null);
            }

            var text = value switch
            {
                string plain => plain,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
            try
            {
                using var failure = System.Text.Json.JsonDocument.Parse(text ?? "");
                var root = failure.RootElement;
                return (
                    root.TryGetProperty("run", out var run) ? run.GetString() : null,
                    root.TryGetProperty("pipeline", out var pipeline) ? pipeline.GetString() : null);
            }
            catch (System.Text.Json.JsonException)
            {
                return (null, null);
            }
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetRunLogQuery(string Id) : IQuery<IReadOnlyList<PipelineRunLogLine>>;

    public sealed class GetRunLogQueryHandler(IPipelineExecutionLogStore log) : IQueryHandler<GetRunLogQuery, IReadOnlyList<PipelineRunLogLine>>
    {
        public async Task<IReadOnlyList<PipelineRunLogLine>> Handle(GetRunLogQuery request, CancellationToken ct)
        {
            var page = await log.FindManyAsync(
                new PipelineExecutionLogRecordFilter { PipelineInstanceId = request.Id },
                PageArgs.FromRange(0, 1000),
                new PipelineExecutionLogRecordOrder<long>(x => x.Sequence, OrderDirection.Ascending), ct);
            return page.Items.Select(PipelineWire.ToLine).ToList();
        }
    }

    /// <summary>
    /// A run's step executions, in the order they started - what the editor shows on each step (state,
    /// timing, fault) and, to an AUTHOR only, what the step received and produced. The engine records each execution as the
    /// run commits (its step execution store, EF or RavenDB like the rest of the engine's); the engine's own wrappers - the
    /// pipeline and the flowcharts it and each body run in - are left out.
    /// </summary>
    /// <remarks>
    /// The data is for authors (<c>pipelines:manage</c>): a viewer sees the states, never a value - the Run page shows a
    /// viewer log lines, not data. An input the step marks as able to hold a secret (<c>CanContainSecrets</c>, e.g. an HTTP
    /// request's Authorization) is shown as hidden even to an author, and a value longer than <see cref="MaxValueLength"/>
    /// is cut and flagged.
    /// </remarks>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListRunStepsQuery(string Id) : IQuery<IReadOnlyList<PipelineStepRunView>>;

    public sealed class ListRunStepsQueryHandler(IStepExecutionStore executions, IStepRegistry registry, IAccessGuard guard)
        : IQueryHandler<ListRunStepsQuery, IReadOnlyList<PipelineStepRunView>>
    {
        internal const int MaxValueLength = 32 * 1024;
        internal const string Hidden = "(hidden - this input can hold a secret)";

        internal static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "Stomme.Pipeline", PipelineGraphCodec.FlowchartType };

        // Relaxed escaping: the editor shows this as TEXT (never markup), and """ for every quote of a record made the
        // records unreadable (found driving).
        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public async Task<IReadOnlyList<PipelineStepRunView>> Handle(ListRunStepsQuery request, CancellationToken ct)
        {
            var records = await executions.FindManyAsync(
                new StepExecutionRecordFilter { PipelineInstanceId = request.Id },
                new StepExecutionRecordOrder<DateTimeOffset>(x => x.StartedAt, OrderDirection.Ascending), ct);
            var author = await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct);
            return ToViews(records, registry, author).ToList();
        }

        /// <summary>
        /// Step execution records as the editor shows them - the engine's wrappers left out, data for an author only, a secret
        /// input hidden even then. A step test shows its records by the same rule.
        /// </summary>
        internal static IEnumerable<PipelineStepRunView> ToViews(IEnumerable<StepExecutionRecord> records, IStepRegistry registry, bool author) =>
            records.Where(record => !Wrappers.Contains(record.StepType)).Select(record => ToView(record, author, registry));

        private static PipelineStepRunView ToView(StepExecutionRecord record, bool author, IStepRegistry registry)
        {
            var view = new PipelineStepRunView
            {
                StepId = record.StepId,
                StepName = record.StepName,
                StepType = record.StepType,
                Status = record.Status.ToString(),
                StartedAtUtc = record.StartedAt.UtcDateTime,
                CompletedAtUtc = record.CompletedAt?.UtcDateTime,
                Error = record.Exception?.Message,
                Waiting = record.HasBookmarks && record.CompletedAt is null, // parked on a bookmark: a Wait, an event
                DataNotKept = record.Metadata?.ContainsKey(PipelineStepDataKeeping.NotKeptKey) == true, // emptied by the pipeline's setting
            };

            // A failure the step did not fault on - its Failed path taken, or the pipeline gone on past it (StepReliability).
            if (Reliability.StepReliability.HandledFailure(record.Metadata) is ({ } handled, var message))
            {
                view.HandledFailure = handled;
                view.Error ??= message;
            }

            if (author)
            {
                var secrets = SecretInputs(record, registry);
                var inputs = record.StepState?.ToDictionary(pair => pair.Key, pair => secrets.Contains(pair.Key) ? Hidden : SecretLinesHidden(pair.Key, pair.Value), StringComparer.Ordinal);
                view.InputsJson = Render(inputs, view);
                view.OutputsJson = Render(record.Outputs, view);
                view.ResultCount = record.Outputs is not null && record.Outputs.TryGetValue("Result", out var result) ? PipelineDataTables.CountOf(result) : null;
            }

            return view;
        }

        // The inputs this step type marks as able to hold a secret, by name as the engine records them (either casing).
        private static HashSet<string> SecretInputs(StepExecutionRecord record, IStepRegistry registry)
        {
            var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var descriptor = registry.Find(record.StepType, record.StepTypeVersion <= 0 ? 1 : record.StepTypeVersion);
            foreach (var input in descriptor?.Inputs ?? Enumerable.Empty<Stomme.Engine.Pipelines.Models.InputDescriptor>())
            {
                if (input.IsSensitive || (descriptor is not null && ListStepsQueryHandler.IsSensitive(descriptor.ClrType, input.Name)))
                {
                    secrets.Add(input.Name);
                }
            }

            return secrets;
        }

        private static string? Render(IDictionary<string, object?>? values, PipelineStepRunView view)
        {
            if (values is null || values.Count == 0)
            {
                return null;
            }

            string text;
            try
            {
                // The data steps pass records as JSON TEXT; shown as one escaped string they cannot be read, so a value that
                // is a JSON object or array is shown as the JSON it is.
                // A stored record comes back from the store's own JSON as a string-KIND JsonElement, not a string (measured driving).
                var readable = values.ToDictionary(pair => pair.Key, pair => TextOf(pair.Value) is { } candidate ? AsJson(candidate) ?? pair.Value : pair.Value, StringComparer.Ordinal);
                text = System.Text.Json.JsonSerializer.Serialize(readable, Json);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
            {
                text = "(the value could not be shown: " + ex.Message + ")";
            }

            if (text.Length > MaxValueLength)
            {
                view.Truncated = true;
                text = text[..MaxValueLength] + "\n…";
            }

            return text;
        }

        internal const string HiddenLine = "(hidden - it can hold a secret)";

        // A secret's name in a parameter line: a password, a secret, a token, an API or a private key.
        private static readonly System.Text.RegularExpressions.Regex SecretName = new("(password|passwd|secret|token|api[_-]?key|private[_-]?key)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // A header that carries a credential.
        private static readonly System.Text.RegularExpressions.Regex SecretHeader = new("^(authorization|proxy-authorization|cookie)$|token|secret|api-?key|auth",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// A connection step's parameter lines that name a secret - a password, a token, a key, an Authorization header - their
        /// values hidden, every other line shown as it was (a password typed into a step's parameters was listed in its
        /// run's data, found driving WordPress's user Create). Only the <c>Parameters</c> input of "name = value" lines.
        /// </summary>
        internal static object? SecretLinesHidden(string input, object? value)
        {
            if (!string.Equals(input, "Parameters", StringComparison.OrdinalIgnoreCase) || TextOf(value) is not { } text || !text.Contains('=', StringComparison.Ordinal))
            {
                return value;
            }

            var hidden = false;
            var lines = text.Split('\n').Select(line =>
            {
                var at = line.IndexOf('=', StringComparison.Ordinal);
                if (at <= 0)
                {
                    return line;
                }

                var name = line[..at].Trim();
                var rest = line[(at + 1)..].Trim();
                var header = name.Equals("headers", StringComparison.OrdinalIgnoreCase) || name.Equals("header", StringComparison.OrdinalIgnoreCase)
                    ? rest.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 ? rest[..colon].Trim() : null
                    : null;
                if (header is not null ? SecretHeader.IsMatch(header) : SecretName.IsMatch(name))
                {
                    hidden = true;
                    return header is not null ? $"{name} = {header}: {HiddenLine}" : $"{name} = {HiddenLine}";
                }

                return line;
            }).ToList();
            return hidden ? string.Join("\n", lines) : value;
        }

        private static string? TextOf(object? value) => value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            System.Text.Json.Nodes.JsonValue node when node.TryGetValue<string>(out var text) => text,
            _ => null,
        };

        private static System.Text.Json.Nodes.JsonNode? AsJson(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                return null;
            }

            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}
