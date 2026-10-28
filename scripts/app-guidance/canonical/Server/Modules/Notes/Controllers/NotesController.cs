#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Application.Modules.Notes.Controllers
{
    /// <summary>
    /// api/Notes. Each action is gated by a permission key before it runs, and the request it sends is gated again by its
    /// [Requires]. 400: invalid input; 403: not allowed; 404: unknown or another site's note; 409: a clash or a stale write.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class NotesController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public NotesController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Notes
        [HttpGet]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListNotesQuery(), ct));

        // GET api/Notes/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNoteQuery(id), ct) is { } note ? Ok(note) : NotFound();

        // POST api/Notes
        [HttpPost]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Create([FromBody] SaveNoteRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new CreateNoteCommand(request.Title, request.Body), ct)));

        // PUT api/Notes/{id}
        [HttpPut("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Edit(Guid id, [FromBody] SaveNoteRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new EditNoteCommand(id, request.Title, request.Body), ct)));

        // DELETE api/Notes/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
            Answer(async () =>
            {
                await _mediator.Send(new DeleteNoteCommand(id), ct);
                return NoContent();
            });

        // AccessDeniedException is not caught: the host turns it into 403. These are the other answers.
        private async Task<IActionResult> Answer(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (ValidationException ex)
            {
                foreach (var failure in ex.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (NoteConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (ConcurrencyConflictException)
            {
                return Conflict(new { error = "Someone else changed this note in the meantime. Reload it and try again." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
