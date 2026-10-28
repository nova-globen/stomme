#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Export;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Application;
using Stomme.Modules.DataExport.Execution;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.DataExport.Controllers
{
    /// <summary>
    /// The data-export HTTP surface (D108 item 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>While <c>DataExport:Enabled</c> is off every route answers 404</b> - not 403: a disabled feature has no
    /// surface to be refused from. That has to happen at ROUTE SELECTION (<see cref="DataExportEnabledAttribute"/>):
    /// <c>[RequiresPermissionKey]</c> is an authorization policy and runs before any action body, so a check inside
    /// the action is never reached by a caller without the key - measured, every route answered 403 that way.
    /// </para>
    /// <para>
    /// <b>The artifact is downloaded HERE and nowhere else by its requester.</b> The framework's file endpoints
    /// authorise by folder View and never by requester; the Exports folder grants View to Administrators alone, so
    /// an ordinary requester is refused there and reaches their file only through <see cref="Download"/>, which
    /// checks requester-or-manage, re-asserts the file is still in the Exports folder, and audits.
    /// </para>
    /// <para>
    /// Only <see cref="ExportRequestRejectedException"/> becomes a 400 with its message - those messages are
    /// written to be shown. No other exception's text is echoed to the caller.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    [DataExportEnabled]
    public sealed class DataExportController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public DataExportController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/DataExport/sources
        [HttpGet("sources")]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> Sources(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListExportSourcesQuery(), ct));

        // GET api/DataExport/mine
        [HttpGet("mine")]
        [RequiresPermissionKey(ExportPermissions.View)]
        public async Task<IActionResult> Mine(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListMyExportsQuery(), ct));

        // GET api/DataExport/site
        [HttpGet("site")]
        [RequiresPermissionKey(ExportPermissions.Manage)]
        public async Task<IActionResult> Site(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListSiteExportsQuery(), ct));

        // GET api/DataExport/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(ExportPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var job = await _mediator.Send(new GetExportQuery(id), ct);
            return job is null ? NotFound() : Ok(job);
        }

        // POST api/DataExport
        [HttpPost]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> Create([FromBody] ExportRequest request, CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new RequestExportCommand(request), ct));
            }
            catch (ExportRequestRejectedException rejected)
            {
                return Problem(detail: rejected.Message, statusCode: StatusCodes.Status400BadRequest, title: "The export request was refused.");
            }
        }

        // POST api/DataExport/{id}/cancel
        [HttpPost("{id:guid}/cancel")]
        [RequiresPermissionKey(ExportPermissions.View)]
        public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        {
            // False covers "not yours", "not here" and "already finished" alike - one answer.
            return await _mediator.Send(new CancelExportCommand(id), ct) ? NoContent() : NotFound();
        }

        // GET api/DataExport/{id}/download
        [HttpGet("{id:guid}/download")]
        [RequiresPermissionKey(ExportPermissions.View)]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            // A resumed download arrives as a Range request for the rest of the file; only the request that starts
            // at the beginning is a download to audit, or one large file would write a row per chunk.
            var download = await _mediator.Send(new PrepareExportDownloadCommand(id, Audit: !IsContinuation(Request.Headers.Range)), ct);
            if (download is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return PhysicalFile(download.PhysicalPath, download.ContentType, download.DownloadName, enableRangeProcessing: true);
        }

        // ---- schedules (BL-95: recurring exports, each run requested as the schedule's owner) -----------------------

        // GET api/DataExport/schedules - the caller's own; every schedule of the site for a manager.
        [HttpGet("schedules")]
        [RequiresPermissionKey(ExportPermissions.View)]
        public async Task<IActionResult> Schedules(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListExportSchedulesQuery(), ct));

        // POST api/DataExport/schedules
        [HttpPost("schedules")]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> CreateSchedule([FromBody] ExportScheduleRequest schedule, CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new CreateExportScheduleCommand(schedule), ct));
            }
            catch (ExportRequestRejectedException rejected)
            {
                return Problem(detail: rejected.Message, statusCode: StatusCodes.Status400BadRequest, title: "The export schedule was refused.");
            }
        }

        // POST api/DataExport/schedules/{id}/pause
        [HttpPost("schedules/{id:guid}/pause")]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> PauseSchedule(Guid id, CancellationToken ct) =>
            await _mediator.Send(new ChangeExportScheduleCommand(id, ExportScheduleAction.Pause), ct) ? NoContent() : NotFound();

        // POST api/DataExport/schedules/{id}/resume
        [HttpPost("schedules/{id:guid}/resume")]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> ResumeSchedule(Guid id, CancellationToken ct) =>
            await _mediator.Send(new ChangeExportScheduleCommand(id, ExportScheduleAction.Resume), ct) ? NoContent() : NotFound();

        // DELETE api/DataExport/schedules/{id}
        [HttpDelete("schedules/{id:guid}")]
        [RequiresPermissionKey(ExportPermissions.Create)]
        public async Task<IActionResult> DeleteSchedule(Guid id, CancellationToken ct) =>
            await _mediator.Send(new ChangeExportScheduleCommand(id, ExportScheduleAction.Delete), ct) ? NoContent() : NotFound();

        // POST api/DataExport/schedules/{id}/takeover - a manager becomes the owner every later run is requested as.
        [HttpPost("schedules/{id:guid}/takeover")]
        [RequiresPermissionKey(ExportPermissions.Manage)]
        public async Task<IActionResult> TakeOverSchedule(Guid id, CancellationToken ct) =>
            await _mediator.Send(new TakeOverExportScheduleCommand(id), ct) ? NoContent() : NotFound();

        private static bool IsContinuation(Microsoft.Extensions.Primitives.StringValues range)
        {
            var value = range.ToString();
            return value.Length != 0 && !value.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Makes the controller's actions unmatchable while <c>DataExport:Enabled</c> is off, so the router answers 404
    /// before authorization is ever consulted.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class DataExportEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(DataExportOptions)) is DataExportOptions { Enabled: true };
    }
}
