#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Mediator;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.DataExport.Application
{
    /// <summary>
    /// Recurring exports (D108's correction of 2026-10-05): the schedule is authored by a person who may make the
    /// export NOW - its request is validated by the request path's own validator - and every run is requested as its owner,
    /// re-authorised then (<c>ExportSchedulePass</c>). A manager sees and manages every schedule of the site and may take one
    /// over; anyone else, their own.
    /// </summary>
    internal static class ExportScheduleWire
    {
        public static ExportScheduleView ToView(ExportScheduleAggregate schedule) => new()
        {
            Id = schedule.Id,
            Name = schedule.Name,
            OwnerUserId = schedule.OwnerUserId,
            SourceKey = schedule.SourceKey,
            Format = schedule.Format.ToString(),
            Cron = schedule.Cron,
            IsPaused = schedule.IsPaused,
            PauseReason = schedule.PauseReason.ToString(),
            NextRunOnUtc = schedule.NextRunOnUtc,
            LastRunOnUtc = schedule.LastRunOnUtc,
            LastJobId = schedule.LastJobId,
        };

        /// <summary>The schedule IF the caller may manage it: their own, or any on the site for a manage holder. One answer, null, for every other case.</summary>
        public static async Task<ExportScheduleAggregate?> FindManageableAsync(ExportCaller caller, IExportScheduleRepository schedules, Guid id, CancellationToken ct)
        {
            var schedule = await schedules.GetForSiteAsync(id, caller.SiteId, ct);
            if (schedule is null)
            {
                return null;
            }

            return schedule.OwnerUserId == caller.UserId || await caller.HoldsAsync(ExportPermissions.Manage, ct) ? schedule : null;
        }
    }

    [Requires(ExportPermissions.Create)]
    public sealed record CreateExportScheduleCommand(ExportScheduleRequest Schedule) : ICommand<ExportScheduleView>;

    public sealed class CreateExportScheduleCommandHandler(
        ExportCaller caller,
        ExportSourceRegistry sources,
        ExportRendererRegistry renderers,
        IExportScheduleRepository schedules,
        IExportClock clock)
        : ICommandHandler<CreateExportScheduleCommand, ExportScheduleView>
    {
        public async Task<ExportScheduleView> Handle(CreateExportScheduleCommand command, CancellationToken ct)
        {
            var input = command.Schedule ?? throw new ExportRequestRejectedException("A schedule is required.");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > ExportScheduleAggregate.MaximumNameLength)
            {
                throw new ExportRequestRejectedException($"A schedule needs a name of at most {ExportScheduleAggregate.MaximumNameLength} characters.");
            }

            if (ExportScheduleAggregate.CronProblem(input.Cron) is { } problem)
            {
                throw new ExportRequestRejectedException(problem);
            }

            // The request path's own rules: a schedule can never hold a request a person could not make now.
            var (source, format, optionsJson) = await ExportRequestValidator.ValidateAsync(caller, sources, renderers, input.Request, ct);

            var schedule = ExportScheduleAggregate.Create(
                Guid.NewGuid(), caller.TenantId, caller.SiteId, caller.UserId, input.Name, source.Key, format,
                input.Request.ArgumentsJson, optionsJson, input.Cron, clock.UtcNow);
            await schedules.AddAsync(schedule, ct);
            return ExportScheduleWire.ToView(schedule);
        }
    }

    [Requires(ExportPermissions.View)]
    public sealed record ListExportSchedulesQuery : IQuery<IReadOnlyList<ExportScheduleView>>;

    public sealed class ListExportSchedulesQueryHandler(ExportCaller caller, IExportScheduleRepository schedules)
        : IQueryHandler<ListExportSchedulesQuery, IReadOnlyList<ExportScheduleView>>
    {
        public async Task<IReadOnlyList<ExportScheduleView>> Handle(ListExportSchedulesQuery request, CancellationToken ct)
        {
            var found = await caller.HoldsAsync(ExportPermissions.Manage, ct)
                ? await schedules.ListBySiteAsync(caller.SiteId, ct)
                : await schedules.ListByOwnerAsync(caller.SiteId, caller.UserId, ct);
            return found.OrderBy(schedule => schedule.Name, StringComparer.CurrentCultureIgnoreCase).Select(ExportScheduleWire.ToView).ToList();
        }
    }

    /// <summary>What to do with a schedule the caller may manage.</summary>
    public enum ExportScheduleAction
    {
        Pause = 0,
        Resume = 1,
        Delete = 2,
    }

    [Requires(ExportPermissions.Create)]
    public sealed record ChangeExportScheduleCommand(Guid Id, ExportScheduleAction Action) : ICommand<bool>;

    public sealed class ChangeExportScheduleCommandHandler(ExportCaller caller, IExportScheduleRepository schedules, IExportClock clock)
        : ICommandHandler<ChangeExportScheduleCommand, bool>
    {
        /// <returns>False when there is no such schedule FOR THIS CALLER.</returns>
        public async Task<bool> Handle(ChangeExportScheduleCommand request, CancellationToken ct)
        {
            var schedule = await ExportScheduleWire.FindManageableAsync(caller, schedules, request.Id, ct);
            if (schedule is null)
            {
                return false;
            }

            // One write per action, to the instance just read.
            switch (request.Action)
            {
                case ExportScheduleAction.Pause:
                    schedule.Pause(ExportSchedulePauseReason.Manual, clock.UtcNow);
                    await schedules.UpdateAsync(schedule, ct);
                    return true;
                case ExportScheduleAction.Resume:
                    schedule.Resume(clock.UtcNow);
                    await schedules.UpdateAsync(schedule, ct);
                    return true;
                case ExportScheduleAction.Delete:
                    await schedules.DeleteAsync(schedule, ct);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>A manager takes a schedule over: every later run is requested as them (D108's correction).</summary>
    [Requires(ExportPermissions.Manage)]
    public sealed record TakeOverExportScheduleCommand(Guid Id) : ICommand<bool>;

    public sealed class TakeOverExportScheduleCommandHandler(ExportCaller caller, IExportScheduleRepository schedules)
        : ICommandHandler<TakeOverExportScheduleCommand, bool>
    {
        public async Task<bool> Handle(TakeOverExportScheduleCommand request, CancellationToken ct)
        {
            var schedule = await schedules.GetForSiteAsync(request.Id, caller.SiteId, ct);
            if (schedule is null)
            {
                return false;
            }

            schedule.TakeOver(caller.UserId);
            await schedules.UpdateAsync(schedule, ct);
            return true;
        }
    }
}
