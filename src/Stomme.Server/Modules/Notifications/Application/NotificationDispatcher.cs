#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// A dispatch the module refused: an invalid request, no resolved site, or a recipient the constraint forbids. Nothing
    /// was persisted. An <see cref="InvalidOperationException"/>, so a producer that references only <c>Stomme.Shared</c>
    /// can catch it without seeing this type. The message states the reason and never carries a recipient address.
    /// </summary>
    public sealed class NotificationDispatchRefusedException : InvalidOperationException
    {
        public NotificationDispatchRefusedException(string reason) : base(reason)
        {
        }
    }

    /// <summary>The optional bus message a dispatch sends when messaging is enabled: a latency hint - the job is authoritative.</summary>
    public sealed record NotificationDispatchHint(Guid RequestId);

    /// <summary>
    /// The implementation of the published <see cref="INotificationDispatcher"/> port (D100 points 3 and 4),
    /// lifted from the predecessor platform <c>NotificationDispatcher</c>: validate, constrain the RECIPIENTS, persist a <c>Queued</c> request
    /// WITH its parameters, and - only when a bus is registered - send a latency hint. Delivery is the
    /// <c>NotificationDispatchJob</c>'s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No open relay.</b> The port carries no principal - producers are server code - so the constraint is on who can be
    /// reached, not on who asks: <see cref="RecipientKind.User"/> must be a user of THIS site;
    /// <see cref="RecipientKind.Email"/> only when the site setting <see cref="NotificationSiteSettingNames.ExternalRecipients"/>
    /// is "True" (and the address parses); <see cref="RecipientKind.Endpoint"/> is ALWAYS refused (BL-78 F9: D100 omitted the
    /// kind the predecessor platform publishes; accepted, it is an open relay and a route around the webhook channel's SSRF guard). A refusal
    /// is logged with its reason and persists nothing.
    /// </para>
    /// <para>
    /// <b>The site is the resolved alias's, never the caller's</b> (<c>SiteState.Alias.SiteId</c>); the job path's mock alias
    /// (<c>SiteId = -1</c>) is refused - a producer running in a job sets the site's alias first. The tenant is the seam's
    /// <see cref="ITenantContext"/>.
    /// </para>
    /// <para>
    /// <b>No <c>[Requires]</c> here, nor on anything the job calls</b> (BL-78 F3): <c>PermissionRowAccessGuard</c> denies
    /// every requirement when there is no <c>HttpContext</c>, which is every job and every in-process producer.
    /// </para>
    /// </remarks>
    public sealed class NotificationDispatcher : INotificationDispatcher
    {
        private readonly INotificationRequestRepository _requests;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ITenantContext _tenantContext;
        private readonly SiteState _siteState;
        private readonly INotificationRecipientDirectory _directory;
        private readonly INotificationSiteSettings _settings;
        private readonly TimeProvider _clock;
        private readonly IServiceProvider _services;
        private readonly ILogger<NotificationDispatcher> _logger;

        public NotificationDispatcher(
            INotificationRequestRepository requests,
            IUnitOfWork unitOfWork,
            ITenantContext tenantContext,
            SiteState siteState,
            INotificationRecipientDirectory directory,
            INotificationSiteSettings settings,
            TimeProvider clock,
            IServiceProvider services,
            ILogger<NotificationDispatcher> logger)
        {
            _requests = requests ?? throw new ArgumentNullException(nameof(requests));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
            _siteState = siteState ?? throw new ArgumentNullException(nameof(siteState));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var siteId = _siteState.Alias?.SiteId ?? -1;
            if (siteId <= 0)
            {
                throw Refuse(siteId, "no site is resolved in this scope (a job must set the site's alias before dispatching)");
            }

            var tenantId = _tenantContext.CurrentTenantId ?? throw Refuse(siteId, "no tenant is resolved in this scope");

            if (string.IsNullOrWhiteSpace(request.TemplateKey))
            {
                throw Refuse(siteId, "the template key is empty");
            }

            if (request.Recipients is null || request.Recipients.Count == 0)
            {
                throw Refuse(siteId, "the request has no recipients");
            }

            if (request.Channels is null || request.Channels.Count == 0)
            {
                throw Refuse(siteId, "the request has no channels");
            }

            if (request.Channels.Any(channel => !Enum.IsDefined(channel)))
            {
                throw Refuse(siteId, "the request names an unknown channel kind");
            }

            if (!Enum.IsDefined(request.Severity))
            {
                throw Refuse(siteId, "the request names an unknown severity");
            }

            var parameters = request.Parameters ?? [];
            if (parameters.Any(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name)))
            {
                throw Refuse(siteId, "a parameter has no name");
            }

            if (parameters.GroupBy(parameter => parameter.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            {
                throw Refuse(siteId, "a parameter name is supplied more than once");
            }

            var recipients = new List<Recipient>(request.Recipients.Count);
            bool? externalAllowed = null;
            for (var index = 0; index < request.Recipients.Count; index++)
            {
                var recipient = request.Recipients[index];
                if (recipient is null || string.IsNullOrWhiteSpace(recipient.Value))
                {
                    throw Refuse(siteId, $"recipient #{index + 1} is empty");
                }

                var value = recipient.Value.Trim();
                switch (recipient.Kind)
                {
                    case NotificationRecipientKind.User:
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
                        {
                            throw Refuse(siteId, $"user recipient #{index + 1} is not a user id");
                        }

                        if (!await _directory.IsSiteUserAsync(siteId, userId, ct))
                        {
                            throw Refuse(siteId, $"user {userId} is not a user of site {siteId}");
                        }

                        recipients.Add(new Recipient(RecipientKind.User, userId.ToString(CultureInfo.InvariantCulture)));
                        break;

                    case NotificationRecipientKind.Email:
                        externalAllowed ??= await _settings.AllowsExternalRecipientsAsync(siteId, ct);
                        if (externalAllowed != true)
                        {
                            throw Refuse(siteId,
                                $"external e-mail recipient #{index + 1} is not allowed: the site setting '{NotificationSiteSettingNames.ExternalRecipients}' is not True");
                        }

                        if (!MailAddress.TryCreate(value, out var address) || !string.Equals(address.Address, value, StringComparison.Ordinal))
                        {
                            throw Refuse(siteId, $"external e-mail recipient #{index + 1} is not a single e-mail address");
                        }

                        recipients.Add(new Recipient(RecipientKind.Email, value));
                        break;

                    case NotificationRecipientKind.Endpoint:
                        throw Refuse(siteId,
                            $"recipient #{index + 1} is an Endpoint recipient, which is never accepted (an endpoint is a channel's configuration, guarded there)");

                    default:
                        throw Refuse(siteId, $"recipient #{index + 1} has an unknown recipient kind");
                }
            }

            var notification = NotificationRequestAggregate.Create(
                Guid.NewGuid(),
                tenantId,
                siteId,
                request.TemplateKey,
                recipients,
                request.Channels.Select(channel => (ChannelKind)(int)channel).ToList(),
                parameters.Select(parameter => new NotificationParameter(parameter.Name.Trim(), parameter.Value ?? string.Empty)).ToList(),
                request.Severity,
                request.Topic,
                createdOnUtc: _clock.GetUtcNow().UtcDateTime);

            await _requests.AddAsync(notification, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // The bus is OPT-IN (Messaging:Enabled). When present it only shortens the wait; a failed send is not a failed
            // dispatch - the request is persisted and the per-minute job will deliver it.
            var bus = _services.GetService<IEventBus>();
            if (bus is not null)
            {
                try
                {
                    await bus.SendLocalAsync(new NotificationDispatchHint(notification.Id), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The dispatch hint for notification request {RequestId} could not be sent ({ExceptionType}); the dispatch job will deliver it.",
                        notification.Id, ex.GetType().Name);
                }
            }

            return notification.Id;
        }

        private NotificationDispatchRefusedException Refuse(int siteId, string reason)
        {
            _logger.LogWarning("Notification dispatch refused on site {SiteId}: {Reason}. Nothing was persisted.", siteId, reason);
            return new NotificationDispatchRefusedException($"The notification dispatch was refused: {reason}.");
        }
    }

    /// <summary>
    /// Handles the optional <see cref="NotificationDispatchHint"/> when messaging is enabled: processes the request NOW if it
    /// is still untouched and due - otherwise the job owns it. Runs under the tenant the bus restores from the message header;
    /// routes the scope to the request's site so the framework-backed settings read the right tenant.
    /// </summary>
    /// <remarks>
    /// Residue (not solved here): the hint and the per-minute job can pick the same untouched request within the same
    /// minute. On RavenDB the second update is a change-vector conflict; the relational module store has no concurrency
    /// token, so a double delivery is possible in that window. Messaging is off by default.
    /// </remarks>
    public sealed class NotificationDispatchHintHandler : IIntegrationEventHandler<NotificationDispatchHint>
    {
        private readonly INotificationRequestRepository _requests;
        private readonly ITenantManager _tenantManager;
        private readonly NotificationDispatchProcessor _processor;
        private readonly TimeProvider _clock;

        public NotificationDispatchHintHandler(
            INotificationRequestRepository requests, ITenantManager tenantManager, NotificationDispatchProcessor processor, TimeProvider clock)
        {
            _requests = requests ?? throw new ArgumentNullException(nameof(requests));
            _tenantManager = tenantManager ?? throw new ArgumentNullException(nameof(tenantManager));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task HandleAsync(NotificationDispatchHint @event, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(@event);

            var request = await _requests.GetByIdAsync(@event.RequestId, ct);
            if (request is null
                || request.CompletedOn is not null
                || request.DigestDeferredUntil is not null
                || request.Attempts.Count > 0
                || request.NextAttemptOn > _clock.GetUtcNow().UtcDateTime)
            {
                return;
            }

            if (FrameworkTenantId.TryToTenantId(request.TenantId, out var frameworkTenantId))
            {
                await _tenantManager.SetAliasAsync(frameworkTenantId, request.SiteId);
            }

            await _processor.ProcessAsync(request, isDigestFlush: false, ct);
        }
    }
}
