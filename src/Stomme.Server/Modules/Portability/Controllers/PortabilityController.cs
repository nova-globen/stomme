#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Portability.Execution;
using Stomme.Modules.Portability.Services;
using Stomme.Portability;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Portability.Controllers
{
    /// <summary>
    /// The portability HTTP surface (BL-82, D107 items 3, 9, 12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>While <c>Portability:Enabled</c> is off every route answers 404</b>, decided at ROUTE SELECTION
    /// (<see cref="PortabilityEnabledAttribute"/>) because a permission-key policy runs before any action body.
    /// </para>
    /// <para>
    /// <b>An import is read here, capped, and never stored as a file</b> (refutation 12): the body is read up to
    /// <c>Portability:MaxPackageBytes</c> + 1 and refused past it, before anything deserialises it; nothing of it becomes
    /// a File row.
    /// </para>
    /// <para>
    /// Only <see cref="PortabilityRequestRejectedException"/> becomes a 400 with its message - those are written to be
    /// shown. No other exception's text reaches the caller.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    [PortabilityEnabled]
    public sealed class PortabilityController : ModuleControllerBase
    {
        private readonly ServerPortabilityService _portability;
        private readonly PortabilityOptions _options;

        public PortabilityController(ServerPortabilityService portability, PortabilityOptions options, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _portability = portability;
            _options = options;
        }

        // GET api/Portability - the packages this caller may see (every one for a view holder; else their own and about them)
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> List() => Ok(await _portability.ListAsync());

        // POST api/Portability/config
        [HttpPost("config")]
        [RequiresPermissionKey(PortabilityPermissions.Export)]
        public Task<IActionResult> Config([FromBody] PortabilityConfigRequest request) => RefusableAsync(() => _portability.RequestConfigAsync(request));

        // POST api/Portability/subject
        [HttpPost("subject")]
        [RequiresPermissionKey(PortabilityPermissions.SubjectData)]
        public Task<IActionResult> Subject([FromBody] PortabilitySubjectRequest request) => RefusableAsync(() => _portability.RequestSubjectAsync(request));

        // POST api/Portability/mine
        [HttpPost("mine")]
        [RequiresPermissionKey(PortabilityPermissions.ExportOwn)]
        public Task<IActionResult> Mine() => RefusableAsync(() => _portability.RequestMineAsync());

        // GET api/Portability/{id}/download
        [HttpGet("{id:guid}/download")]
        [Authorize]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var download = await _portability.DownloadAsync(id, ct);
            if (download is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(download.Value.Bytes, "application/json", download.Value.FileName);
        }

        // POST api/Portability/import?strategy=Skip - the package is the raw request body
        [HttpPost("import")]
        [RequiresPermissionKey(PortabilityPermissions.Import)]
        public async Task<IActionResult> Import([FromQuery] ConflictStrategy strategy, CancellationToken ct)
        {
            var max = Math.Max(1, _options.MaxPackageBytes);
            if (Request.ContentLength is { } length && length > max)
            {
                return Problem(detail: $"The package is larger than {max:N0} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge, title: "The import was refused.");
            }

            // Read at most max + 1 characters' worth: a body that lied about its length is still cut off here.
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var buffer = new char[checked((int)Math.Min(max + 1, int.MaxValue - 1))];
            var read = 0;
            int chunk;
            while (read < buffer.Length && (chunk = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)) > 0)
            {
                read += chunk;
            }

            if (read > max)
            {
                return Problem(detail: $"The package is larger than {max:N0} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge, title: "The import was refused.");
            }

            return Ok(await _portability.ImportAsync(new string(buffer, 0, read), strategy, ct));
        }

        private async Task<IActionResult> RefusableAsync(Func<Task<PortabilityPackageView>> action)
        {
            try
            {
                return Ok(await action());
            }
            catch (PortabilityRequestRejectedException rejected)
            {
                return Problem(detail: rejected.Message, statusCode: StatusCodes.Status400BadRequest, title: "The request was refused.");
            }
        }
    }

    /// <summary>Makes the controller unmatchable while <c>Portability:Enabled</c> is off, so the router answers 404.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class PortabilityEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(PortabilityOptions)) is PortabilityOptions { Enabled: true };
    }
}
