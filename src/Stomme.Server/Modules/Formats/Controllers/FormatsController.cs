#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Formats;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.Formats.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Formats.Controllers
{
    /// <summary>
    /// The Formats API (BL-94 increment 2, D110): <c>api/Formats</c>. Each action is gated on its key by
    /// <c>[RequiresPermissionKey]</c> (an authorization POLICY, so it runs before the body); the request is decided again
    /// by the mediator's authorization behavior. The site is the request's - nothing here names one.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class FormatsController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public FormatsController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Formats?kind=&scope=&culture=
        [HttpGet]
        [RequiresPermissionKey(FormatPermissions.View)]
        public async Task<IActionResult> List(FormatKind? kind, FormatScope? scope, string? culture, CancellationToken ct) =>
            Ok(await _mediator.Send(new ListFormatsQuery(kind, culture, scope), ct));

        // GET api/Formats/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(FormatPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetFormatQuery(id), ct) is { } format ? Ok(format) : NotFound();

        // POST api/Formats - create (no id) or update (an id the site owns)
        [HttpPost]
        [RequiresPermissionKey(FormatPermissions.Manage)]
        public async Task<IActionResult> Save([FromBody] FormatDefinitionRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                var id = await _mediator.Send(new SaveFormatDefinitionCommand(request.Id, request.Name, request.Kind, request.Pattern, request.Culture, request.TimeZone), ct);
                return Ok(await _mediator.Send(new GetFormatQuery(id), ct));
            }
            catch (ValidationException ex)
            {
                foreach (var failure in ex.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // A duplicate name, a read-only preset, an unrenderable pattern, an unknown culture or zone: the caller's
                // input, answered as such. The message is the domain's own, which names no secret.
                return BadRequest(new { error = ex.Message });
            }
        }

        // DELETE api/Formats/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(FormatPermissions.Manage)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            try
            {
                await _mediator.Send(new DeleteFormatDefinitionCommand(id), ct);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
