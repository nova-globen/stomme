#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>What one site pass did - the job's notes and the tests' observations.</summary>
    public sealed record NotificationSiteRunResult(
        int Delivered, int Deferred, int DigestHeld, int Retried, int Failed, int Flushed, int Purged, bool HourlyPassRan);

    /// <summary>
    /// The Job-backed delivery pipeline for ONE site - the policy the predecessor platform spread over
    /// <c>NotificationDelivery</c>, the send/retry commands, the bus consumers and <c>NotificationDigestJob</c>, made scalar
    /// and pull-based: <c>NotificationDispatchJob</c> calls <see cref="RunSiteAsync"/> per site per minute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A pass over a due request:</b> template by <c>KeyNormalized</c> -&gt; the recipients' preferences -&gt; digest hold
    /// (every recipient a digest-mode user: <c>DigestQueued</c> attempts + <c>DigestDeferredUntil</c>, no retry consumed) -&gt;
    /// quiet hours (everyone reachable asleep, non-critical: <c>NextAttemptOn</c> re-armed to the window end, no retry
    /// consumed, no attempt) -&gt; render ONCE per encoding target with a per-render budget -&gt; per requested channel still
    /// unresolved: no eligible recipient = <c>Skipped</c>; no enabled channel of the kind = <c>Failed</c>; otherwise every
    /// enabled channel of the kind fans out through <see cref="NotificationChannelDelivery"/> (resolver null = <c>Skipped</c>).
    /// Then the policy: every channel resolved = complete; otherwise <see cref="MaxRetries"/> scalar retries
    /// (<c>AttemptCount</c>, <c>NextAttemptOn = now + 30 s x 2^(n-1)</c>, capped at 1 h) and then complete as Failed.
    /// </para>
    /// <para>
    /// <b>The hourly pass</b> (throttled by the site setting <see cref="NotificationSiteSettingNames.LastHourlyPass"/>):
    /// flushes <c>DigestDeferredUntil &lt;= now</c> with the digest hold bypassed, then purges COMPLETED requests older than
    /// <see cref="NotificationSiteSettingNames.RequestRetention"/> (default 30 days). A restart loses nothing: the marker is
    /// persisted, and running the pass early is harmless (a flush of due holds and an idempotent purge).
    /// </para>
    /// <para>
    /// <b>One write per request per pass</b>: every mutation of a request lands in ONE <c>UpdateAsync</c> + unit-of-work
    /// save, on the instance this scope loaded - never a second instance with the same id (the BL-11 identity-map class).
    /// No <c>[Requires]</c> anywhere on this path (BL-78 F3: there is no <c>HttpContext</c> in a job).
    /// </para>
    /// <para>
    /// <b>The render budget</b> (BL-78 F1): <c>HostedServiceBase.ExecuteJobAsync</c> passes no cancellation token, so each
    /// render gets its own <see cref="NotificationTemplateRenderer.CreateRenderCancellation"/> on top of Fluid's
    /// <c>MaxSteps</c>/<c>MaxRecursion</c>.
    /// </para>
    /// </remarks>
    public sealed class NotificationDispatchProcessor
    {
        /// <summary>Automatic retries after the first pass; the request completes Failed after the last.</summary>
        public const int MaxRetries = 3;

        public const int DefaultRequestRetentionDays = 30;

        public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

        public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

        public static readonly TimeSpan HourlyPassInterval = TimeSpan.FromHours(1);

        private readonly INotificationRequestRepository _requests;
        private readonly INotificationTemplateRepository _templates;
        private readonly INotificationChannelRepository _channels;
        private readonly NotificationPreferenceResolver _preferences;
        private readonly NotificationChannelDelivery _delivery;
        private readonly NotificationTemplateRenderer _renderer;
        private readonly INotificationSiteSettings _settings;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _clock;
        private readonly ILogger<NotificationDispatchProcessor> _logger;

        public NotificationDispatchProcessor(
            INotificationRequestRepository requests,
            INotificationTemplateRepository templates,
            INotificationChannelRepository channels,
            NotificationPreferenceResolver preferences,
            NotificationChannelDelivery delivery,
            NotificationTemplateRenderer renderer,
            INotificationSiteSettings settings,
            IUnitOfWork unitOfWork,
            TimeProvider clock,
            ILogger<NotificationDispatchProcessor> logger)
        {
            _requests = requests ?? throw new ArgumentNullException(nameof(requests));
            _templates = templates ?? throw new ArgumentNullException(nameof(templates));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
            _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The names the default channels are created under.</summary>
        public const string DefaultEmailChannelName = "E-mail";

        public const string DefaultInAppChannelName = "In-app";

        /// <summary>
        /// The site's enabled channels, after creating a default for e-mail and in-app where the site has NO channel of that
        /// kind (a new site had none, so every notification failed and was retried until an administrator created
        /// them). Neither kind needs settings of its own - e-mail goes through the site's notification queue and SMTP settings,
        /// in-app into the inbox - so a default is complete. A channel an administrator disabled or renamed is left alone;
        /// webhook and SMS still need configuring.
        /// </summary>
        private async Task<IReadOnlyList<NotificationChannelAggregate>> EnabledWithDefaultsAsync(NotificationRequestAggregate request, IEnumerable<ChannelKind> pending, CancellationToken ct)
        {
            var wanted = pending.Where(kind => kind is ChannelKind.Email or ChannelKind.InApp).Distinct().ToList();
            if (wanted.Count > 0)
            {
                var existing = await _channels.ListBySiteAsync(request.SiteId, ct);
                var missing = wanted.Where(kind => existing.All(channel => channel.Kind != kind)).ToList();
                foreach (var kind in missing)
                {
                    var name = kind == ChannelKind.Email ? DefaultEmailChannelName : DefaultInAppChannelName;
                    await _channels.AddAsync(NotificationChannelAggregate.Create(Guid.NewGuid(), request.TenantId, request.SiteId, kind, name,
                        new TransportSettings(kind, null, null, Array.Empty<TransportOption>())), ct);
                    _logger.LogInformation("Site {SiteId} had no {Kind} notification channel; the default '{Name}' was created.", request.SiteId, kind, name);
                }

                if (missing.Count > 0)
                {
                    await _unitOfWork.SaveChangesAsync(ct);
                }
            }

            return await _channels.ListEnabledAsync(request.SiteId, ct);
        }

        /// <summary>The back-off before the given 1-based retry: 30 s doubled per retry, capped at one hour.</summary>
        public static TimeSpan BackoffFor(int retry)
        {
            var seconds = RetryBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, retry - 1));
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
        }

        /// <summary>One site's minute: the hourly pass when it is due, then every due request.</summary>
        public async Task<NotificationSiteRunResult> RunSiteAsync(int siteId, CancellationToken ct = default)
        {
            var tally = new Tally();
            var now = Now();

            var lastPass = await _settings.GetLastHourlyPassAsync(siteId, ct);
            if (lastPass is null || now - lastPass.Value >= HourlyPassInterval)
            {
                await RunHourlyPassAsync(siteId, now, tally, ct);
                await _settings.SetLastHourlyPassAsync(siteId, now, ct);
                tally.HourlyPassRan = true;
            }

            foreach (var request in await _requests.ListDueAsync(siteId, now, ct))
            {
                await ProcessTalliedAsync(request, isDigestFlush: false, tally, ct);
            }

            return tally.ToResult();
        }

        /// <summary>One pass over one request, with the policy applied and the request saved once.</summary>
        public async Task<DeliveryOutcome> ProcessAsync(NotificationRequestAggregate request, bool isDigestFlush, CancellationToken ct = default) =>
            await ProcessTalliedAsync(request, isDigestFlush, new Tally(), ct);

        private async Task RunHourlyPassAsync(int siteId, DateTime now, Tally tally, CancellationToken ct)
        {
            foreach (var held in await _requests.ListDigestDueAsync(siteId, now, ct))
            {
                held.ReleaseFromDigest();
                await ProcessTalliedAsync(held, isDigestFlush: true, tally, ct);
                tally.Flushed++;
            }

            var retention = await _settings.GetRequestRetentionAsync(siteId, ct);
            if (retention <= TimeSpan.Zero)
            {
                retention = TimeSpan.FromDays(DefaultRequestRetentionDays);
            }

            foreach (var expired in await _requests.ListPurgeableAsync(siteId, now - retention, ct))
            {
                await _requests.DeleteAsync(expired, ct);
                tally.Purged++;
            }

            await _unitOfWork.SaveChangesAsync(ct);
        }

        private async Task<DeliveryOutcome> ProcessTalliedAsync(NotificationRequestAggregate request, bool isDigestFlush, Tally tally, CancellationToken ct)
        {
            var now = Now();
            DeliveryOutcome outcome;
            try
            {
                outcome = await DeliverAsync(request, isDigestFlush, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A defect or a store fault while delivering one request must not end the pass for the others. The request
                // keeps its state (it is re-selected next minute); the exception TYPE only is logged (messages can carry secrets).
                _logger.LogError("Notification request {RequestId} on site {SiteId} could not be processed ({ExceptionType}); it stays due.",
                    request.Id, request.SiteId, ex.GetType().Name);
                tally.Failed++;
                return DeliveryOutcome.Completed;
            }

            if (outcome.IsDigestQueued)
            {
                tally.DigestHeld++;
            }
            else if (outcome.DeferFor is { } deferFor)
            {
                request.DeferUntil(now + deferFor);
                tally.Deferred++;
            }
            else if (request.CompletedOn is null)
            {
                if (request.Status == NotificationStatus.Sent)
                {
                    request.Complete(now);
                    tally.Delivered++;
                }
                else if (request.AttemptCount >= MaxRetries)
                {
                    request.Complete(now);
                    tally.Failed++;
                    _logger.LogWarning("Notification request {RequestId} on site {SiteId} completed {Status} after {Retries} retries.",
                        request.Id, request.SiteId, request.Status, request.AttemptCount);
                }
                else
                {
                    request.ScheduleRetry(now + BackoffFor(request.AttemptCount + 1));
                    tally.Retried++;
                }
            }

            try
            {
                await _requests.UpdateAsync(request, ct);
                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch (Stomme.Persistence.Abstractions.ConcurrencyConflictException)
            {
                // Another writer changed the request since this pass loaded it. The erasure of its recipient deletes or scrubs
                // the row from its own job, and a second pass is the other candidate. That write wins; this pass leaves the
                // request to it and goes on with the site's other requests. Before BL-188 the conflict escaped
                // the pass, so one erased request stopped every other delivery of the site for the minute, logged as an
                // Error. A failed write leaves nothing to poison the next one: EfRepository discards it and the RavenDB
                // repository writes through a session of its own. The File store checks no concurrency and never raises this.
                _logger.LogInformation("Notification request {RequestId} on site {SiteId} was changed by another writer during the pass; it is left to that write.",
                    request.Id, request.SiteId);
            }

            return outcome;
        }

        /// <summary>The delivery pass itself (lifted from the predecessor platform <c>NotificationDelivery.DeliverAsync</c>); mutates the request only.</summary>
        private async Task<DeliveryOutcome> DeliverAsync(NotificationRequestAggregate request, bool isDigestFlush, DateTime now, CancellationToken ct)
        {
            bool Resolved(ChannelKind kind) =>
                request.Attempts.Any(attempt => attempt.Channel == kind && attempt.Status is DeliveryStatus.Succeeded or DeliveryStatus.Skipped);

            var pending = request.RequestedChannels.Where(kind => !Resolved(kind)).ToList();
            if (pending.Count == 0)
            {
                return DeliveryOutcome.Completed;
            }

            var plan = await _preferences.ResolveAsync(request, ct);

            // A request that was already held once has been flushed: it is never held again (its DigestQueued attempts stay).
            var alreadyHeld = request.Attempts.Any(attempt => attempt.Status == DeliveryStatus.DigestQueued);
            if (!isDigestFlush && !alreadyHeld && plan.ShouldDeferToDigest())
            {
                foreach (var kind in pending)
                {
                    request.RecordAttempt(kind, DeliveryStatus.DigestQueued, now);
                }

                request.DeferToDigest(NextHour(now));
                return DeliveryOutcome.ForDigest;
            }

            if (plan.QuietDeferral(pending, now) is { } deferFor)
            {
                return DeliveryOutcome.Deferred(deferFor);
            }

            var template = await _templates.GetByKeyAsync(request.SiteId, request.TemplateKey, ct);
            if (template is null)
            {
                foreach (var kind in pending)
                {
                    request.RecordAttempt(kind, DeliveryStatus.Failed, now, $"No template found for key '{request.TemplateKey}'.");
                }

                return DeliveryOutcome.Completed;
            }

            var content = SelectContent(template, plan.PreferredLocale());
            var parameters = request.Parameters
                .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (string?)group.Last().Value, StringComparer.OrdinalIgnoreCase);
            var enabled = await EnabledWithDefaultsAsync(request, pending, ct);
            var rendered = new Dictionary<NotificationRenderTarget, NotificationRenderResult>();

            foreach (var kind in pending)
            {
                var eligible = plan.EligibleRecipients(kind);
                if (eligible.Count == 0)
                {
                    // Every targeted recipient suppressed this channel - a deliberate non-delivery that resolves the channel.
                    request.RecordAttempt(kind, DeliveryStatus.Skipped, now, "Suppressed by recipient notification preferences.");
                    continue;
                }

                var configured = enabled.Where(channel => channel.Kind == kind).ToList();
                if (configured.Count == 0)
                {
                    request.RecordAttempt(kind, DeliveryStatus.Failed, now, $"No enabled channel is configured for {kind}.");
                    continue;
                }

                var target = NotificationTemplateRenderer.TargetFor(kind);
                if (!rendered.TryGetValue(target, out var render))
                {
                    using var budget = NotificationTemplateRenderer.CreateRenderCancellation(ct);
                    render = await _renderer.RenderAsync(content, parameters, target, budget.Token);
                    rendered[target] = render;
                }

                if (!render.Succeeded || render.Payload is null)
                {
                    request.RecordAttempt(kind, DeliveryStatus.Failed, now, $"The template '{template.Key}' could not be rendered: {render.Error}");
                    continue;
                }

                foreach (var channel in configured)
                {
                    await _delivery.DeliverToRecipientsAsync(request.SiteId, request, channel, eligible, render.Payload, now, ct);
                }
            }

            return DeliveryOutcome.Completed;
        }

        // The recipient's preferred locale, then an English locale, then the first content (a template always has one).
        private static Content SelectContent(NotificationTemplateAggregate template, string? preferredLocale)
        {
            LocalizedContent? match = null;
            if (!string.IsNullOrWhiteSpace(preferredLocale))
            {
                match = template.Contents.FirstOrDefault(content =>
                    content.Locale.Code.Equals(preferredLocale, StringComparison.OrdinalIgnoreCase));
            }

            match ??= template.Contents.FirstOrDefault(content => content.Locale.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            return (match ?? template.Contents[0]).Content;
        }

        /// <summary>The next top of the hour strictly after <paramref name="now"/> - when a digest hold becomes flushable.</summary>
        internal static DateTime NextHour(DateTime now) =>
            new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);

        private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

        private sealed class Tally
        {
            public int Delivered;
            public int Deferred;
            public int DigestHeld;
            public int Retried;
            public int Failed;
            public int Flushed;
            public int Purged;
            public bool HourlyPassRan;

            public NotificationSiteRunResult ToResult() =>
                new(Delivered, Deferred, DigestHeld, Retried, Failed, Flushed, Purged, HourlyPassRan);
        }
    }
}
