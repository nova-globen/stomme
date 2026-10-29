#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// What runs next (<see cref="PipelineUpcoming"/>): every published pipeline's schedules that start it, their next runs
    /// in the next day or week, read off the published bodies with the schedules task's own rules - its cron (Cronos), in its time
    /// zone. For anyone who may view the pipelines.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListUpcomingRunsQuery(int Hours = 24) : IQuery<PipelineUpcomingView>;

    public sealed class ListUpcomingRunsQueryHandler(IPipelineDefinitionStore definitions, IOptions<PipelineTriggerOptions> options, TimeProvider clock)
        : IQueryHandler<ListUpcomingRunsQuery, PipelineUpcomingView>
    {
        public async Task<PipelineUpcomingView> Handle(ListUpcomingRunsQuery request, CancellationToken ct)
        {
            var hours = PipelineUpcoming.Window(request.Hours);
            var now = clock.GetUtcNow().UtcDateTime;
            var until = now.AddHours(hours);
            var view = new PipelineUpcomingView { Hours = hours, Enabled = options.Value.Schedules.Enabled };
            var published = await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Published }, ct);
            foreach (var definition in published)
            {
                PipelineGraph graph;
                try
                {
                    graph = PipelineGraphCodec.ToGraph(definition.StringData);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                {
                    continue; // a body the editor cannot read has no schedule to list
                }

                // A schedule starts its pipeline when nothing leads to it, it sits in the pipeline itself and it is on.
                foreach (var step in graph.Steps.Where(step => step.Type == GetTriggerInfoQueryHandler.ScheduleType && !step.Disabled && string.IsNullOrEmpty(step.Parent)
                    && !graph.Connections.Any(connection => connection.TargetId == step.Id)))
                {
                    var cronText = step.Inputs.TryGetValue("Cron", out var cronValue) ? cronValue : null;
                    step.Inputs.TryGetValue("TimeZone", out var zoneText);
                    if (Schedule.TryParseAll(cronText, out _) is not { } crons || Schedule.FindZone(zoneText, out _) is not { } zone)
                    {
                        continue; // the editor says why on the schedule itself
                    }

                    var schedule = new PipelineUpcomingSchedule
                    {
                        DefinitionId = definition.DefinitionId,
                        PipelineName = definition.Name,
                        StepId = step.Id,
                        StepName = step.Name,
                        Cron = string.Join("; ", Schedule.Lines(cronText)),
                        TimeZone = Schedule.NormalizeZone(zoneText),
                    };
                    // Every cron line's runs in the window, merged in order (n8n's trigger rules).
                    foreach (var next in Schedule.Next(crons, now, zone, PipelineUpcoming.MaxCounted, until))
                    {
                        if (schedule.NextRunsUtc.Count < PipelineUpcoming.Shown)
                        {
                            schedule.NextRunsUtc.Add(next);
                        }

                        schedule.RunsInWindow++;
                    }

                    if (schedule.RunsInWindow > 0)
                    {
                        view.Schedules.Add(schedule);
                    }
                }
            }

            view.Schedules = view.Schedules.OrderBy(schedule => schedule.NextRunsUtc[0])
                .ThenBy(schedule => schedule.PipelineName, StringComparer.CurrentCultureIgnoreCase).ToList();
            return view;
        }
    }
}
