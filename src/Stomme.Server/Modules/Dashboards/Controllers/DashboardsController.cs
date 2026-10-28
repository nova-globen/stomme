#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Dashboards;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Dashboards.Controllers
{
    /// <summary>
    /// <c>api/Dashboards</c> (BL-96 increment 9): the site's boards under <c>dashboards:view</c>, every mutation under
    /// <c>dashboards:manage</c>, the publish under <c>dashboards:publish</c>; a refused command (a rule of the board,
    /// a validation) answers with the reason, a board of another site is not found.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class DashboardsController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public DashboardsController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Dashboards
        [HttpGet]
        [RequiresPermissionKey(DashboardPermissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _mediator.Send(new ListDashboardsQuery(), ct));

        // GET api/Dashboards/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(DashboardPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetDashboardQuery(id), ct) is { } dashboard ? Ok(dashboard) : NotFound();

        // GET api/Dashboards/{id}/widgets/{widgetId}/data
        [HttpGet("{id:guid}/widgets/{widgetId:guid}/data")]
        [RequiresPermissionKey(DashboardPermissions.View)]
        public async Task<IActionResult> WidgetData(Guid id, Guid widgetId, CancellationToken ct) => await RunAsync(() => _mediator.Send(new GetWidgetDataQuery(id, widgetId), ct));

        // POST api/Dashboards
        [HttpPost]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> Create([FromBody] CreateDashboardRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await RunAsync(async () => await _mediator.Send(new GetDashboardQuery(await _mediator.Send(new CreateDashboardCommand(request.Name, request.Columns, request.RowHeightPx), ct)), ct));

        // PUT api/Dashboards/{id} - rename
        [HttpPut("{id:guid}")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> Rename(Guid id, [FromBody] RenameDashboardRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new UpdateDashboardCommand(id, request.Name), ct);

        // POST api/Dashboards/{id}/clone
        [HttpPost("{id:guid}/clone")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> Clone(Guid id, [FromBody] CloneDashboardRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await RunAsync(async () => await _mediator.Send(new GetDashboardQuery(await _mediator.Send(new CloneDashboardCommand(id, request.NewName), ct)), ct));

        // POST api/Dashboards/{id}/publish
        [HttpPost("{id:guid}/publish")]
        [RequiresPermissionKey(DashboardPermissions.Publish)]
        public async Task<IActionResult> Publish(Guid id, CancellationToken ct) => await MutateAsync(id, new PublishDashboardCommand(id), ct);

        // POST api/Dashboards/{id}/layout
        [HttpPost("{id:guid}/layout")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> Layout(Guid id, [FromBody] LayoutRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new RearrangeLayoutCommand(id, request.Columns, request.RowHeightPx), ct);

        // DELETE api/Dashboards/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await _mediator.Send(new DeleteDashboardCommand(id), ct);
            return NoContent();
        }

        // POST api/Dashboards/{id}/widgets
        [HttpPost("{id:guid}/widgets")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> AddWidget(Guid id, [FromBody] AddWidgetRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new AddWidgetCommand(id, request.Title, request.Kind, request.DatasetId, request.Columns ?? new(), request.Column, request.Row, request.ColumnSpan, request.RowSpan,
                request.RefreshKind, request.IntervalSeconds, request.Interaction, request.Target, request.ChartSpecJson), ct);

        // PUT api/Dashboards/{id}/widgets/{widgetId}
        [HttpPut("{id:guid}/widgets/{widgetId:guid}")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> UpdateWidget(Guid id, Guid widgetId, [FromBody] UpdateWidgetRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new UpdateWidgetCommand(id, widgetId, request.Title, request.RefreshKind, request.IntervalSeconds, request.Interaction, request.Target, request.ChartSpecJson), ct);

        // POST api/Dashboards/{id}/widgets/{widgetId}/move
        [HttpPost("{id:guid}/widgets/{widgetId:guid}/move")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> MoveWidget(Guid id, Guid widgetId, [FromBody] MoveWidgetRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new MoveWidgetCommand(id, widgetId, request.Column, request.Row, request.ColumnSpan, request.RowSpan), ct);

        // PUT api/Dashboards/{id}/widgets/{widgetId}/grid-state
        [HttpPut("{id:guid}/widgets/{widgetId:guid}/grid-state")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> SaveGridState(Guid id, Guid widgetId, [FromBody] GridStateRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new SaveGridStateCommand(id, widgetId, (request.ColumnWidths ?? new()).Select(column => new ColumnWidth(column.Column, column.WidthPx)).ToList(), request.SortColumn, request.SortDescending, request.FilterExpression), ct);

        // POST api/Dashboards/{id}/widgets/{widgetId}/reorder
        [HttpPost("{id:guid}/widgets/{widgetId:guid}/reorder")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> ReorderWidget(Guid id, Guid widgetId, [FromBody] ReorderWidgetRequest request, CancellationToken ct) =>
            request is null ? BadRequest() : await MutateAsync(id, new ReorderWidgetCommand(id, widgetId, request.Forward), ct);

        // DELETE api/Dashboards/{id}/widgets/{widgetId}
        [HttpDelete("{id:guid}/widgets/{widgetId:guid}")]
        [RequiresPermissionKey(DashboardPermissions.Manage)]
        public async Task<IActionResult> RemoveWidget(Guid id, Guid widgetId, CancellationToken ct) => await MutateAsync(id, new RemoveWidgetCommand(id, widgetId), ct);

        /// <summary>Sends the mutation and answers with the board as it now is; a rule of the board or a validation answers with the reason.</summary>
        private Task<IActionResult> MutateAsync<T>(Guid id, IRequest<T> command, CancellationToken ct) => RunAsync(async () =>
        {
            await _mediator.Send(command, ct);
            return await _mediator.Send(new GetDashboardQuery(id), ct);
        });

        private async Task<IActionResult> RunAsync<T>(Func<Task<T>> work)
        {
            try
            {
                return await work() is { } result ? Ok(result) : NotFound();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ValidationException)
            {
                return Refused(ex);
            }
        }

        // A rule of the board or the caller's input, answered as such - 400 with the domain's own message; a validation as a problem.
        private IActionResult Refused(Exception ex)
        {
            if (ex is ValidationException validation)
            {
                foreach (var failure in validation.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }

            return BadRequest(new { error = ex.Message });
        }
    }
}
