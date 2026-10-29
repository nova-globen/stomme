#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Landing;
using Stomme.Mediator;
using Stomme.Modules.Landing.Application;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Landing.Controllers
{
    /// <summary>
    /// <c>api/Landing</c>: the site's landing page. Reading is <c>landing:view</c> - every visitor's - and answers 204
    /// for a site with no page, or a draft the caller may not see; everything else is <c>landing:manage</c>. A content the
    /// domain refuses is 400 with its reason, publishing a page never made is 409; a dashboard not on the site is 404, a draft
    /// one 409.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class LandingController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public LandingController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Landing
        [HttpGet]
        [RequiresPermissionKey(LandingPermissions.View)]
        public async Task<IActionResult> Get(CancellationToken ct) =>
            await _mediator.Send(new GetLandingPageQuery(), ct) is { } page ? Ok(page) : NoContent();

        // GET api/Landing/template
        [HttpGet("template")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public async Task<IActionResult> Template(CancellationToken ct) => Ok(await _mediator.Send(new GetLandingTemplateQuery(), ct));

        // PUT api/Landing/predefined
        [HttpPut("predefined")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> ConfigurePredefined([FromBody] ConfigurePredefinedLandingRequest request, CancellationToken ct) =>
            AnswerAsync(async () => Ok(await _mediator.Send(new ConfigurePredefinedLandingCommand(
                new PredefinedLandingContent(request?.Headline ?? string.Empty, request?.Introduction ?? string.Empty, request?.Sections ?? new List<LandingSectionView>())), ct)));

        // PUT api/Landing/dashboard - one of the site's published dashboards, shown in place
        [HttpPut("dashboard")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> ConfigureDashboard([FromBody] ConfigureDashboardLandingRequest request, CancellationToken ct) =>
            AnswerAsync(async () => Ok(await _mediator.Send(new ConfigureDashboardLandingCommand(request?.DashboardId ?? Guid.Empty), ct)));

        // POST api/Landing/generate - a page from a brief, PROPOSED; nothing stored
        [HttpPost("generate")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> Generate([FromBody] GenerateLandingRequest request, CancellationToken ct) =>
            AiAsync(async () => Ok(await _mediator.Send(new GenerateLandingDraftCommand(request?.Brief ?? string.Empty), ct)));

        // POST api/Landing/sections/propose - one section revised, PROPOSED; nothing stored
        [HttpPost("sections/propose")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> ProposeSection([FromBody] ProposeLandingSectionRequest request, CancellationToken ct) =>
            AiAsync(async () => Ok(await _mediator.Send(new ProposeLandingSectionCommand(request?.Section ?? string.Empty, request?.Instruction ?? string.Empty), ct)));

        // PUT api/Landing/generated - make the page from generated HTML the administrator accepted; sanitised again here
        [HttpPut("generated")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> ConfigureGenerated([FromBody] ConfigureGeneratedLandingRequest request, CancellationToken ct) =>
            AnswerAsync(async () => Ok(await _mediator.Send(new ConfigureGeneratedLandingCommand(request?.Html ?? string.Empty), ct)));

        // POST api/Landing/publish
        [HttpPost("publish")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> Publish(CancellationToken ct) => AnswerAsync(async () => Ok(await _mediator.Send(new PublishLandingCommand(), ct)));

        // POST api/Landing/unpublish
        [HttpPost("unpublish")]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public Task<IActionResult> Unpublish(CancellationToken ct) => AnswerAsync(async () => Ok(await _mediator.Send(new UnpublishLandingCommand(), ct)));

        // DELETE api/Landing - forget the page; the chooser is offered again
        [HttpDelete]
        [RequiresPermissionKey(LandingPermissions.Manage)]
        public async Task<IActionResult> Reset(CancellationToken ct)
        {
            await _mediator.Send(new ResetLandingCommand(), ct);
            return NoContent();
        }

        // The assistant's refusals (off, not configured, quota spent) through its ONE mapping, then the module's own.
        private static async Task<IActionResult> AiAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await AnswerAsync(action);
            }
            catch (Exception ex) when (Stomme.Modules.AiAssistant.Application.AssistantRefusals.Describe(ex) is { } refusal)
            {
                return new ObjectResult(refusal.View) { StatusCode = refusal.Status };
            }
        }

        private static async Task<IActionResult> AnswerAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return new NotFoundObjectResult(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return new ConflictObjectResult(new { error = ex.Message });
            }
        }
    }
}
