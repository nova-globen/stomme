using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using [Owner].Module.[Module].Application;
using [Owner].Module.[Module].Models;

namespace [Owner].Module.[Module].Controllers
{
    /// <summary>
    /// The module's API, <c>api/[Module]</c>. Each action carries <c>[RequiresPermissionKey]</c> - the host's authorization
    /// policy, decided before the action runs - and sends a request that carries the same key in <c>[Requires]</c>.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class [Module]Controller : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public [Module]Controller(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/[Module]
        [HttpGet]
        [RequiresPermissionKey([Module]Permissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _mediator.Send(new List[Module]EntriesQuery(), ct));

        // POST api/[Module]
        [HttpPost]
        [RequiresPermissionKey([Module]Permissions.Edit)]
        public async Task<IActionResult> Add([FromBody] Add[Module]EntryRequest request, CancellationToken ct)
        {
            try
            {
                return Ok(await _mediator.Send(new Add[Module]EntryCommand(request.Text), ct));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
