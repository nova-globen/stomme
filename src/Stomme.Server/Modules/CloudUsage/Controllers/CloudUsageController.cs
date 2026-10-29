#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.CloudUsage.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.CloudUsage.Controllers
{
    /// <summary>
    /// <c>api/CloudUsage</c>: the site's rollups and budgets under
    /// <c>cloud-usage:view</c>, the sync and the budget upsert under <c>cloud-usage:manage</c>. A sync that is refused
    /// answers 200 with its code and reason in the body (the refusal is the answer, as the sync reports it); a budget the
    /// domain refuses answers 400 with the domain's message; a window of dates is <c>from</c>/<c>to</c> as yyyy-MM-dd.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class CloudUsageController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public CloudUsageController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/CloudUsage/summary?from=2026-09-01&to=2026-09-30
        [HttpGet("summary")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
            Ok(await _mediator.Send(new GetCloudCostSummaryQuery(from, to), ct));

        // GET api/CloudUsage/by-provider
        [HttpGet("by-provider")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> ByProvider([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
            Ok(await _mediator.Send(new GetCloudCostByProviderQuery(from, to), ct));

        // GET api/CloudUsage/by-service?top=10
        [HttpGet("by-service")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> ByService([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? top, CancellationToken ct) =>
            Ok(await _mediator.Send(new GetCloudCostByServiceQuery(from, to, top), ct));

        // GET api/CloudUsage/month-over-month?month=2026-09-01
        [HttpGet("month-over-month")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> MonthOverMonth([FromQuery] DateOnly? month, CancellationToken ct) =>
            Ok(await _mediator.Send(new GetCloudCostMonthOverMonthQuery(month), ct));

        // GET api/CloudUsage/budget-status?month=2026-09-01
        [HttpGet("budget-status")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> BudgetStatus([FromQuery] DateOnly? month, CancellationToken ct) =>
            Ok(await _mediator.Send(new GetCloudCostBudgetStatusQuery(month), ct));

        // GET api/CloudUsage/budgets
        [HttpGet("budgets")]
        [RequiresPermissionKey(CloudUsagePermissions.View)]
        public async Task<IActionResult> Budgets(CancellationToken ct) => Ok(await _mediator.Send(new ListCloudBudgetsQuery(), ct));

        // PUT api/CloudUsage/budgets - one budget per scope; a second set for a scope amends it
        [HttpPut("budgets")]
        [RequiresPermissionKey(CloudUsagePermissions.Manage)]
        public async Task<IActionResult> SetBudget([FromBody] SetCloudBudgetRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                return Ok(await _mediator.Send(new SetCloudBudgetCommand(request.MonthlyAmount, request.Currency, request.Provider), ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ValidationException)
            {
                return Refused(ex);
            }
        }

        // POST api/CloudUsage/sync
        [HttpPost("sync")]
        [RequiresPermissionKey(CloudUsagePermissions.Manage)]
        public async Task<IActionResult> Sync([FromBody] SyncCloudCostsRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            return Ok(await _mediator.Send(new SyncCloudCostsCommand(request.ConnectionId, request.Provider, request.Window, request.TargetDataset, request.DatasetScope), ct));
        }

        // A rule of the domain or the caller's input, answered as such - 400 with the domain's own message; a validation as a problem.
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
