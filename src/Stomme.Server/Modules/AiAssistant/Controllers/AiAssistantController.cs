#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.AiAssistant.Controllers
{
    /// <summary>
    /// <c>api/AiAssistant</c> (the predecessor platform's <c>AssistantApi</c>): the current user's conversations on this
    /// site under <c>assistant:use</c>. A refusal answers with its code and a message safe to show - the guardrail's 400 (an
    /// injection), 403 (a reference the user may not use) or 429 (the rate window); the model's 409 (off for the site, not
    /// configured, key unreadable, unknown provider), 429 (the site's AI quota spent) or 503 (the meter unreachable); 409 for a
    /// conversation or proposal in the wrong state. Another user's conversation is 404, like one that does not exist.
    /// </summary>
    /// <remarks>
    /// While <c>AiAssistant:Enabled</c> is off every route answers 404 - decided at ROUTE SELECTION
    /// (<see cref="AiAssistantEnabledAttribute"/>), because <c>[RequiresPermissionKey]</c> runs before any action body.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    [AiAssistantEnabled]
    public sealed class AiAssistantController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public AiAssistantController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/AiAssistant/conversations
        [HttpGet("conversations")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _mediator.Send(new ListConversationsQuery(), ct));

        // POST api/AiAssistant/conversations
        [HttpPost("conversations")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public Task<IActionResult> Start(CancellationToken ct) =>
            RefusalsAsync(async () =>
            {
                var conversation = await _mediator.Send(new StartConversationCommand(), ct);
                return Created($"api/AiAssistant/conversations/{conversation.Id}", conversation);
            });

        // GET api/AiAssistant/conversations/{id}
        [HttpGet("conversations/{id:guid}")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetConversationQuery(id), ct) is { } conversation ? Ok(conversation) : NotFound();

        // POST api/AiAssistant/conversations/{id}/messages
        [HttpPost("conversations/{id:guid}/messages")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public Task<IActionResult> Send(Guid id, [FromBody] SendAssistantMessageRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () =>
                request is null
                    ? BadRequest()
                    : await _mediator.Send(new SendAssistantMessageCommand(id, request.Message), ct) is { } conversation ? Ok(conversation) : NotFound());

        // POST api/AiAssistant/conversations/{id}/artifacts/{artifactId}/apply
        [HttpPost("conversations/{id:guid}/artifacts/{artifactId:guid}/apply")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public Task<IActionResult> Apply(Guid id, Guid artifactId, CancellationToken ct) =>
            RefusalsAsync(async () => await _mediator.Send(new ApplyAssistantArtifactCommand(id, artifactId), ct) is { } result ? Ok(result) : NotFound());

        // POST api/AiAssistant/conversations/{id}/artifacts/{artifactId}/discard
        [HttpPost("conversations/{id:guid}/artifacts/{artifactId:guid}/discard")]
        [RequiresPermissionKey(AssistantPermissions.Use)]
        public Task<IActionResult> Discard(Guid id, Guid artifactId, CancellationToken ct) =>
            RefusalsAsync(async () => await _mediator.Send(new DiscardAssistantArtifactCommand(id, artifactId), ct) ? NoContent() : NotFound());

        // GET api/AiAssistant/settings - the site's configuration; whether a key is stored, never the key
        [HttpGet("settings")]
        [RequiresPermissionKey(AssistantPermissions.Configure)]
        public Task<IActionResult> Settings(CancellationToken ct) =>
            RefusalsAsync(async () => Ok(await _mediator.Send(new GetAssistantSettingsQuery(), ct)));

        // PUT api/AiAssistant/settings
        [HttpPut("settings")]
        [RequiresPermissionKey(AssistantPermissions.Configure)]
        public Task<IActionResult> SaveSettings([FromBody] SaveAssistantSettingsRequest? request, CancellationToken ct) =>
            RefusalsAsync(async () =>
                request is null
                    ? BadRequest()
                    : Ok(await _mediator.Send(new SaveAssistantSettingsCommand(request.Enabled, request.Provider, request.Endpoint, request.Model, request.Temperature, request.ApiKey, request.ClearApiKey), ct)));

        /// <summary>
        /// The refusals a caller is told about, each with its code. Only these messages are echoed - they are written to be
        /// shown and never carry a model's words; any other failure is the framework's own (a denied module key is 403).
        /// </summary>
        private async Task<IActionResult> RefusalsAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (ValidationException validation)
            {
                foreach (var failure in validation.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (Exception ex) when (AssistantRefusals.Describe(ex) is { } refusal)
            {
                return StatusCode(refusal.Status, refusal.View);
            }
        }
    }
    /// <summary>Route selection answers 404 for every assistant route while <c>AiAssistant:Enabled</c> is off.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class AiAssistantEnabledAttribute : Microsoft.AspNetCore.Mvc.ActionConstraints.ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(Microsoft.AspNetCore.Routing.RouteContext routeContext, Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(AssistantOptions)) is AssistantOptions { Enabled: true };
    }
}
