#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Services;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Backup.Controllers
{
    /// <summary>
    /// The backup HTTP surface. HOST ONLY - an attribute on the class, not a permission key (refutation
    /// 8), and every action's service call re-asserts it. While <c>Backup:Enabled</c> is off every route answers 404, decided
    /// at route selection (<see cref="BackupEnabledAttribute"/>). Only <see cref="BackupRequestRejectedException"/> becomes a
    /// 400 with its message; no other exception's text reaches the caller.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    [BackupEnabled]
    [Authorize(Roles = RoleNames.Host)]
    public sealed class BackupController : ModuleControllerBase
    {
        private readonly ServerBackupService _backup;

        public BackupController(ServerBackupService backup, ILogManager logger, IHttpContextAccessor accessor) : base(logger, accessor)
        {
            _backup = backup;
        }

        // GET api/Backup
        [HttpGet]
        public Task<IActionResult> List() => RefusableAsync(async () => Ok(await _backup.ListAsync()));

        // POST api/Backup - take a snapshot (queued); 202 with its id
        [HttpPost]
        public Task<IActionResult> Create([FromBody] BackupPassphraseRequest request) => RefusableAsync(async () => Accepted(await _backup.CreateAsync(request)));

        // POST api/Backup/{id}/restore - pre-flight and stage (queued); applied when the host restarts
        [HttpPost("{id:guid}/restore")]
        public Task<IActionResult> Restore(Guid id, [FromBody] BackupPassphraseRequest request) => RefusableAsync(async () => Accepted(await _backup.RestoreAsync(id, request)));

        // GET api/Backup/{id}/restore - what the worker decided about a queued restore
        [HttpGet("{id:guid}/restore")]
        public Task<IActionResult> RestoreReport(Guid id) => RefusableAsync(async () => await _backup.RestoreReportAsync(id) is { } report ? Ok(report) : NotFound());

        // DELETE api/Backup/{id}
        [HttpDelete("{id:guid}")]
        public Task<IActionResult> Delete(Guid id) => RefusableAsync(async () =>
        {
            await _backup.DeleteAsync(id);
            return NoContent();
        });

        // GET api/Backup/{id}/download - the archive itself, still encrypted
        [HttpGet("{id:guid}/download")]
        public Task<IActionResult> Download(Guid id) => RefusableAsync(async () =>
            await _backup.OpenArchiveAsync(id) is { } archive
                ? File(archive, "application/octet-stream", $"{id:N}{Stomme.Modules.Backup.Domain.StorageLocation.ArchiveExtension}")
                : NotFound());

        // POST api/Backup/import - multipart: the archive file and the passphrase (never in the URL)
        [HttpPost("import")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public Task<IActionResult> Import([FromForm] IFormFile archive, [FromForm] string passphrase) => RefusableAsync(async () =>
        {
            if (archive is null)
            {
                throw new BackupRequestRejectedException("Choose an archive to import.");
            }

            await using var stream = archive.OpenReadStream();
            return Ok(await _backup.ImportAsync(stream, passphrase));
        });

        // GET api/Backup/schedules
        [HttpGet("schedules")]
        public Task<IActionResult> Schedules() => RefusableAsync(async () => Ok(await _backup.SchedulesAsync()));

        // PUT api/Backup/schedules
        [HttpPut("schedules")]
        public Task<IActionResult> SaveSchedule([FromBody] BackupScheduleView view) => RefusableAsync(async () => Ok(await _backup.SaveScheduleAsync(view)));

        // EVERY action goes through here, so the service's own Host check answers 403 - never a 500 - should the class
        // attribute ever be lost (measured: without the wrapper the service refusal surfaced as a 500 on the GETs).
        private async Task<IActionResult> RefusableAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (BackupRequestRejectedException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }
    }

    /// <summary>Makes the controller unmatchable while <c>Backup:Enabled</c> is off, so the router answers 404.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class BackupEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(BackupOptions)) is BackupOptions { Enabled: true };
    }
}
