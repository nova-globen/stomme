#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Health;
using Stomme.Modules.Health.Application;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Health.Controllers
{
    /// <summary>
    /// The Health module's Web API (<c>api/Health</c>): this site's current health, its rollup
    /// history, and acknowledging an alert. Every action dispatches through <see cref="IMediator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TWO gates per action, deliberately the SAME key.</b> <c>[RequiresPermissionKey]</c> is decided by the
    /// authorization middleware before the action runs; the request's own <c>[Requires]</c> is decided again by the
    /// mediator's authorization behaviour — both over the site's <c>(health, -1, operation)</c> rows, so they
    /// cannot disagree.
    /// </para>
    /// <para>
    /// <b>No READ executes a probe.</b> Every read answers from rows the JOB wrote. That is not an optimisation: an
    /// endpoint that probes on demand lets a caller drive database round-trips, cache writes and TCP connects to the
    /// site's mail server at whatever rate they can issue requests. The one exception is <c>run</c>:
    /// <c>health:manage</c> only, one in flight per site (a second gets 409), bounded by the job's tick budget.
    /// </para>
    /// <para>
    /// <b>No action takes a site, a tenant or a user id</b> — all three are resolved server-side, so there is no
    /// parameter to forge, and the acknowledge action resolves its row through the site-scoped read rather than a
    /// bare id (the ids are deterministic, therefore guessable).
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class HealthController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public HealthController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Health
        [HttpGet]
        [RequiresPermissionKey(HealthPermissions.View)]
        public async Task<HealthDashboard> Get(CancellationToken ct) =>
            await _mediator.Send(new GetHealthDashboardQuery(), ct);

        // GET api/Health/rollups?window=Hour&take=200
        [HttpGet("rollups")]
        [RequiresPermissionKey(HealthPermissions.View)]
        public async Task<IReadOnlyList<HealthRollupView>> Rollups(string? window, int take, CancellationToken ct) =>
            await _mediator.Send(new ListHealthRollupsQuery(string.IsNullOrWhiteSpace(window) ? "Hour" : window, take <= 0 ? 200 : take), ct);

        // POST api/Health/run  -> 200 with what ran, or 409 while a run for this site is already in flight
        [HttpPost("run")]
        [RequiresPermissionKey(HealthPermissions.Manage)]
        public async Task<IActionResult> Run(CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new RunHealthProbesNowCommand(), ct));
            }
            catch (InvalidOperationException ex) when (ex.Message == RunHealthProbesNowCommandHandler.AlreadyRunning)
            {
                return Conflict(ex.Message);
            }
        }

        // POST api/Health/alerts/{id}/acknowledge
        [HttpPost("alerts/{id:guid}/acknowledge")]
        [RequiresPermissionKey(HealthPermissions.Manage)]
        public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
        {
            try
            {
                await _mediator.Send(new AcknowledgeHealthAlertCommand(id), ct);
                return NoContent();
            }
            catch (InvalidOperationException)
            {
                // The handler resolves the row through the SITE-scoped read, so "another site's alert" and "no
                // such alert" arrive here identically - and must leave identically, or the id space becomes an
                // oracle for what exists on other sites.
                return NotFound();
            }
        }
    }
}
