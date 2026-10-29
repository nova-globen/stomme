#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Collaboration.Controllers
{
    /// <summary>
    /// <c>api/Collaboration</c> (the predecessor platform's <c>CollaborationApi</c>): an editor's advisory lock and its
    /// session under <c>collaboration:view</c>, the take-over, the close and the overview under <c>collaboration:manage</c>.
    /// Taking a lock someone else holds is a 200 that NAMES them - advisory, never an error. A refusal carries its code and
    /// a message written to be shown: 404 for a resource the user may not edit or a session they are not part of (like
    /// one that does not exist), 409 for a lock or session in a state that refuses the act or a change with no live lock.
    /// </summary>
    /// <remarks>
    /// While <c>Collaboration:Enabled</c> is off every route answers 404 - decided at ROUTE SELECTION
    /// (<see cref="CollaborationEnabledAttribute"/>), because a key check runs before any action body.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    [CollaborationEnabled]
    public sealed class CollaborationController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public CollaborationController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Collaboration/locks?resourceType=dashboard&resourceId=...&scope=Resource - who holds it, or 204
        [HttpGet("locks")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Lock([FromQuery] string resourceType, [FromQuery] string resourceId, [FromQuery] LockScope scope, CancellationToken ct) =>
            RefusalsAsync(async () => await _mediator.Send(new GetLockQuery(resourceType, resourceId, scope), ct) is { } held ? Ok(held) : NoContent());

        // POST api/Collaboration/locks/acquire
        [HttpPost("locks/acquire")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Acquire([FromBody] LockRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () => request is null ? BadRequest() : Ok(await _mediator.Send(new AcquireLockCommand(request.ResourceType, request.ResourceId, request.Scope, request.LeaseSeconds), ct)));

        // POST api/Collaboration/locks/renew - the editor's heartbeat; 404 when there is no lock to renew
        [HttpPost("locks/renew")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Renew([FromBody] LockRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () => request is null
                ? BadRequest()
                : await _mediator.Send(new RenewLockCommand(request.ResourceType, request.ResourceId, request.Scope, request.LeaseSeconds), ct) is { } renewed ? Ok(renewed) : NotFound());

        // POST api/Collaboration/locks/release
        [HttpPost("locks/release")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Release([FromBody] LockRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                if (request is null)
                {
                    return BadRequest();
                }

                await _mediator.Send(new ReleaseLockCommand(request.ResourceType, request.ResourceId, request.Scope), ct);
                return NoContent();
            });

        // POST api/Collaboration/locks/take-over - an administrator releases whoever holds it
        [HttpPost("locks/take-over")]
        [RequiresPermissionKey(CollaborationPermissions.Manage)]
        public Task<IActionResult> TakeOver([FromBody] LockRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () => request is null
                ? BadRequest()
                : await _mediator.Send(new ForceReleaseLockCommand(request.ResourceType, request.ResourceId, request.Scope), ct) ? NoContent() : NotFound());

        // POST api/Collaboration/sessions - open or join the resource's session
        [HttpPost("sessions")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Join([FromBody] JoinSessionRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () => request is null ? BadRequest() : Ok(await _mediator.Send(new JoinEditSessionCommand(request.ResourceType, request.ResourceId), ct)));

        // GET api/Collaboration/sessions?resourceType=...&resourceId=... - the open session, or 204
        [HttpGet("sessions")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Open([FromQuery] string resourceType, [FromQuery] string resourceId, CancellationToken ct) =>
            RefusalsAsync(async () => await _mediator.Send(new GetOpenEditSessionQuery(resourceType, resourceId), ct) is { } open ? Ok(open) : NoContent());

        // GET api/Collaboration/sessions/{id}
        [HttpGet("sessions/{id:guid}")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Session(Guid id, CancellationToken ct) =>
            RefusalsAsync(async () => await _mediator.Send(new GetEditSessionQuery(id), ct) is { } session ? Ok(session) : NotFound());

        // POST api/Collaboration/sessions/{id}/presence
        [HttpPost("sessions/{id:guid}/presence")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Presence(Guid id, [FromBody] PresenceRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                await _mediator.Send(new UpdatePresenceCommand(id, request?.ActiveRegion, request?.CursorPosition), ct);
                return NoContent();
            });

        // POST api/Collaboration/sessions/{id}/heartbeat - the editor is still here (increment 12)
        [HttpPost("sessions/{id:guid}/heartbeat")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Heartbeat(Guid id, CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                await _mediator.Send(new HeartbeatEditSessionCommand(id), ct);
                return NoContent();
            });

        // POST api/Collaboration/sessions/{id}/changes
        [HttpPost("sessions/{id:guid}/changes")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Change(Guid id, [FromBody] ChangeRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () => request is null ? BadRequest() : Ok(await _mediator.Send(new BroadcastChangeCommand(id, request.Descriptor), ct)));

        // POST api/Collaboration/sessions/{id}/leave
        [HttpPost("sessions/{id:guid}/leave")]
        [RequiresPermissionKey(CollaborationPermissions.View)]
        public Task<IActionResult> Leave(Guid id, CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                await _mediator.Send(new LeaveEditSessionCommand(id), ct);
                return NoContent();
            });

        // POST api/Collaboration/sessions/{id}/close - an administrator ends it
        [HttpPost("sessions/{id:guid}/close")]
        [RequiresPermissionKey(CollaborationPermissions.Manage)]
        public Task<IActionResult> Close(Guid id, CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                await _mediator.Send(new CloseEditSessionCommand(id), ct);
                return NoContent();
            });

        // GET api/Collaboration/overview - the site's live locks and open sessions
        [HttpGet("overview")]
        [RequiresPermissionKey(CollaborationPermissions.Manage)]
        public Task<IActionResult> Overview(CancellationToken ct) =>
            RefusalsAsync(async () => Ok(await _mediator.Send(new GetCollaborationOverviewQuery(), ct)));

        private async Task<IActionResult> RefusalsAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (ValidationException validation)
            {
                foreach (var failure in validation.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (Exception ex) when (CollaborationRefusals.Describe(ex) is { } refusal)
            {
                return StatusCode(refusal.Status, refusal.View);
            }
        }
    }

    /// <summary>The ONE mapping from a collaboration refusal to what a caller is told; only these messages are echoed.</summary>
    public static class CollaborationRefusals
    {
        public sealed record Refusal(int Status, CollaborationRefusalView View);

        public static Refusal? Describe(Exception exception) => exception switch
        {
            CollaborationRefusedException { Reason: CollaborationRefusal.NoLiveLock } refused => new(StatusCodes.Status409Conflict, new CollaborationRefusalView(refused.Reason.ToString(), refused.Message)),
            CollaborationRefusedException refused => new(StatusCodes.Status404NotFound, new CollaborationRefusalView(refused.Reason.ToString(), refused.Message)),
            CollaborationConflictException conflict => new(StatusCodes.Status409Conflict, new CollaborationRefusalView("Conflict", conflict.Message)),
            InvalidResourceRefException invalid => new(StatusCodes.Status400BadRequest, new CollaborationRefusalView("Invalid", invalid.Message)),
            _ => null,
        };
    }

    /// <summary>Route selection answers 404 for every collaboration route while <c>Collaboration:Enabled</c> is off.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class CollaborationEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(CollaborationOptions)) is CollaborationOptions { Enabled: true };
    }
}
