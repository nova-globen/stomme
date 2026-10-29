#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Metering;
using Stomme.Models.Metering;
using Stomme.Modules.Metering.Application;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Metering.Controllers
{
    /// <summary>
    /// The Metering module's Web API (<c>api/Metering</c>): the caller's own consumption, the site's
    /// usage log and AI dashboard, and the quota surface. Every action dispatches through <see cref="IMediator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TWO gates per action, deliberately the SAME key.</b> <c>[RequiresPermissionKey]</c> is decided by the
    /// authorization middleware before the action runs; the request's own <c>[Requires]</c> is decided again by the
    /// mediator's authorization behavior - both over the site's <c>(metering, -1, operation)</c> rows, so they
    /// cannot disagree. the predecessor platform's four AI endpoints reached their queries with three keys between them and its
    /// usage pages hand-checked <c>HasClaim</c>; nothing here is gated by a page.
    /// </para>
    /// <para>
    /// <b>No action takes a site, a tenant or a user id.</b> All three are resolved server-side, which is what makes
    /// the "my usage" endpoint safe to expose to every registered user: there is no parameter to forge.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class MeteringController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public MeteringController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // ---- self-service (ViewOwn) ------------------------------------------------------------------------------

        // GET api/Metering/mine
        [HttpGet("mine")]
        [RequiresPermissionKey(MeteringPermissions.ViewOwn)]
        public async Task<IReadOnlyList<MyUsageLine>> Mine(CancellationToken ct) =>
            await _mediator.Send(new GetMyUsageQuery(), ct);

        // ---- the site's reads (View) -----------------------------------------------------------------------------

        // GET api/Metering/quotas
        [HttpGet("quotas")]
        [RequiresPermissionKey(MeteringPermissions.View)]
        public async Task<IReadOnlyList<QuotaView>> Quotas(CancellationToken ct) =>
            await _mediator.Send(new ListQuotasQuery(), ct);

        // GET api/Metering/quotas/{id}
        [HttpGet("quotas/{id:guid}")]
        [RequiresPermissionKey(MeteringPermissions.View)]
        public Task<IActionResult> Quota(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var quota = await _mediator.Send(new GetQuotaQuery(id), ct);
                return quota is null ? NotFound() : (IActionResult)Ok(quota);
            });

        // GET api/Metering/usage
        [HttpGet("usage")]
        [RequiresPermissionKey(MeteringPermissions.View)]
        public async Task<IReadOnlyList<UsageRecordView>> Usage([FromQuery] string? metric, [FromQuery] int take, CancellationToken ct) =>
            await _mediator.Send(new ListUsageQuery(metric, take <= 0 ? 200 : take), ct);

        // GET api/Metering/ai
        [HttpGet("ai")]
        [RequiresPermissionKey(MeteringPermissions.View)]
        public async Task<AiUsageDashboard> AiDashboard([FromQuery] int days, CancellationToken ct) =>
            await _mediator.Send(new GetAiUsageDashboardQuery(days <= 0 ? 30 : days), ct);

        // GET api/Metering/ai/calls
        [HttpGet("ai/calls")]
        [RequiresPermissionKey(MeteringPermissions.View)]
        public async Task<IReadOnlyList<AiUsageRecordView>> AiCalls([FromQuery] int take, CancellationToken ct) =>
            await _mediator.Send(new ListAiUsageQuery(take <= 0 ? 200 : take), ct);

        // ---- the operator's writes (Manage) ----------------------------------------------------------------------

        // POST api/Metering/quotas
        [HttpPost("quotas")]
        [RequiresPermissionKey(MeteringPermissions.Manage)]
        public Task<IActionResult> Define([FromBody] QuotaDefinition definition, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var id = await _mediator.Send(new DefineQuotaCommand(definition.Metric, definition.Period, definition.Limit, definition.WarnAtPercent), ct);
                return Ok(id);
            });

        // POST api/Metering/quotas/{id}/reset
        [HttpPost("quotas/{id:guid}/reset")]
        [RequiresPermissionKey(MeteringPermissions.Manage)]
        public Task<IActionResult> Reset(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new ResetQuotaCommand(id), ct);
                return NoContent();
            });

        // POST api/Metering/quotas/{id}/override
        [HttpPost("quotas/{id:guid}/override")]
        [RequiresPermissionKey(MeteringPermissions.Manage)]
        public Task<IActionResult> Override(Guid id, [FromBody] QuotaOverrideRequest request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new GrantQuotaOverrideCommand(id, request.Additional, request.ExpiresUtc), ct);
                return NoContent();
            });

        // POST api/Metering/quotas/{id}/enforcement/{enforced}
        [HttpPost("quotas/{id:guid}/enforcement/{enforced:bool}")]
        [RequiresPermissionKey(MeteringPermissions.Manage)]
        public Task<IActionResult> Enforcement(Guid id, bool enforced, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new SetQuotaEnforcementCommand(id, enforced), ct);
                return NoContent();
            });

        // DELETE api/Metering/quotas/{id}
        [HttpDelete("quotas/{id:guid}")]
        [RequiresPermissionKey(MeteringPermissions.Manage)]
        public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new DeleteQuotaCommand(id), ct);
                return NoContent();
            });

        /// <summary>
        /// A domain rule the command refuses (an unknown id, a non-canonical metric key, an unparsable period, a
        /// negative allowance) and a validation failure are a 400 with the reason - never a 500 and never a silent
        /// success. A permission denial never reaches here: it is a 403 from the middleware or from
        /// <c>AccessDeniedExceptionFilter</c>.
        /// </summary>
        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (FluentValidation.ValidationException ex)
            {
                foreach (var failure in ex.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "The metering request was refused.");
            }
        }
    }
}
