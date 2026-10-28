#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.Dashboards.Services;
using Stomme.Shared;

namespace Stomme.Modules.Dashboards.Services
{
    /// <summary>The page's service on the server: the same commands and queries the API sends, through the mediator (the Guestbook pattern).</summary>
    public sealed class ServerDashboardsService(IMediator mediator) : IDashboardsService, ITransientService
    {
        public async Task<List<DashboardView>> ListAsync() => (await mediator.Send(new ListDashboardsQuery())).ToList();

        public Task<DashboardView> GetAsync(Guid id) => mediator.Send(new GetDashboardQuery(id))!;

        public Task<WidgetDataView> GetWidgetDataAsync(Guid id, Guid widgetId) => mediator.Send(new GetWidgetDataQuery(id, widgetId));

        public async Task<DashboardView> CreateAsync(CreateDashboardRequest request) => (await mediator.Send(new GetDashboardQuery(await mediator.Send(new CreateDashboardCommand(request.Name, request.Columns, request.RowHeightPx)))))!;

        public Task<DashboardView> RenameAsync(Guid id, RenameDashboardRequest request) => AfterAsync(id, mediator.Send(new UpdateDashboardCommand(id, request.Name)));

        public async Task<DashboardView> CloneAsync(Guid id, CloneDashboardRequest request) => (await mediator.Send(new GetDashboardQuery(await mediator.Send(new CloneDashboardCommand(id, request.NewName)))))!;

        public Task<DashboardView> PublishAsync(Guid id) => AfterAsync(id, mediator.Send(new PublishDashboardCommand(id)));

        public Task<DashboardView> RearrangeLayoutAsync(Guid id, LayoutRequest request) => AfterAsync(id, mediator.Send(new RearrangeLayoutCommand(id, request.Columns, request.RowHeightPx)));

        public Task DeleteAsync(Guid id) => mediator.Send(new DeleteDashboardCommand(id));

        public Task<DashboardView> AddWidgetAsync(Guid id, AddWidgetRequest request) => AfterAsync(id, mediator.Send(new AddWidgetCommand(id, request.Title, request.Kind, request.DatasetId, request.Columns ?? new(), request.Column, request.Row, request.ColumnSpan, request.RowSpan,
            request.RefreshKind, request.IntervalSeconds, request.Interaction, request.Target, request.ChartSpecJson)));

        public Task<DashboardView> UpdateWidgetAsync(Guid id, Guid widgetId, UpdateWidgetRequest request) => AfterAsync(id, mediator.Send(new UpdateWidgetCommand(id, widgetId, request.Title, request.RefreshKind, request.IntervalSeconds, request.Interaction, request.Target, request.ChartSpecJson)));

        public Task<DashboardView> MoveWidgetAsync(Guid id, Guid widgetId, MoveWidgetRequest request) => AfterAsync(id, mediator.Send(new MoveWidgetCommand(id, widgetId, request.Column, request.Row, request.ColumnSpan, request.RowSpan)));

        public Task<DashboardView> SaveGridStateAsync(Guid id, Guid widgetId, GridStateRequest request) => AfterAsync(id, mediator.Send(new SaveGridStateCommand(id, widgetId, (request.ColumnWidths ?? new()).Select(column => new ColumnWidth(column.Column, column.WidthPx)).ToList(), request.SortColumn, request.SortDescending, request.FilterExpression)));

        public Task<DashboardView> ReorderWidgetAsync(Guid id, Guid widgetId, ReorderWidgetRequest request) => AfterAsync(id, mediator.Send(new ReorderWidgetCommand(id, widgetId, request.Forward)));

        public Task<DashboardView> RemoveWidgetAsync(Guid id, Guid widgetId) => AfterAsync(id, mediator.Send(new RemoveWidgetCommand(id, widgetId)));

        private async Task<DashboardView> AfterAsync<T>(Guid id, Task<T> mutation)
        {
            await mutation;
            return (await mediator.Send(new GetDashboardQuery(id)))!;
        }
    }
}
