#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Enums;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// How the site's pipelines have been running over the last day, week or month (n8n's Insights): the window's runs,
    /// the newest <see cref="PipelineInsights.MaxRuns"/> of them, read as summaries (states and times, never data) and counted by
    /// <see cref="PipelineInsights.Of"/>, with the time they saved where a published pipeline says what one run saves. For anyone
    /// who may view runs - the runs list shows them the same.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetInsightsQuery(int Days = 7) : IQuery<PipelineInsightsView>;

    public sealed class GetInsightsQueryHandler(IPipelineInstanceStore instances, IPipelineDefinitionStore definitions, ISystemClock clock)
        : IQueryHandler<GetInsightsQuery, PipelineInsightsView>
    {
        public async Task<PipelineInsightsView> Handle(GetInsightsQuery request, CancellationToken ct)
        {
            var days = PipelineInsights.Window(request.Days);
            var since = clock.UtcNow - TimeSpan.FromDays(days);
            var page = await instances.SummarizeManyAsync(
                new PipelineInstanceFilter
                {
                    TimestampFilters = new[]
                    {
                        new TimestampFilter { Column = nameof(PipelineInstance.CreatedAt), Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = since },
                    },
                },
                PageArgs.FromRange(0, PipelineInsights.MaxRuns + 1),
                new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending), ct);
            var names = await PipelineRunDetails.NamesAsync(definitions, ct);
            var runs = page.Items.Take(PipelineInsights.MaxRuns).Select(PipelineWire.ToView).Select(view =>
            {
                view.PipelineName = names.TryGetValue(view.DefinitionId ?? "", out var name) ? name : null;
                return view;
            });

            // n8n's time saved: what each published pipeline says one run that finishes well saves, read off its body.
            var published = await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Published }, ct);
            var minutesSaved = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var definition in published)
            {
                if (PipelineGraphCodec.MinutesSaved(definition.StringData) is { } minutes)
                {
                    minutesSaved[definition.DefinitionId] = minutes;
                }
            }

            return PipelineInsights.Of(runs, days, since.UtcDateTime, capped: page.Items.Count > PipelineInsights.MaxRuns, minutesSaved);
        }
    }

    /// <summary>
    /// How each step of one pipeline has been doing over the last day, week or month (better than n8n's Insights, which stop
    /// at the workflow): the window's runs of the pipeline, the newest <see cref="PipelineInsights.MaxRunsForSteps"/>, and their
    /// steps' passes read as summaries - states and times, never data - so anyone who may view runs sees them; a failure the step
    /// went on past (its Failed path, or on) counts as one, as the run's canvas shows it.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetStepInsightsQuery(string DefinitionId, int Days = 7) : IQuery<PipelineStepInsightsView>;

    public sealed class GetStepInsightsQueryHandler(IPipelineInstanceStore instances, Stomme.Engine.Pipelines.Runtime.IStepExecutionStore executions, ISystemClock clock)
        : IQueryHandler<GetStepInsightsQuery, PipelineStepInsightsView>
    {
        /// <summary>The runs whose passes one read asks for at once.</summary>
        private const int RunsPerRead = 100;

        public async Task<PipelineStepInsightsView> Handle(GetStepInsightsQuery request, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DefinitionId);
            var days = PipelineInsights.Window(request.Days);
            var since = clock.UtcNow - TimeSpan.FromDays(days);
            var page = await instances.SummarizeManyAsync(
                new PipelineInstanceFilter
                {
                    DefinitionId = request.DefinitionId,
                    TimestampFilters = new[]
                    {
                        new TimestampFilter { Column = nameof(PipelineInstance.CreatedAt), Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = since },
                    },
                },
                PageArgs.FromRange(0, PipelineInsights.MaxRunsForSteps + 1),
                new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending), ct);
            var runs = page.Items.Take(PipelineInsights.MaxRunsForSteps).Select(run => run.Id).ToList();

            var passes = new List<PipelineStepRunView>();
            foreach (var chunk in runs.Chunk(RunsPerRead))
            {
                var summaries = await executions.FindManySummariesAsync(new Stomme.Engine.Pipelines.Runtime.Filters.StepExecutionRecordFilter { PipelineInstanceIds = chunk }, ct);
                passes.AddRange(summaries.Where(summary => !ListRunStepsQueryHandler.Wrappers.Contains(summary.StepType)).Select(summary => new PipelineStepRunView
                {
                    StepId = summary.StepId,
                    StepName = summary.StepName!,
                    Status = summary.Status.ToString(),
                    StartedAtUtc = summary.StartedAt.UtcDateTime,
                    CompletedAtUtc = summary.CompletedAt?.UtcDateTime,
                    HandledFailure = Reliability.StepReliability.HandledFailure(summary.Metadata).Handled,
                }));
            }

            return new PipelineStepInsightsView
            {
                Days = days,
                DefinitionId = request.DefinitionId,
                Runs = runs.Count,
                Capped = page.Items.Count > PipelineInsights.MaxRunsForSteps,
                Steps = PipelineInsights.OfSteps(passes),
            };
        }
    }
}
