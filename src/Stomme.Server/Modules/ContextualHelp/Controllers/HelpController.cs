#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Help;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Modules.ContextualHelp.Startup;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.ContextualHelp.Controllers
{
    /// <summary>
    /// The contextual-help Web API (<c>api/Help</c>). Every action dispatches through
    /// <see cref="IMediator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TWO gates per action, the SAME key</b>: <c>[RequiresPermissionKey]</c> is decided by the authorization
    /// middleware before the action runs, and the request's <c>[Requires]</c> again by the mediator. There is NO
    /// class-level <c>[AllowAnonymous]</c> - an anonymous reader is admitted by the <c>help:view</c> ROW (Everyone),
    /// which is exactly as revocable as any other permission.
    /// </para>
    /// <para>
    /// <b>Off means 404, from the ROUTER</b> (<see cref="HelpEnabledAttribute"/>): a <c>[RequiresPermissionKey]</c> is a
    /// policy that runs before any action body, so an <c>if (!enabled) return NotFound()</c> inside an action would
    /// never be reached by a caller without the key.
    /// </para>
    /// <para>
    /// <b>Wire input never produces a 500.</b> A malformed <c>?anchor=</c> is "no topic" (204, D103 refutation 10);
    /// author input the domain refuses is a 400 with the reason; another site's id answers exactly like no id (404),
    /// so the deterministic - therefore guessable - id space is not an oracle for other sites.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    [HelpEnabled]
    public sealed class HelpController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public HelpController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Help/topic?anchor=page:admin&locale=sv-SE  -> 200 with the view, or 204 for "no topic"
        [HttpGet("topic")]
        [RequiresPermissionKey(HelpPermissions.View)]
        public async Task<IActionResult> Topic(string? anchor, string? locale, CancellationToken ct)
        {
            var view = await _mediator.Send(new ResolveHelpTopicQuery(anchor, locale), ct);
            return view is null ? NoContent() : Ok(view);
        }

        // GET api/Help/search?q=users
        [HttpGet("search")]
        [RequiresPermissionKey(HelpPermissions.View)]
        public async Task<IReadOnlyList<HelpTopicSummary>> Search(string? q, CancellationToken ct) =>
            await _mediator.Send(new ListPublishedHelpTopicsQuery(q), ct);

        // GET api/Help  -> every topic of this site, drafts included
        [HttpGet]
        [RequiresPermissionKey(HelpPermissions.Manage)]
        public async Task<IReadOnlyList<HelpTopicSummary>> List(CancellationToken ct) =>
            await _mediator.Send(new ListAllHelpTopicsQuery(), ct);

        // GET api/Help/conflicts
        [HttpGet("conflicts")]
        [RequiresPermissionKey(HelpPermissions.Manage)]
        public async Task<IReadOnlyList<HelpConflictView>> Conflicts(CancellationToken ct) =>
            await _mediator.Send(new ListHelpContributionConflictsQuery(), ct);

        // GET api/Help/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(HelpPermissions.Author)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var view = await _mediator.Send(new GetHelpTopicForEditQuery(id), ct);
            return view is null ? NotFound() : Ok(view);
        }

        // POST api/Help
        [HttpPost]
        [RequiresPermissionKey(HelpPermissions.Author)]
        public Task<IActionResult> Save([FromBody] HelpTopicSaveInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new SaveHelpTopicCommand(
                input.Id, input.Anchor ?? string.Empty, input.Locale ?? string.Empty, input.Title ?? string.Empty, input.Markdown ?? string.Empty), ct)));

        // POST api/Help/{id}/publish?published=true
        [HttpPost("{id:guid}/publish")]
        [RequiresPermissionKey(HelpPermissions.Author)]
        public Task<IActionResult> Publish(Guid id, bool published, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new PublishHelpTopicCommand(id, published), ct);
                return NoContent();
            });

        // POST api/Help/preview
        [HttpPost("preview")]
        [RequiresPermissionKey(HelpPermissions.Author)]
        public Task<IActionResult> Preview([FromBody] HelpPreviewInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(new HelpPreviewResult(await _mediator.Send(new PreviewHelpMarkdownQuery(input?.Markdown), ct))));

        // DELETE api/Help/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(HelpPermissions.Manage)]
        public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new DeleteHelpTopicCommand(id), ct);
                return NoContent();
            });

        // POST api/Help/{id}/reset
        [HttpPost("{id:guid}/reset")]
        [RequiresPermissionKey(HelpPermissions.Manage)]
        public Task<IActionResult> Reset(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new ResetHelpTopicToSeedCommand(id), ct);
                return NoContent();
            });

        /// <summary>
        /// The domain's refusals as HTTP: "no such topic on this site" is a 404 (indistinguishable from another site's
        /// id), anything else the domain refused is a 400 with its reason.
        /// </summary>
        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("No such help topic", StringComparison.Ordinal))
            {
                return NotFound();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "The help request was refused.");
            }
        }
    }

    /// <summary>
    /// Makes the controller's actions unmatchable while <c>Help:Enabled</c> is off, so the ROUTER answers 404 before
    /// authorization is ever consulted - read from the configuration THIS host runs on.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class HelpEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            HelpSettings.Enabled(HostConfiguration.Resolve(routeContext.HttpContext.RequestServices));
    }
}
