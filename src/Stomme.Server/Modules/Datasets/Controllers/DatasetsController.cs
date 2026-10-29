#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Datasets;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Datasets.Controllers
{
    /// <summary>
    /// The Datasets API: <c>api/Datasets</c>. Each action is gated on its key by
    /// <c>[RequiresPermissionKey]</c> (an authorization POLICY, so it runs before the body); the request is decided again by
    /// the mediator's authorization behavior. The site is the request's. A preview needs the higher key and is refused
    /// for a sensitive dataset whoever asks; a list or a get never carries raw rows. <c>upload</c> (increment 13) registers a
    /// dataset from a managed file: another site's file is 404, a folder the caller may not view 403, a refused file 400.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class DatasetsController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public DatasetsController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Datasets
        [HttpGet]
        [RequiresPermissionKey(DatasetPermissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _mediator.Send(new ListDatasetsQuery(), ct));

        // GET api/Datasets/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(DatasetPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetDatasetQuery(id), ct) is { } dataset ? Ok(dataset) : NotFound();

        // GET api/Datasets/{id}/preview - the bounded raw rows, the higher key
        [HttpGet("{id:guid}/preview")]
        [RequiresPermissionKey(DatasetPermissions.Preview)]
        public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new PreviewDatasetRowsQuery(id), ct));
            }
            catch (InvalidOperationException)
            {
                return NotFound();
            }
        }

        // POST api/Datasets - register, or refresh the dataset the origin already has
        [HttpPost]
        [RequiresPermissionKey(DatasetPermissions.Manage)]
        public async Task<IActionResult> Register([FromBody] RegisterDatasetRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                var id = await _mediator.Send(new RegisterDatasetCommand(request.Name, request.OriginKind, request.OriginReference, request.SampleJson, request.Classification), ct);
                return Ok(await _mediator.Send(new GetDatasetQuery(id), ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ValidationException)
            {
                return Refused(ex);
            }
        }

        // POST api/Datasets/upload - a dataset from a file the file manager holds, opened as the caller
        [HttpPost("upload")]
        [RequiresPermissionKey(DatasetPermissions.Manage)]
        public async Task<IActionResult> Upload([FromBody] UploadDatasetRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                var id = await _mediator.Send(new RegisterUploadedDatasetCommand(request.FileId, request.Name, request.Classification), ct);
                return Ok(await _mediator.Send(new GetDatasetQuery(id), ct));
            }
            catch (System.IO.FileNotFoundException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException refused)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = refused.Message });
            }
            catch (Exception ex) when (ex is DatasetUploadRefusedException or InvalidOperationException or ArgumentException or ValidationException)
            {
                return Refused(ex);
            }
        }

        // POST api/Datasets/{id}/refresh
        [HttpPost("{id:guid}/refresh")]
        [RequiresPermissionKey(DatasetPermissions.Manage)]
        public async Task<IActionResult> Refresh(Guid id, [FromBody] RefreshDatasetRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                await _mediator.Send(new RefreshDatasetCommand(id, request.SampleJson), ct);
                return Ok(await _mediator.Send(new GetDatasetQuery(id), ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ValidationException)
            {
                return Refused(ex);
            }
        }

        // DELETE api/Datasets/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(DatasetPermissions.Manage)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            try
            {
                await _mediator.Send(new DeleteDatasetCommand(id), ct);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // A name held by another origin, an over-cap or malformed sample, a blank name: the caller's input, answered as such.
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
