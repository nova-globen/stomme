#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// The metering module's per-site settings: plain <see cref="Setting"/> rows on <see cref="EntityNames.Site"/>,
    /// read through <see cref="ISettingRepository"/> - the module's own keys, never the framework's
    /// <c>SettingKeys.cs</c>.
    /// </summary>
    public static class MeteringSiteSettingNames
    {
        /// <summary>Whole days a ledger row is kept. Default 90; values below the FLOOR are raised to it.</summary>
        public const string UsageRetention = "MeteringUsageRetention";

        /// <summary>Whole days an AI rollup row is kept. Default 730.</summary>
        public const string UsageRollupRetention = "MeteringUsageRollupRetention";

        /// <summary>The UTC instant ("O" format) the rollup job last completed a pass for this site.</summary>
        public const string LastRollupPass = "MeteringLastRollupPassOn";
    }

    /// <summary>What the rollup job reads and writes per site. A port, so the pass is testable without the framework store.</summary>
    public interface IMeteringSiteSettings
    {
        Task<int> GetUsageRetentionDaysAsync(int siteId, CancellationToken ct = default);

        Task<int> GetRollupRetentionDaysAsync(int siteId, CancellationToken ct = default);

        Task<DateTime?> GetLastRollupPassAsync(int siteId, CancellationToken ct = default);

        Task SetLastRollupPassAsync(int siteId, DateTime passedOnUtc, CancellationToken ct = default);
    }

    /// <summary>
    /// The production <see cref="IMeteringSiteSettings"/>.
    /// </summary>
    /// <remarks>
    /// <b>The retention FLOOR is load-bearing, not a nicety.</b> The repair pass recomputes a Daily or Monthly
    /// counter from the ledger, and it may only do so for a window the retained ledger covers COMPLETELY. Sixty-two
    /// days is the smallest retention that always leaves both the live monthly window and the previous one intact
    /// (31 + 31), so an operator who sets retention to a week cannot turn the repair pass into a silent
    /// under-count. A value below the floor is RAISED to it rather than refused: refusing would leave the site with
    /// no retention at all.
    /// </remarks>
    public sealed class SettingRepositoryMeteringSiteSettings : IMeteringSiteSettings
    {
        /// <summary>Days a ledger row is kept when the site says nothing.</summary>
        public const int DefaultUsageRetentionDays = 90;

        /// <summary>The smallest retention the repair pass can be trusted under - see the type remarks.</summary>
        public const int MinimumUsageRetentionDays = 62;

        /// <summary>Days an AI rollup row is kept when the site says nothing (two years of dashboard history).</summary>
        public const int DefaultRollupRetentionDays = 730;

        private readonly ISettingRepository _settings;

        public SettingRepositoryMeteringSiteSettings(ISettingRepository settings) =>
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        public async Task<int> GetUsageRetentionDaysAsync(int siteId, CancellationToken ct = default)
        {
            var days = await ReadDaysAsync(siteId, MeteringSiteSettingNames.UsageRetention, DefaultUsageRetentionDays);
            return Math.Max(days, MinimumUsageRetentionDays);
        }

        public Task<int> GetRollupRetentionDaysAsync(int siteId, CancellationToken ct = default) =>
            ReadDaysAsync(siteId, MeteringSiteSettingNames.UsageRollupRetention, DefaultRollupRetentionDays);

        public async Task<DateTime?> GetLastRollupPassAsync(int siteId, CancellationToken ct = default)
        {
            var raw = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, MeteringSiteSettingNames.LastRollupPass, string.Empty);
            // RoundtripKind ALONE: combined with AdjustToUniversal this throws ArgumentException - the defect the
            // BL-78 dispatch pass hit on every site before it was measured.
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                ? DateTime.SpecifyKind(last.Kind == DateTimeKind.Local ? last.ToUniversalTime() : last, DateTimeKind.Utc)
                : null;
        }

        public async Task SetLastRollupPassAsync(int siteId, DateTime passedOnUtc, CancellationToken ct = default)
        {
            var value = DateTime.SpecifyKind(passedOnUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
            var existing = await _settings.GetSettingAsync(EntityNames.Site, siteId, MeteringSiteSettingNames.LastRollupPass);
            if (existing is null)
            {
                await _settings.AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = MeteringSiteSettingNames.LastRollupPass,
                    SettingValue = value,
                    IsPrivate = true,
                });
                return;
            }

            existing.SettingValue = value;
            await _settings.UpdateSettingAsync(existing);
        }

        private async Task<int> ReadDaysAsync(int siteId, string name, int fallback)
        {
            var raw = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, name, string.Empty);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : fallback;
        }
    }
}
