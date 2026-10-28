using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Modules.Guestbook.Services;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using GuestbookEntry = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Controllers
{
    /// <summary>
    /// The Web API boundary for the Guestbook module: the client <c>IGuestbookService</c> calls these over
    /// HTTP, and every action delegates to the server <see cref="IGuestbookService"/>, which dispatches
    /// through the <see cref="Stomme.Mediator.IMediator"/>. This is the full Stomme module round trip —
    /// component → client IService (HTTP) → controller → server IService → mediator → handler.
    /// <para>
    /// A guestbook is public by nature, so reads and signing are <see cref="AllowAnonymousAttribute"/> — this
    /// keeps the sample easy to drive without an auth cookie and matches the module's <c>View = Everyone</c>
    /// permission on the seeded page. A production module gating writes would keep <c>ModuleControllerBase</c>'s
    /// per-entity authorization instead.
    /// </para>
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    [AllowAnonymous]
    public class GuestbookController : ModuleControllerBase
    {
        private readonly IGuestbookService _guestbookService;

        public GuestbookController(IGuestbookService guestbookService, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _guestbookService = guestbookService;
        }

        // GET api/guestbook?moduleid=x  — the CQRS query
        [HttpGet]
        public async Task<IEnumerable<GuestbookEntry>> Get(int moduleId) =>
            await _guestbookService.ListEntriesAsync(moduleId);

        // GET api/guestbook/stream?moduleid=x — the CQRS stream request, streamed as JSON
        [HttpGet("stream")]
        public IAsyncEnumerable<GuestbookEntry> Stream(int moduleId, CancellationToken ct) =>
            _guestbookService.StreamEntries(moduleId, ct);

        // POST api/guestbook — the CQRS command. A ValidationException raised by the pipeline's
        // ValidationBehavior becomes a 400 with the field errors: the visible "rejected a bad command" result.
        [HttpPost]
        public async Task<ActionResult<GuestbookEntry>> Post([FromBody] GuestbookEntry entry)
        {
            try
            {
                return await _guestbookService.SignAsync(entry);
            }
            catch (ValidationException ex)
            {
                foreach (var failure in ex.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }
                return ValidationProblem(ModelState);
            }
        }
    }
}
