#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Notifications;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Notifications.Controllers
{
    /// <summary>
    /// The Notifications module's Web API (<c>api/Notifications</c>): the self-service surface (the caller's own
    /// notifications and preferences), the send log, sending, templates and channels. Every action dispatches through
    /// <see cref="IMediator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TWO gates per action, deliberately the SAME key.</b> <c>[RequiresPermissionKey]</c> is decided by the
    /// authorization middleware before the action runs; the request's <c>[Requires]</c> is decided again by the mediator's
    /// <c>AuthorizationBehavior</c> - both over the site's <c>(notifications, -1, operation)</c> rows, so they cannot disagree.
    /// A denial is a 403: from the middleware, or as <c>ProblemDetails</c> from <c>AccessDeniedExceptionFilter</c>.
    /// </para>
    /// <para>
    /// <b>Never a class-level <c>[AllowAnonymous]</c></b> - it would short-circuit every key policy on this controller while the
    /// mediator half still denied, which reads as gated and is not (<c>PermissionKeyDeclarationGateTests</c> fails the build on it).
    /// </para>
    /// <para>
    /// A domain rule the command refuses (<see cref="InvalidOperationException"/> / <see cref="ArgumentException"/> - an unknown
    /// id, a duplicate key, an undeclared placeholder, a denied webhook host) and a <see cref="ValidationException"/> are a 400
    /// with the reason.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class NotificationsController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public NotificationsController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // ---- self-service (ViewOwn) ------------------------------------------------------------------------------------

        // GET api/Notifications/mine
        [HttpGet("mine")]
        [RequiresPermissionKey(NotificationsPermissions.ViewOwn)]
        public async Task<IReadOnlyList<NotificationRequestSummary>> Mine(CancellationToken ct) =>
            await _mediator.Send(new ListMyNotificationsQuery(), ct);

        // GET api/Notifications/preferences
        [HttpGet("preferences")]
        [RequiresPermissionKey(NotificationsPermissions.ViewOwn)]
        public async Task<NotificationPreferencesView> GetPreferences(CancellationToken ct) =>
            await _mediator.Send(new GetMyNotificationPreferencesQuery(), ct);

        // PUT api/Notifications/preferences
        [HttpPut("preferences")]
        [RequiresPermissionKey(NotificationsPermissions.ViewOwn)]
        public Task<IActionResult> SavePreferences([FromBody] NotificationPreferencesView preferences, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new SaveMyNotificationPreferencesCommand(
                    preferences.AcceptedChannels ?? Array.Empty<NotificationChannelKind>(),
                    preferences.SeverityFilter,
                    preferences.MutedTopics ?? Array.Empty<string>(),
                    preferences.QuietHours,
                    preferences.Digest,
                    preferences.PreferredLocale), ct);
                return NoContent();
            });

        // ---- the send log (ViewLog) ------------------------------------------------------------------------------------

        // GET api/Notifications/log
        [HttpGet("log")]
        [RequiresPermissionKey(NotificationsPermissions.ViewLog)]
        public async Task<IReadOnlyList<NotificationRequestSummary>> Log(CancellationToken ct) =>
            await _mediator.Send(new ListNotificationRequestsQuery(), ct);

        // GET api/Notifications/log/{id}
        [HttpGet("log/{id:guid}")]
        [RequiresPermissionKey(NotificationsPermissions.ViewLog)]
        public async Task<ActionResult<NotificationRequestDetail>> LogEntry(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNotificationRequestQuery(id), ct) is { } detail ? detail : NotFound();

        // ---- sending (Send) --------------------------------------------------------------------------------------------

        // POST api/Notifications/log/{id}/retry
        [HttpPost("log/{id:guid}/retry")]
        [RequiresPermissionKey(NotificationsPermissions.Send)]
        public Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new RetryNotificationRequestCommand(id), ct);
                return NoContent();
            });

        // POST api/Notifications/send-test
        [HttpPost("send-test")]
        [RequiresPermissionKey(NotificationsPermissions.Send)]
        public Task<IActionResult> SendTest([FromBody] NotificationSendTestInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new SendTestNotificationCommand(
                input.Channels ?? Array.Empty<NotificationChannelKind>(), input.Subject, input.Body), ct)));

        // ---- templates (ManageTemplates) -------------------------------------------------------------------------------

        // GET api/Notifications/templates
        [HttpGet("templates")]
        [RequiresPermissionKey(NotificationsPermissions.ManageTemplates)]
        public async Task<IReadOnlyList<NotificationTemplateSummary>> Templates(CancellationToken ct) =>
            await _mediator.Send(new ListNotificationTemplatesQuery(), ct);

        // GET api/Notifications/templates/{id}
        [HttpGet("templates/{id:guid}")]
        [RequiresPermissionKey(NotificationsPermissions.ManageTemplates)]
        public async Task<ActionResult<NotificationTemplateDetail>> Template(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNotificationTemplateQuery(id), ct) is { } detail ? detail : NotFound();

        // POST api/Notifications/templates
        [HttpPost("templates")]
        [RequiresPermissionKey(NotificationsPermissions.ManageTemplates)]
        public Task<IActionResult> SaveTemplate([FromBody] NotificationTemplateInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new SaveNotificationTemplateCommand(
                input.Id,
                input.Key ?? string.Empty,
                input.SupportedChannels ?? Array.Empty<NotificationChannelKind>(),
                input.Contents ?? Array.Empty<NotificationTemplateContentInput>(),
                input.Placeholders ?? Array.Empty<string>()), ct)));

        // DELETE api/Notifications/templates/{id}
        [HttpDelete("templates/{id:guid}")]
        [RequiresPermissionKey(NotificationsPermissions.ManageTemplates)]
        public Task<IActionResult> DeleteTemplate(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new DeleteNotificationTemplateCommand(id), ct);
                return NoContent();
            });

        // POST api/Notifications/templates/preview - a PREVIEW is template authoring, never viewing.
        [HttpPost("templates/preview")]
        [RequiresPermissionKey(NotificationsPermissions.ManageTemplates)]
        public Task<IActionResult> Preview([FromBody] NotificationTemplatePreviewInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new PreviewNotificationTemplateQuery(input.Id, input.LocaleCode ?? string.Empty, input.Parameters), ct)));

        // ---- channels (ManageChannels) ---------------------------------------------------------------------------------

        // GET api/Notifications/channels
        [HttpGet("channels")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public async Task<IReadOnlyList<NotificationChannelSummary>> Channels(CancellationToken ct) =>
            await _mediator.Send(new ListNotificationChannelsQuery(), ct);

        // GET api/Notifications/channels/{id}
        [HttpGet("channels/{id:guid}")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public async Task<ActionResult<NotificationChannelDetail>> Channel(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNotificationChannelQuery(id), ct) is { } detail ? detail : NotFound();

        // POST api/Notifications/channels
        [HttpPost("channels")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public Task<IActionResult> SaveChannel([FromBody] NotificationChannelInput input, CancellationToken ct) =>
            RunAsync(async () => Ok(await _mediator.Send(new ConfigureChannelCommand(
                input.Id, input.Kind, input.Name ?? string.Empty, input.Endpoint, input.Secret, input.Options, input.RateLimit), ct)));

        // POST api/Notifications/channels/{id}/enable
        [HttpPost("channels/{id:guid}/enable")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public Task<IActionResult> EnableChannel(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new EnableChannelCommand(id), ct);
                return NoContent();
            });

        // POST api/Notifications/channels/{id}/disable
        [HttpPost("channels/{id:guid}/disable")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public Task<IActionResult> DisableChannel(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new DisableChannelCommand(id), ct);
                return NoContent();
            });

        // PUT api/Notifications/channels/{id}/credential - write-only
        [HttpPut("channels/{id:guid}/credential")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public Task<IActionResult> SetChannelCredential(Guid id, [FromBody] NotificationChannelCredentialInput input, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new SetChannelCredentialCommand(id, input?.Secret), ct);
                return NoContent();
            });

        // DELETE api/Notifications/channels/{id}
        [HttpDelete("channels/{id:guid}")]
        [RequiresPermissionKey(NotificationsPermissions.ManageChannels)]
        public Task<IActionResult> DeleteChannel(Guid id, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await _mediator.Send(new DeleteChannelCommand(id), ct);
                return NoContent();
            });

        // A command's refusal is a 400 with its reason. AccessDeniedException is NOT caught here - it propagates to the
        // AccessDeniedExceptionFilter (403 ProblemDetails); nor is anything else (ExceptionMiddleware's 500).
        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "The notification request was refused.");
            }
        }
    }
}