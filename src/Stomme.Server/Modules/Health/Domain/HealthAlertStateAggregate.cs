#nullable enable
using System;
using Stomme.Health;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// What we currently believe about one subject, and what we have already told anyone about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This row is the reason the alerting is not a per-process dictionary.</b> Tracking transitions in
    /// a <c>Dictionary</c> inside one process has three consequences: a restart forgets that
    /// an alert was already raised and raises it again; a second host over the same database alerts a second time
    /// for the same outage; and nothing can ever be acknowledged, because there is nowhere to record that someone
    /// did. Putting the state in a row fixes all three, and is what makes "a recovery is sent exactly once per
    /// raised alert" a property of the data rather than of a process's uptime.
    /// </para>
    /// <para>
    /// <b>One row per (tenant, scope, site, subject).</b> For an Installation- or Tenant-scope subject that means
    /// ONE row per tenant rather than one per site — so a shared store going down raises one alert, not one for
    /// every site on it. That is the difference between a monitoring feature and a mail bomb on the worst day of
    /// an operator's year. The tenant is in the key because two tenants can share one physical database and site
    /// ids are not unique across them; see <c>HealthIds</c>.
    /// </para>
    /// <para>
    /// <b>The description is privileged.</b> It carries whatever the probe said, which for a store failure is the
    /// raw exception message — host, port, database, user. It lives here so the gated admin API can show it, and
    /// it is never put into a notification; those carry the exception TYPE.
    /// </para>
    /// </remarks>
    public sealed class HealthAlertStateAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private HealthAlertStateAggregate()
        {
            TenantId = null!;
            Subject = null!;
        }

        public TenantId TenantId { get; private set; }

        public HealthProbeScope Scope { get; private set; }

        /// <summary>The site, or <c>0</c> for an Installation- or Tenant-scope row.</summary>
        public int SiteId { get; private set; }

        public HealthSubject Subject { get; private set; }

        /// <summary>What the last tick saw.</summary>
        public HealthState CurrentState { get; private set; }

        /// <summary>The worst state anyone has actually been TOLD about. Healthy means nothing is outstanding.</summary>
        public HealthState LastAlertedState { get; private set; }

        /// <summary>When the subject first stopped being Healthy in the current bad spell.</summary>
        public DateTime? BadSinceUtc { get; private set; }

        /// <summary>
        /// When a scheduled re-check comes due — the durable form of a deferred re-check message. The
        /// probe job sweeps it; nothing depends on a bus being enabled.
        /// </summary>
        public DateTime? NextEvaluationOnUtc { get; private set; }

        /// <summary>The state the pending re-check was armed FOR, so an improvement below it suppresses the raise.</summary>
        public HealthState? ScheduledForState { get; private set; }

        public DateTime? AlertRaisedOnUtc { get; private set; }

        public DateTime? AlertClearedOnUtc { get; private set; }

        /// <summary>
        /// No new raise before this instant. Without it, a dependency flapping either side of the threshold
        /// re-alerts forever — which is how a monitoring system teaches its operators to ignore it.
        /// </summary>
        public DateTime? CooldownUntilUtc { get; private set; }

        /// <summary>How many alerts this subject has ever raised — the UI's "is this chronic?" column.</summary>
        public int AlertCount { get; private set; }

        /// <summary>The last probe description. PRIVILEGED — see the remarks on this type.</summary>
        public string? LastDescription { get; private set; }

        /// <summary>When an operator acknowledged the outstanding alert, if they have.</summary>
        public DateTime? AcknowledgedOnUtc { get; private set; }

        public static HealthAlertStateAggregate For(TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(subject);
            HealthRollupAggregate.GuardScope(scope, siteId);

            return new HealthAlertStateAggregate
            {
                Id = HealthIds.ForAlertState(tenantId, scope, siteId, subject),
                TenantId = tenantId,
                Scope = scope,
                SiteId = siteId,
                Subject = subject,
                CurrentState = HealthState.Healthy,
                LastAlertedState = HealthState.Healthy,
                AlertCount = 0
            };
        }

        /// <summary>
        /// Records what a tick saw, and returns the state it was in BEFORE — which is what the transition machine
        /// needs and what a caller cannot reconstruct afterwards.
        /// </summary>
        public HealthState Observe(HealthState state, string? description, DateTime nowUtc)
        {
            var previous = CurrentState;
            var now = HealthTime.AsUtc(nowUtc);

            CurrentState = state;
            LastDescription = description;

            if (state == HealthState.Healthy)
            {
                BadSinceUtc = null;
            }
            else if (previous == HealthState.Healthy || BadSinceUtc is null)
            {
                BadSinceUtc = now;
            }

            return previous;
        }

        /// <summary>Arms a re-check for <paramref name="state"/>, due after <paramref name="delay"/>.</summary>
        public void ScheduleRecheck(HealthState state, TimeSpan delay, DateTime nowUtc)
        {
            ScheduledForState = state;
            NextEvaluationOnUtc = HealthTime.AsUtc(nowUtc).Add(delay);
        }

        /// <summary>Disarms a pending re-check without raising anything.</summary>
        public void ClearRecheck()
        {
            ScheduledForState = null;
            NextEvaluationOnUtc = null;
        }

        /// <summary>Whether a re-check is armed and due at <paramref name="nowUtc"/>.</summary>
        public bool RecheckIsDue(DateTime nowUtc) =>
            NextEvaluationOnUtc is { } due && HealthTime.AsUtc(nowUtc) >= HealthTime.AsUtc(due);

        /// <summary>Whether a new raise is suppressed by the cooldown at <paramref name="nowUtc"/>.</summary>
        public bool InCooldown(DateTime nowUtc) =>
            CooldownUntilUtc is { } until && HealthTime.AsUtc(nowUtc) < HealthTime.AsUtc(until);

        /// <summary>
        /// Records that an alert has been raised at <paramref name="state"/>. Disarms the re-check, clears any
        /// previous acknowledgement (a NEW alert is not acknowledged by having acknowledged the last one), and
        /// counts it.
        /// </summary>
        public void Raise(HealthState state, DateTime nowUtc)
        {
            var now = HealthTime.AsUtc(nowUtc);

            LastAlertedState = state;
            AlertRaisedOnUtc = now;
            AlertClearedOnUtc = null;
            AcknowledgedOnUtc = null;
            AlertCount++;
            ClearRecheck();
        }

        /// <summary>
        /// Records that the recovery has been sent, and opens the cooldown. <b>Exactly once per raised alert</b>:
        /// the caller asks <see cref="HasOutstandingAlert"/> first, and this clears it, so a second recovery for
        /// the same alert has nothing to clear.
        /// </summary>
        public void Clear(DateTime nowUtc, TimeSpan cooldown)
        {
            var now = HealthTime.AsUtc(nowUtc);

            LastAlertedState = HealthState.Healthy;
            AlertClearedOnUtc = now;
            AcknowledgedOnUtc = null;
            CooldownUntilUtc = now.Add(cooldown);
            ClearRecheck();
        }

        /// <summary>Whether someone has been told about a problem that has not yet been cleared.</summary>
        public bool HasOutstandingAlert => LastAlertedState != HealthState.Healthy;

        /// <summary>Acknowledges the outstanding alert. Acknowledging silences nothing; it records who knows.</summary>
        public void Acknowledge(DateTime nowUtc) => AcknowledgedOnUtc = HealthTime.AsUtc(nowUtc);
    }
}
