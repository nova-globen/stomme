#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Dashboards.Application
{
    // ============================================================================================================
    // The Dashboards application layer (dashboard and widget commands,
    // dashboard queries and the widget data query): every request under its key; every handler resolves the board through the
    // REQUEST's site - another site's board is "does not exist"; every mutation is one read and one update of the board
    // the widgets hang off (the per-write rule holds by construction: the aggregate is the unit). The widget's data
    // comes from the Datasets catalogue - the descriptor with its masked summary and the rows only when the
    // classification allows them - after the bound columns are checked against the published schema.
    // ============================================================================================================

    internal static class DashboardViews
    {
        public static DashboardView From(DashboardAggregate dashboard) =>
            new(dashboard.Id, dashboard.Name, dashboard.Status, dashboard.Layout.Columns, dashboard.Layout.RowHeightPx, dashboard.CreatedUtc, dashboard.UpdatedUtc, dashboard.Widgets.Select(From).ToList());

        public static WidgetView From(Widget widget) =>
            new(widget.Id, widget.Title, widget.Kind, widget.Binding.DatasetId, widget.Binding.Columns,
                widget.Position.Column, widget.Position.Row, widget.Position.ColumnSpan, widget.Position.RowSpan,
                widget.Refresh.Kind, widget.Refresh.IntervalSeconds, widget.Interaction.Kind, widget.Interaction.Target, widget.ChartSpec?.EchartsJson,
                new GridStateView(widget.GridState.ColumnWidths.Select(column => new ColumnWidthView(column.Column, column.WidthPx)).ToList(), widget.GridState.SortColumn, widget.GridState.SortDescending, widget.GridState.FilterExpression));
    }

    internal static class WidgetFactory
    {
        public static RefreshMode ToRefreshMode(RefreshKind kind, int? intervalSeconds) => kind switch
        {
            RefreshKind.Manual => RefreshMode.Manual,
            RefreshKind.EventDriven => RefreshMode.EventDriven,
            RefreshKind.Interval => RefreshMode.Interval(intervalSeconds ?? throw new InvalidOperationException("Interval refresh requires an interval (seconds).")),
            _ => RefreshMode.Manual,
        };

        public static Interaction ToInteraction(InteractionKind kind, string? target) => kind == InteractionKind.None ? Interaction.None : new Interaction(kind, target);

        public static ChartSpec? ToChartSpec(WidgetKind kind, string? chartSpecJson) =>
            kind == WidgetKind.Chart ? new ChartSpec(chartSpecJson ?? throw new InvalidOperationException("A chart widget requires a chart spec.")) : null;
    }

    /// <summary>The board of the request's site by id; another site's, or none, is "does not exist".</summary>
    internal static class Boards
    {
        public static async Task<DashboardAggregate> RequireAsync(IDashboardRepository dashboards, SiteState siteState, Guid id, CancellationToken ct)
        {
            var dashboard = await dashboards.GetByIdAsync(id, ct);
            if (dashboard is null || dashboard.SiteId != siteState.Alias?.SiteId)
            {
                throw new InvalidOperationException($"Dashboard '{id}' does not exist.");
            }

            return dashboard;
        }

        public static async Task<DashboardAggregate?> FindAsync(IDashboardRepository dashboards, SiteState siteState, Guid id, CancellationToken ct)
        {
            var dashboard = await dashboards.GetByIdAsync(id, ct);
            return dashboard is null || dashboard.SiteId != siteState.Alias?.SiteId ? null : dashboard;
        }
    }

    // ---------------------------------------------------------------- dashboards

    [Requires(DashboardPermissions.Manage)]
    public sealed record CreateDashboardCommand(string Name, int Columns = 12, int RowHeightPx = 40) : ICommand<Guid>;

    public sealed class CreateDashboardCommandValidator : AbstractValidator<CreateDashboardCommand>
    {
        public CreateDashboardCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().MaximumLength(DashboardLimits.MaxNameLength);
            RuleFor(command => command.Columns).InclusiveBetween(DashboardLimits.MinColumns, DashboardLimits.MaxColumns);
            RuleFor(command => command.RowHeightPx).InclusiveBetween(DashboardLimits.MinRowHeightPx, DashboardLimits.MaxRowHeightPx);
        }
    }

    public sealed class CreateDashboardCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock)
        : ICommandHandler<CreateDashboardCommand, Guid>
    {
        public async Task<Guid> Handle(CreateDashboardCommand request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A dashboard belongs to the request's site; none is resolved.");
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A dashboard belongs to a tenant; none is resolved in this scope.");
            var dashboard = DashboardAggregate.Create(Guid.NewGuid(), tenantId, siteId, request.Name, new LayoutGrid(request.Columns, request.RowHeightPx), clock.GetUtcNow().UtcDateTime);
            await dashboards.AddAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return dashboard.Id;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record UpdateDashboardCommand(Guid Id, string Name) : ICommand<Unit>;

    public sealed class UpdateDashboardCommandValidator : AbstractValidator<UpdateDashboardCommand>
    {
        public UpdateDashboardCommandValidator() => RuleFor(command => command.Name).NotEmpty().MaximumLength(DashboardLimits.MaxNameLength);
    }

    public sealed class UpdateDashboardCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<UpdateDashboardCommand, Unit>
    {
        public async Task<Unit> Handle(UpdateDashboardCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.Id, ct);
            dashboard.Rename(request.Name, clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record CloneDashboardCommand(Guid Id, string NewName) : ICommand<Guid>;

    public sealed class CloneDashboardCommandValidator : AbstractValidator<CloneDashboardCommand>
    {
        public CloneDashboardCommandValidator() => RuleFor(command => command.NewName).NotEmpty().MaximumLength(DashboardLimits.MaxNameLength);
    }

    public sealed class CloneDashboardCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<CloneDashboardCommand, Guid>
    {
        public async Task<Guid> Handle(CloneDashboardCommand request, CancellationToken ct)
        {
            var source = await Boards.RequireAsync(dashboards, siteState, request.Id, ct);
            var clone = source.CloneAs(Guid.NewGuid(), request.NewName, clock.GetUtcNow().UtcDateTime);
            await dashboards.AddAsync(clone, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return clone.Id;
        }
    }

    [Requires(DashboardPermissions.Publish)]
    public sealed record PublishDashboardCommand(Guid Id) : ICommand<Unit>;

    public sealed class PublishDashboardCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<PublishDashboardCommand, Unit>
    {
        public async Task<Unit> Handle(PublishDashboardCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.Id, ct);
            dashboard.Publish(clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record DeleteDashboardCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteDashboardCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState) : ICommandHandler<DeleteDashboardCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteDashboardCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.FindAsync(dashboards, siteState, request.Id, ct);
            if (dashboard is null)
            {
                return Unit.Value; // idempotent
            }

            await dashboards.DeleteAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    // ---------------------------------------------------------------- widgets

    [Requires(DashboardPermissions.Manage)]
    public sealed record AddWidgetCommand(
        Guid DashboardId, string Title, WidgetKind Kind, Guid DatasetId, IReadOnlyList<string> Columns,
        int Column, int Row, int ColumnSpan, int RowSpan,
        RefreshKind RefreshKind = RefreshKind.Manual, int? IntervalSeconds = null,
        InteractionKind InteractionKind = InteractionKind.None, string? Target = null, string? ChartSpecJson = null) : ICommand<Guid>;

    public sealed class AddWidgetCommandValidator : AbstractValidator<AddWidgetCommand>
    {
        public AddWidgetCommandValidator()
        {
            RuleFor(command => command.Title).NotEmpty().MaximumLength(DashboardLimits.MaxWidgetTitleLength);
            RuleFor(command => command.ColumnSpan).GreaterThan(0);
            RuleFor(command => command.RowSpan).GreaterThan(0);
        }
    }

    public sealed class AddWidgetCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<AddWidgetCommand, Guid>
    {
        public async Task<Guid> Handle(AddWidgetCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            var widget = Widget.Create(Guid.NewGuid(), request.Title, request.Kind, new DataBinding(request.DatasetId, request.Columns), new GridPosition(request.Column, request.Row, request.ColumnSpan, request.RowSpan),
                WidgetFactory.ToRefreshMode(request.RefreshKind, request.IntervalSeconds), WidgetFactory.ToInteraction(request.InteractionKind, request.Target), WidgetFactory.ToChartSpec(request.Kind, request.ChartSpecJson));
            dashboard.AddWidget(widget, clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return widget.Id;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record UpdateWidgetCommand(Guid DashboardId, Guid WidgetId, string Title, RefreshKind RefreshKind, int? IntervalSeconds, InteractionKind InteractionKind, string? Target, string? ChartSpecJson) : ICommand<Unit>;

    public sealed class UpdateWidgetCommandValidator : AbstractValidator<UpdateWidgetCommand>
    {
        public UpdateWidgetCommandValidator() => RuleFor(command => command.Title).NotEmpty().MaximumLength(DashboardLimits.MaxWidgetTitleLength);
    }

    public sealed class UpdateWidgetCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<UpdateWidgetCommand, Unit>
    {
        public async Task<Unit> Handle(UpdateWidgetCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            var kind = dashboard.Widgets.SingleOrDefault(widget => widget.Id == request.WidgetId)?.Kind ?? throw new InvalidOperationException($"Widget '{request.WidgetId}' is not on this dashboard.");
            dashboard.ReconfigureWidget(request.WidgetId, request.Title, WidgetFactory.ToRefreshMode(request.RefreshKind, request.IntervalSeconds), WidgetFactory.ToInteraction(request.InteractionKind, request.Target),
                WidgetFactory.ToChartSpec(kind, request.ChartSpecJson), clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record MoveWidgetCommand(Guid DashboardId, Guid WidgetId, int Column, int Row, int ColumnSpan, int RowSpan) : ICommand<Unit>;

    public sealed class MoveWidgetCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<MoveWidgetCommand, Unit>
    {
        public async Task<Unit> Handle(MoveWidgetCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            dashboard.MoveWidget(request.WidgetId, new GridPosition(request.Column, request.Row, request.ColumnSpan, request.RowSpan), clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record RemoveWidgetCommand(Guid DashboardId, Guid WidgetId) : ICommand<Unit>;

    public sealed class RemoveWidgetCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<RemoveWidgetCommand, Unit>
    {
        public async Task<Unit> Handle(RemoveWidgetCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            dashboard.RemoveWidget(request.WidgetId, clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record RearrangeLayoutCommand(Guid DashboardId, int Columns, int RowHeightPx) : ICommand<Unit>;

    public sealed class RearrangeLayoutCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<RearrangeLayoutCommand, Unit>
    {
        public async Task<Unit> Handle(RearrangeLayoutCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            dashboard.RearrangeLayout(new LayoutGrid(request.Columns, request.RowHeightPx), clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record SaveGridStateCommand(Guid DashboardId, Guid WidgetId, IReadOnlyList<ColumnWidth> ColumnWidths, string? SortColumn, bool SortDescending, string? FilterExpression) : ICommand<Unit>;

    public sealed class SaveGridStateCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<SaveGridStateCommand, Unit>
    {
        public async Task<Unit> Handle(SaveGridStateCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            dashboard.SaveWidgetGridState(request.WidgetId, new GridState(request.ColumnWidths, request.SortColumn, request.SortDescending, request.FilterExpression), clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DashboardPermissions.Manage)]
    public sealed record ReorderWidgetCommand(Guid DashboardId, Guid WidgetId, bool Forward) : ICommand<Unit>;

    public sealed class ReorderWidgetCommandHandler(IDashboardRepository dashboards, IUnitOfWork unitOfWork, SiteState siteState, TimeProvider clock) : ICommandHandler<ReorderWidgetCommand, Unit>
    {
        public async Task<Unit> Handle(ReorderWidgetCommand request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            dashboard.ReorderWidget(request.WidgetId, request.Forward, clock.GetUtcNow().UtcDateTime);
            await dashboards.UpdateAsync(dashboard, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    // ---------------------------------------------------------------- queries

    [Requires(DashboardPermissions.View)]
    public sealed record ListDashboardsQuery : IQuery<IReadOnlyList<DashboardView>>;

    public sealed class ListDashboardsQueryHandler(IDashboardRepository dashboards, SiteState siteState) : IQueryHandler<ListDashboardsQuery, IReadOnlyList<DashboardView>>
    {
        public async Task<IReadOnlyList<DashboardView>> Handle(ListDashboardsQuery request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("Dashboards belong to the request's site; none is resolved.");
            return (await dashboards.ListForSiteAsync(siteId, ct)).Select(DashboardViews.From).ToList();
        }
    }

    [Requires(DashboardPermissions.View)]
    public sealed record GetDashboardQuery(Guid Id) : IQuery<DashboardView?>;

    public sealed class GetDashboardQueryHandler(IDashboardRepository dashboards, SiteState siteState) : IQueryHandler<GetDashboardQuery, DashboardView?>
    {
        public async Task<DashboardView?> Handle(GetDashboardQuery request, CancellationToken ct)
        {
            var dashboard = await Boards.FindAsync(dashboards, siteState, request.Id, ct);
            return dashboard is null ? null : DashboardViews.From(dashboard);
        }
    }

    [Requires(DashboardPermissions.View)]
    public sealed record GetWidgetDataQuery(Guid DashboardId, Guid WidgetId) : IQuery<WidgetDataView>;

    public sealed class GetWidgetDataQueryHandler(IDashboardRepository dashboards, SiteState siteState, IDatasetCatalog catalog) : IQueryHandler<GetWidgetDataQuery, WidgetDataView>
    {
        public async Task<WidgetDataView> Handle(GetWidgetDataQuery request, CancellationToken ct)
        {
            var dashboard = await Boards.RequireAsync(dashboards, siteState, request.DashboardId, ct);
            var widget = dashboard.Widgets.SingleOrDefault(candidate => candidate.Id == request.WidgetId) ?? throw new InvalidOperationException($"Widget '{request.WidgetId}' is not on this dashboard.");
            var descriptor = await catalog.GetDescriptorAsync(widget.Binding.DatasetId, ct)
                ?? throw new InvalidOperationException($"The dataset '{widget.Binding.DatasetId}' bound to widget '{request.WidgetId}' does not exist.");

            // Every bound column must exist in the dataset's published schema.
            var schemaColumns = descriptor.Schema.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (widget.Binding.Columns.FirstOrDefault(column => !schemaColumns.Contains(column)) is { } unknown)
            {
                throw new InvalidOperationException($"Widget '{request.WidgetId}' binds column '{unknown}', which does not exist in dataset '{descriptor.Name}'.");
            }

            var rows = await catalog.GetPreviewRowsAsync(widget.Binding.DatasetId, ct);
            return new WidgetDataView(widget.Id, descriptor.Id, descriptor.Name, descriptor.Classification, widget.Binding.Columns, descriptor.Summary, rows.Allowed, rows.RowsJson, rows.Reason);
        }
    }

    /// <summary>The Datasets module's delete guard, answered by the boards of the request's site: a dataset a widget binds cannot be deleted.</summary>
    public sealed class DashboardDatasetUsageProvider(IDashboardRepository dashboards, SiteState siteState) : IDatasetUsageProvider
    {
        public async Task<IReadOnlyList<string>> FindReferencesAsync(Guid datasetId, CancellationToken ct = default)
        {
            var siteId = siteState.Alias?.SiteId;
            if (siteId is null)
            {
                return Array.Empty<string>();
            }

            return (await dashboards.ListForSiteAsync(siteId.Value, ct))
                .SelectMany(dashboard => dashboard.Widgets.Where(widget => widget.Binding.DatasetId == datasetId).Select(widget => $"widget '{widget.Title}' on dashboard '{dashboard.Name}'"))
                .ToList();
        }
    }
}
