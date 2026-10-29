using System;
using System.Collections.Generic;
using Stomme.Health;

namespace Stomme.Models.Health
{
    /// <summary>
    /// The wire shapes the health API answers with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flat records in <c>Stomme.Shared</c>, carrying no domain type: the Client references this assembly and must
    /// not see <c>HealthRollupAggregate</c>, <c>HealthSubject</c> or anything else behind the module's boundary.
    /// </para>
    /// <para>
    /// <b><see cref="HealthSubjectView.Description"/> is the privileged field and is nullable for a reason.</b> It
    /// carries whatever the probe said, which for a store failure is the raw exception message — host, port,
    /// database, user. The API fills it only for a caller entitled to it: a site's <c>health:view</c> holder sees
    /// the descriptions of that site's OWN subjects, and a Tenant- or Installation-scope subject arrives with
    /// state and timestamp but a <c>null</c> description unless the caller is Host. A missing description is
    /// therefore normal and the UI must render it as such, not as an error.
    /// </para>
    /// </remarks>
    public sealed record HealthSubjectView(
        string Subject,
        string Dependency,
        HealthProbeScope Scope,
        HealthState State,
        string? Description,
        double LatencyMs,
        DateTime? BadSinceUtc,
        DateTime? AlertRaisedOnUtc,
        DateTime? AcknowledgedOnUtc,
        int AlertCount);

    /// <summary>One window of one subject's history.</summary>
    public sealed record HealthRollupView(
        string Subject,
        HealthProbeScope Scope,
        string Window,
        DateTime PeriodStartUtc,
        int Checks,
        int Healthy,
        int Degraded,
        int Unhealthy,
        double AverageLatencyMs,
        double MaxLatencyMs,
        double AvailabilityPercent);

    /// <summary>What a "run probes now" did: how many probes ran and the worst state they reported.</summary>
    public sealed record HealthRunNowResult(int ProbesRun, HealthState Worst);

    /// <summary>What the admin page shows: the current picture plus whatever is outstanding.</summary>
    /// <param name="ProbingEnabled">
    /// Whether the probe JOB runs on this install (<c>Health:Enabled</c>). Without it the page could not tell "not
    /// checked YET" from "never going to be checked" - found by driving: with probing off it promised
    /// a tick "every minute" that would never come.
    /// </param>
    public sealed record HealthDashboard(
        HealthState Worst,
        DateTime? LastTickUtc,
        IReadOnlyList<HealthSubjectView> Subjects,
        IReadOnlyList<HealthSubjectView> OutstandingAlerts,
        bool ProbingEnabled = true);

    /// <summary>A metric the install can read, and its last value when one was taken.</summary>
    public sealed record HealthMetricView(string Name, string Unit, string? Description, double? Value);
}
