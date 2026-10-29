#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Connectivity.Controllers
{
    /// <summary>
    /// The Connectivity API: <c>api/Connections</c>. Each action is gated on its key by
    /// <c>[RequiresPermissionKey]</c> (an authorization POLICY, so it runs before the body); the request is decided again
    /// by the mediator's authorization behavior. The site is the request's - nothing here names one. A credential
    /// travels IN only; no response carries one, and a connector's verdict comes back as a category.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class ConnectionsController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public ConnectionsController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Connections
        [HttpGet]
        [RequiresPermissionKey(ConnectionPermissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _mediator.Send(new ListConnectionsQuery(), ct));

        // GET api/Connections/connectors - the installed connector types for the editor
        [HttpGet("connectors")]
        [RequiresPermissionKey(ConnectionPermissions.View)]
        public async Task<IActionResult> Connectors(CancellationToken ct) => Ok(await _mediator.Send(new ListConnectorsQuery(), ct));

        // GET api/Connections/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(ConnectionPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetConnectionQuery(id), ct) is { } connection ? Ok(connection) : NotFound();

        // POST api/Connections - create
        [HttpPost]
        [RequiresPermissionKey(ConnectionPermissions.Manage)]
        public async Task<IActionResult> Create([FromBody] ConnectionRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                var id = await _mediator.Send(new CreateConnectionCommand(request.Name, request.ConnectorType, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds), ct);
                return Ok(await _mediator.Send(new GetConnectionQuery(id), ct));
            }
            catch (Exception ex) when (IsTheCallers(ex))
            {
                return Refused(ex);
            }
        }

        // PUT api/Connections/{id} - reconfigure (the connector type is fixed at creation)
        [HttpPut("{id:guid}")]
        [RequiresPermissionKey(ConnectionPermissions.Manage)]
        public async Task<IActionResult> Update(Guid id, [FromBody] ConnectionRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                await _mediator.Send(new UpdateConnectionCommand(id, request.Name, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds), ct);
                return Ok(await _mediator.Send(new GetConnectionQuery(id), ct));
            }
            catch (Exception ex) when (IsTheCallers(ex))
            {
                return Refused(ex);
            }
        }

        // POST api/Connections/{id}/test
        [HttpPost("{id:guid}/test")]
        [RequiresPermissionKey(ConnectionPermissions.Manage)]
        public async Task<IActionResult> Test(Guid id, CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new TestConnectionCommand(id), ct));
            }
            catch (InvalidOperationException)
            {
                return NotFound();
            }
        }

        // DELETE api/Connections/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(ConnectionPermissions.Manage)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            try
            {
                await _mediator.Send(new DeleteConnectionCommand(id), ct);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // GET api/Connections/egress - the private hosts this site may reach
        [HttpGet("egress")]
        [RequiresPermissionKey(ConnectionPermissions.View)]
        public async Task<IActionResult> Egress(CancellationToken ct) => Ok(await _mediator.Send(new GetConnectionEgressQuery(), ct));

        // PUT api/Connections/egress - the Host role alone (403 otherwise); an entry the guard could never match is 400
        [HttpPut("egress")]
        [RequiresPermissionKey(ConnectionPermissions.Manage)]
        public async Task<IActionResult> SaveEgress([FromBody] ConnectionEgressRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                return Ok(await _mediator.Send(new SaveConnectionEgressCommand(request.AllowedHosts), ct));
            }
            catch (Exception ex) when (IsTheCallers(ex))
            {
                return Refused(ex);
            }
        }

        private static bool IsTheCallers(Exception ex) =>
            ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or ValidationException;

        // An unknown type, a duplicate name, an endpoint carrying a credential, an undeclared credential field, a schema
        // failure: the caller's input, answered as such. A HostOnly refusal is 403. The messages are the domain's own,
        // which name no secret.
        private IActionResult Refused(Exception ex)
        {
            switch (ex)
            {
                case ValidationException validation:
                    foreach (var failure in validation.Errors)
                    {
                        ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                    }

                    return ValidationProblem(ModelState);
                case UnauthorizedAccessException refused:
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = refused.Message });
                default:
                    return BadRequest(new { error = ex.Message });
            }
        }
    }
}
