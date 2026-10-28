#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Notifications;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>A single template parameter binding supplied with a <see cref="NotificationRequestAggregate"/> (the persisted shape).</summary>
    public sealed record NotificationParameter(string Name, string Value);

    /// <summary>
    /// The dispatch unit a producer creates to "tell someone something": recipients, a template key WITH its
    /// parameters, the channels to reach them over, and the resulting <see cref="DeliveryAttempt"/>s. Lifted from
    /// the predecessor platform <c>NotificationRequest</c> (BL-78); <see cref="Status"/> is still always derived from the attempts.
    /// <para>
    /// <b>Reshaped for Stomme (D100 points 3 and 7).</b> Tenant-owned AND site-scoped (<see cref="SiteId"/>);
    /// every instant a UTC <see cref="DateTime"/>; <see cref="TemplateKeyNormalized"/> maintained here so a key lookup
    /// normalizes both sides (D49); and the Job-backed pipeline's state is SCALAR so the provider-neutral
    /// <c>FindAsync</c> can select due work: <see cref="AttemptCount"/> + <see cref="NextAttemptOn"/> (retry), and
    /// <see cref="DigestDeferredUntil"/>, which replaces the predecessor platform's <c>Attempts.Any(a =&gt; a.Status == DigestQueued)</c>
    /// (a collection predicate FindAsync cannot express). The dispatch <see cref="Parameters"/> are persisted - the
    /// first design lost them, so a deferred render would have seen an empty context.
    /// </para>
    /// <para>
    /// The retry/deferral POLICY (3 retries, 30 s x2, cap 1 h; quiet-hours deferral; the hourly digest flush) belongs
    /// to <c>NotificationDispatchJob</c>; this aggregate only records the state transitions it is told about.
    /// </para>
    /// </summary>
    public sealed class NotificationRequestAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<Recipient> _recipients = [];
        private readonly List<ChannelKind> _requestedChannels = [];
        private readonly List<NotificationParameter> _parameters = [];
        private readonly List<DeliveryAttempt> _attempts = [];
        private DateTime _createdOn;
        private DateTime? _completedOn;
        private DateTime _nextAttemptOn;
        private DateTime? _digestDeferredUntil;

        // Materialisation constructor. Not for application use.
        private NotificationRequestAggregate()
        {
            TenantId = null!;
            TemplateKey = string.Empty;
            TemplateKeyNormalized = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        /// <summary>The site this request belongs to.</summary>
        public int SiteId { get; private set; }

        /// <summary>The <see cref="NotificationTemplateAggregate"/> key whose content this request renders.</summary>
        public string TemplateKey { get; private set; }

        /// <summary><see cref="TemplateKey"/> upper-invariant - the D49 normalized lookup column.</summary>
        public string TemplateKeyNormalized { get; private set; }

        /// <summary>How urgent this request is - drives per-user severity filtering and quiet-hours handling.</summary>
        public NotificationSeverity Severity { get; private set; }

        /// <summary>An optional subscription topic; a recipient who muted it is suppressed. Null = never muted.</summary>
        public string? Topic { get; private set; }

        public NotificationStatus Status { get; private set; }

        /// <summary>How many dispatch passes have consumed a retry (see <see cref="ScheduleRetry"/>).</summary>
        public int AttemptCount { get; private set; }

        /// <summary>When the request is next due for a dispatch pass (UTC). Initially <see cref="CreatedOn"/>.</summary>
        public DateTime NextAttemptOn
        {
            get => UtcInstant.Stamp(_nextAttemptOn);
            private set => _nextAttemptOn = value;
        }

        /// <summary>When a digest-held request is due to be flushed (UTC); null when not held for a digest.</summary>
        public DateTime? DigestDeferredUntil
        {
            get => UtcInstant.Stamp(_digestDeferredUntil);
            private set => _digestDeferredUntil = value;
        }

        /// <summary>When the request was created (UTC).</summary>
        public DateTime CreatedOn
        {
            get => UtcInstant.Stamp(_createdOn);
            private set => _createdOn = value;
        }

        /// <summary>When the pipeline finished with the request (UTC); null while it is still in flight.</summary>
        public DateTime? CompletedOn
        {
            get => UtcInstant.Stamp(_completedOn);
            private set => _completedOn = value;
        }

        public IReadOnlyList<Recipient> Recipients => _recipients;

        public IReadOnlyList<ChannelKind> RequestedChannels => _requestedChannels;

        public IReadOnlyList<NotificationParameter> Parameters => _parameters;

        public IReadOnlyList<DeliveryAttempt> Attempts => _attempts;

        /// <summary>True once every requested channel has at least one recorded attempt.</summary>
        public bool AllChannelsAttempted =>
            _requestedChannels.All(channel => _attempts.Any(attempt => attempt.Channel == channel));

        /// <summary>The D49 normalization of a template key: trimmed, upper-invariant. Apply to BOTH sides of a lookup.</summary>
        public static string NormalizeTemplateKey(string templateKey) => (templateKey ?? string.Empty).Trim().ToUpperInvariant();

        public static NotificationRequestAggregate Create(
            Guid id,
            TenantId tenantId,
            int siteId,
            string templateKey,
            IReadOnlyList<Recipient> recipients,
            IReadOnlyList<ChannelKind> requestedChannels,
            IReadOnlyList<NotificationParameter>? parameters = null,
            NotificationSeverity severity = NotificationSeverity.Information,
            string? topic = null,
            DateTime? createdOnUtc = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A notification request must belong to a site.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
            ArgumentNullException.ThrowIfNull(recipients);
            ArgumentNullException.ThrowIfNull(requestedChannels);

            if (recipients.Count == 0)
            {
                throw new ArgumentException("A notification needs at least one recipient.", nameof(recipients));
            }

            if (requestedChannels.Count == 0)
            {
                throw new ArgumentException("A notification needs at least one channel to send over.", nameof(requestedChannels));
            }

            if (recipients.Any(recipient => recipient is null))
            {
                throw new ArgumentException("A recipient cannot be null.", nameof(recipients));
            }

            var parameterList = parameters ?? [];
            if (parameterList.Any(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name)))
            {
                throw new ArgumentException("Every parameter needs a name.", nameof(parameters));
            }

            var createdOn = createdOnUtc is { } supplied ? UtcInstant.Require(supplied, nameof(createdOnUtc)) : DateTime.UtcNow;
            var trimmedKey = templateKey.Trim();

            var request = new NotificationRequestAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                TemplateKey = trimmedKey,
                TemplateKeyNormalized = NormalizeTemplateKey(trimmedKey),
                Severity = severity,
                Topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
                Status = NotificationStatus.Queued,
                CreatedOn = createdOn,
                NextAttemptOn = createdOn,
            };

            request._recipients.AddRange(recipients);
            request._requestedChannels.AddRange(requestedChannels.Distinct());
            request._parameters.AddRange(parameterList);
            return request;
        }

        /// <summary>
        /// Records the outcome of delivering this request over one of its <see cref="RequestedChannels"/> and
        /// recomputes <see cref="Status"/>. Raises <see cref="NotificationSentEvent"/> on success or
        /// <see cref="NotificationFailedEvent"/> on failure for that channel.
        /// </summary>
        public DeliveryAttempt RecordAttempt(ChannelKind channel, DeliveryStatus status, DateTime attemptedOnUtc, string? error = null)
        {
            if (!_requestedChannels.Contains(channel))
            {
                throw new InvalidOperationException($"Channel '{channel}' was not requested by this notification.");
            }

            var attempt = new DeliveryAttempt(Guid.NewGuid(), channel, status, attemptedOnUtc, error);
            _attempts.Add(attempt);
            RecomputeStatus();

            // A Skipped attempt is a deliberate, preference-driven non-delivery - neither a send nor a failure - so it
            // raises no domain event.
            if (status == DeliveryStatus.Succeeded)
            {
                RaiseDomainEvent(new NotificationSentEvent(Id, channel));
            }
            else if (status == DeliveryStatus.Failed)
            {
                RaiseDomainEvent(new NotificationFailedEvent(Id, channel, attempt.Error!));
            }

            return attempt;
        }

        /// <summary>Re-arms <see cref="NextAttemptOn"/> WITHOUT consuming a retry (the quiet-hours deferral).</summary>
        public void DeferUntil(DateTime nextAttemptOnUtc) =>
            NextAttemptOn = UtcInstant.Require(nextAttemptOnUtc, nameof(nextAttemptOnUtc));

        /// <summary>Consumes one retry (<see cref="AttemptCount"/> + 1) and re-arms <see cref="NextAttemptOn"/>.</summary>
        public void ScheduleRetry(DateTime nextAttemptOnUtc)
        {
            NextAttemptOn = UtcInstant.Require(nextAttemptOnUtc, nameof(nextAttemptOnUtc));
            AttemptCount++;
        }

        /// <summary>Holds the request for the digest flush due at <paramref name="untilUtc"/>.</summary>
        public void DeferToDigest(DateTime untilUtc) =>
            DigestDeferredUntil = UtcInstant.Require(untilUtc, nameof(untilUtc));

        /// <summary>Releases a digest hold (the flush picked the request up).</summary>
        public void ReleaseFromDigest() => DigestDeferredUntil = null;

        /// <summary>
        /// Erasure (D112): drops every recipient entry naming the user, so the request keeps no link to the person; true
        /// when one was there. A request left with no recipient is the caller's to delete.
        /// </summary>
        public bool RemoveRecipient(int userId)
        {
            var address = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var removed = _recipients.RemoveAll(r => r.Kind == RecipientKind.User && r.Address == address);
            return removed > 0;
        }

        /// <summary>Marks the pipeline finished with the request at <paramref name="completedOnUtc"/> (the retention clock).</summary>
        public void Complete(DateTime completedOnUtc) =>
            CompletedOn = UtcInstant.Require(completedOnUtc, nameof(completedOnUtc));

        /// <summary>
        /// Re-opens a COMPLETED request that did not fully send (an operator's retry, BL-78 step 5): it becomes due again at
        /// <paramref name="nextAttemptOnUtc"/> with a fresh retry budget and no digest hold. The recorded attempts are KEPT - the
        /// next pass re-attempts only the channels that are not yet resolved. A request that is still in flight, or that was
        /// fully sent, cannot be re-opened.
        /// </summary>
        public void Reopen(DateTime nextAttemptOnUtc)
        {
            var next = UtcInstant.Require(nextAttemptOnUtc, nameof(nextAttemptOnUtc));
            if (CompletedOn is null)
            {
                throw new InvalidOperationException("The notification request is still in flight; it is not finished, so there is nothing to retry.");
            }

            if (Status == NotificationStatus.Sent)
            {
                throw new InvalidOperationException("The notification request was sent on every requested channel; there is nothing to retry.");
            }

            _completedOn = null;
            _digestDeferredUntil = null;
            AttemptCount = 0;
            NextAttemptOn = next;
        }

        private void RecomputeStatus()
        {
            // DigestQueued is a pending placeholder - excluded when deciding whether a channel was genuinely attempted.
            var realAttempts = _attempts.Where(a => a.Status != DeliveryStatus.DigestQueued).ToList();

            if (realAttempts.Count == 0)
            {
                Status = NotificationStatus.Queued;
                return;
            }

            // A channel is "resolved" once it has a successful or a deliberately-skipped attempt.
            bool ChannelResolved(ChannelKind channel) =>
                realAttempts.Any(attempt => attempt.Channel == channel
                    && attempt.Status is DeliveryStatus.Succeeded or DeliveryStatus.Skipped);

            var allResolved = _requestedChannels.All(ChannelResolved);
            var anyResolved = _requestedChannels.Any(ChannelResolved);
            var allAttempted = _requestedChannels.All(channel => realAttempts.Any(a => a.Channel == channel));

            Status = allResolved
                ? NotificationStatus.Sent
                : (allAttempted && !anyResolved)
                    ? NotificationStatus.Failed
                    : NotificationStatus.PartiallyFailed;
        }
    }
}
