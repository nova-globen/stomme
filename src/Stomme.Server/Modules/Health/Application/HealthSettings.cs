#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The thresholds health runs by, read once per scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are installation-wide, stored as Host-scope <c>Setting</c> rows.</b> They are not a
    /// <c>ConfigSettingsType</c> and not per-site: "how slow is too slow for the database" is a property of the
    /// deployment, and making it per-site would mean the same shared store is Degraded on one site and Healthy on
    /// the next, which is a contradiction an operator cannot act on.
    /// </para>
    /// <para>
    /// Every value is parsed defensively and falls back rather than throwing. A Setting row is free text — the
    /// admin page uses a number input, but the settings API and the database do not — and a monitoring component
    /// that dies on a typo in its own configuration is worse than useless. The same lesson is applied
    /// here before it can bite rather than after.
    /// </para>
    /// </remarks>
    public sealed class HealthSettings
    {
        public const string DegradedLatencyKey = "HealthDegradedLatencyMs";
        public const string ProbeTimeoutKey = "HealthProbeTimeoutMs";
        public const string UnhealthyAlertAfterKey = "HealthUnhealthyAlertAfter";
        public const string DegradedAlertAfterKey = "HealthDegradedAlertAfter";
        public const string CooldownKey = "HealthAlertCooldownMinutes";
        public const string RetentionKey = "HealthRetention";

        public const int DefaultDegradedLatencyMs = 1000;
        public const int DefaultProbeTimeoutMs = 5000;
        public const int DefaultUnhealthyAlertAfterMinutes = 2;
        public const int DefaultDegradedAlertAfterMinutes = 5;
        public const int DefaultCooldownMinutes = 30;
        public const int DefaultRetentionDays = 30;

        /// <summary>Health rollups older than a year are not evidence, they are disk. Below a day they are useless.</summary>
        public const int MinimumRetentionDays = 1;
        public const int MaximumRetentionDays = 365;

        private HealthSettings(
            TimeSpan degradedLatency,
            TimeSpan probeTimeout,
            TimeSpan unhealthyAlertAfter,
            TimeSpan degradedAlertAfter,
            TimeSpan cooldown)
        {
            DegradedLatency = degradedLatency;
            ProbeTimeout = probeTimeout;
            Thresholds = new HealthAlertThresholds(unhealthyAlertAfter, degradedAlertAfter, cooldown);
        }

        public TimeSpan DegradedLatency { get; }

        public TimeSpan ProbeTimeout { get; }

        public HealthAlertThresholds Thresholds { get; }

        /// <summary>The defaults, for a caller with no store to read (the readiness endpoint before install).</summary>
        public static HealthSettings Defaults { get; } = new(
            TimeSpan.FromMilliseconds(DefaultDegradedLatencyMs),
            TimeSpan.FromMilliseconds(DefaultProbeTimeoutMs),
            TimeSpan.FromMinutes(DefaultUnhealthyAlertAfterMinutes),
            TimeSpan.FromMinutes(DefaultDegradedAlertAfterMinutes),
            TimeSpan.FromMinutes(DefaultCooldownMinutes));

        /// <summary>Reads the Host-scope settings, falling back to the defaults for anything unreadable.</summary>
        public static async Task<HealthSettings> LoadAsync(ISettingRepository settings)
        {
            IEnumerable<Setting> rows;
            try
            {
                rows = await settings.GetSettingsAsync(EntityNames.Host, -1);
            }
            catch
            {
                // The settings store is part of what health is meant to WATCH, so being unable to read it is a
                // reason to use defaults, never a reason to stop monitoring.
                return Defaults;
            }

            return new HealthSettings(
                TimeSpan.FromMilliseconds(Positive(settings, rows, DegradedLatencyKey, DefaultDegradedLatencyMs)),
                TimeSpan.FromMilliseconds(Positive(settings, rows, ProbeTimeoutKey, DefaultProbeTimeoutMs)),
                TimeSpan.FromMinutes(Positive(settings, rows, UnhealthyAlertAfterKey, DefaultUnhealthyAlertAfterMinutes)),
                TimeSpan.FromMinutes(Positive(settings, rows, DegradedAlertAfterKey, DefaultDegradedAlertAfterMinutes)),
                TimeSpan.FromMinutes(Positive(settings, rows, CooldownKey, DefaultCooldownMinutes)));
        }

        /// <summary>
        /// The retention for health rollups, clamped to <see cref="MinimumRetentionDays"/>..<see cref="MaximumRetentionDays"/>.
        /// </summary>
        /// <remarks>
        /// The clamp lives here rather than in <c>PurgeJob</c> because a bound is a statement about the DATA, and
        /// only this module knows that a health rollup is worthless past a year and that zero days would delete the
        /// history the admin page exists to show. Zero is meaningful for the framework's own passes ("purge
        /// everything older than now") and is exactly wrong here.
        /// </remarks>
        public static int ClampRetention(int days) =>
            days < MinimumRetentionDays ? MinimumRetentionDays
            : days > MaximumRetentionDays ? MaximumRetentionDays
            : days;

        private static int Positive(ISettingRepository settings, IEnumerable<Setting> rows, string key, int fallback)
        {
            var raw = settings.GetSettingValue(rows, key, fallback.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : fallback;
        }

        /// <summary>
        /// Whether the health PROBE JOB runs at all (config, not a Setting row).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This gates the JOB only.</b> Whether the module's admin page is SEEDED is the framework's own
        /// <c>module.health.enabled</c> knob (<c>ModuleFeatureFlags</c>), which is a different switch
        /// entirely. Conflating the two is a mistake this repository has already made once and corrected, in
        /// metering's equivalent remarks.
        /// </para>
        /// <para>
        /// <b>ON by default.</b> Probing, the
        /// endpoints and the admin page are on for a stock install — stated, not hidden, because a stock install
        /// then gains two Job rows and per-minute rows in the module store. An operator can still set
        /// <c>Health:Enabled = false</c>, and the page then says probing is off. Alerting
        /// (<c>Health:Alerting</c>) ships OFF.
        /// </para>
        /// </remarks>
        public static bool ProbingEnabled(IConfiguration? configuration) =>
            !bool.TryParse(configuration?["Health:Enabled"], out var enabled) || enabled;

        /// <summary>The config key that spaces out the probe job's mail-server connects.</summary>
        public const string MailServerProbeIntervalKey = "Health:MailServerProbeIntervalMinutes";

        /// <summary>
        /// How often, in minutes, the probe JOB connects to each site's mail server (config, not a Setting row).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every connect is a connection to the relay, and a relay may count them: a host with three sites made 180 an
        /// hour before sending any mail, and a relay capped at 120 an hour locked that host out of all mail. The store
        /// and cache probes cost nobody anything, so this spaces out the mail server alone, and an operator no longer
        /// has to turn all of health off (<c>Health:Enabled</c>) to spare the relay.
        /// </para>
        /// <para>
        /// Absent, unreadable or negative is 1, every tick, as before. 0 means the job never probes the mail server.
        /// An operator's Run now always probes it: that is one connect per click, and it is what the operator asked for.
        /// </para>
        /// </remarks>
        public static int MailServerProbeIntervalMinutes(IConfiguration? configuration) =>
            int.TryParse(configuration?[MailServerProbeIntervalKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
                ? minutes
                : 1;
    }

    /// <summary>
    /// When the probe job last connected to each site's mail server, so <see cref="HealthSettings.MailServerProbeIntervalMinutes"/>
    /// can space the connects out.
    /// </summary>
    /// <remarks>
    /// A singleton held in memory: after a restart the first tick probes every mail server once, which is the right answer
    /// for a host that has just come up. Ticks drift by a few seconds, so a probe is due half a minute early rather than a
    /// whole interval late.
    /// </remarks>
    public sealed class HealthMailServerSchedule
    {
        private static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<(Guid Tenant, int Site), DateTime> _last = new();

        /// <summary>Whether the job probes this site's mail server on this tick; a yes is recorded as the probe's time.</summary>
        public bool TakeIfDue(Guid tenant, int site, DateTime now, int intervalMinutes)
        {
            if (intervalMinutes == 0)
            {
                return false;
            }

            var key = (tenant, site);
            if (intervalMinutes > 1 && _last.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(intervalMinutes) - Slack)
            {
                return false;
            }

            _last[key] = now;
            return true;
        }
    }
}
