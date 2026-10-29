#nullable enable
using System;
using Stomme.Health;

namespace Stomme.Modules.Health.Application
{
    /// <summary>What the alert step should do about a subject.</summary>
    public enum HealthAlertActionKind
    {
        /// <summary>Nothing — a blip, an improvement, or a state somebody has already been told about.</summary>
        None = 0,

        /// <summary>Arm a re-check; alert only if it is still at least this bad when the re-check comes due.</summary>
        ScheduleRecheck = 1,

        /// <summary>Send the recovery notice now.</summary>
        DispatchRecovery = 2,

        /// <summary>The threshold elapsed and it is still bad — raise.</summary>
        RaiseAlert = 3
    }

    /// <summary>The policy's answer. <see cref="Delay"/> is meaningful only for a scheduled re-check.</summary>
    public sealed record HealthAlertDecision(HealthAlertActionKind Kind, HealthState State, TimeSpan Delay)
    {
        public static readonly HealthAlertDecision None = new(HealthAlertActionKind.None, HealthState.Healthy, TimeSpan.Zero);
    }

    /// <summary>How long a subject must stay bad before anyone is told, and how long after a recovery before it can raise again.</summary>
    public sealed record HealthAlertThresholds(TimeSpan UnhealthyAlertAfter, TimeSpan DegradedAlertAfter, TimeSpan Cooldown);

    /// <summary>
    /// The transition machine, pure and without I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The branch ORDER is the algorithm, not a style choice.</b> Recovery is tested FIRST, because a rule of
    /// the shape "alert when the current state is worse than the last alerted state" can never fire for a
    /// recovery — Healthy (0) is not greater than Unhealthy (2) — and a design that reordered these would silently
    /// lose every recovery notice while looking correct. That exact mistake was caught in review before this row
    /// was written; the order below is the answer to it.
    /// </para>
    /// <para>
    /// <b>"Sustained" is a re-check, not a timer.</b> Going bad only ARMS a re-check; the raise happens when that
    /// re-check comes due and the subject is still at least as bad as it was when armed. An improvement below the
    /// armed state suppresses the raise, which is what makes a blip silent without any need for a delayed message,
    /// a bus, or a process that stays alive.
    /// </para>
    /// <para>
    /// <b>Unhealthy→Degraded arms nothing</b>, deliberately: it is an improvement, the outstanding alert already
    /// told someone the subject is bad, and it clears on the eventual recovery. Arming there would alert twice
    /// for one outage, on its way to getting better.
    /// </para>
    /// <para>
    /// The decision carries no channels, severity or template key. Routing is the notification module's job, and
    /// baking three template keys into the policy would make it untestable without the notification stack. The
    /// policy answers only "what should happen", and the caller decides who hears about it.
    /// </para>
    /// </remarks>
    public sealed class HealthAlertPolicy
    {
        private readonly HealthAlertThresholds _thresholds;

        public HealthAlertPolicy(HealthAlertThresholds thresholds) => _thresholds = thresholds;

        /// <summary>Decides what a change of state means.</summary>
        public HealthAlertDecision OnTransition(HealthState previousState, HealthState currentState)
        {
            // Recovery FIRST - see the remarks. Back to healthy from anything worse.
            if (currentState == HealthState.Healthy && previousState != HealthState.Healthy)
            {
                return new HealthAlertDecision(HealthAlertActionKind.DispatchRecovery, HealthState.Healthy, TimeSpan.Zero);
            }

            // Into unhealthy from any better state, escalation from degraded included.
            if (currentState == HealthState.Unhealthy && previousState != HealthState.Unhealthy)
            {
                return new HealthAlertDecision(HealthAlertActionKind.ScheduleRecheck, HealthState.Unhealthy, _thresholds.UnhealthyAlertAfter);
            }

            // Into degraded from healthy only. Unhealthy->Degraded is an improvement and arms nothing.
            if (currentState == HealthState.Degraded && previousState == HealthState.Healthy)
            {
                return new HealthAlertDecision(HealthAlertActionKind.ScheduleRecheck, HealthState.Degraded, _thresholds.DegradedAlertAfter);
            }

            return HealthAlertDecision.None;
        }

        /// <summary>Decides whether an armed re-check that has come due should raise.</summary>
        public HealthAlertDecision OnRecheck(HealthState scheduledState, HealthState currentState)
        {
            // Recovered, or improved below what the re-check was armed for: the problem was not sustained.
            if (currentState == HealthState.Healthy || (int)currentState < (int)scheduledState)
            {
                return HealthAlertDecision.None;
            }

            return currentState switch
            {
                HealthState.Unhealthy => new HealthAlertDecision(HealthAlertActionKind.RaiseAlert, HealthState.Unhealthy, TimeSpan.Zero),
                HealthState.Degraded => new HealthAlertDecision(HealthAlertActionKind.RaiseAlert, HealthState.Degraded, TimeSpan.Zero),
                _ => HealthAlertDecision.None
            };
        }
    }
}
