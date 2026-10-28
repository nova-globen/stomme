#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// The export module's per-site settings: plain <c>Setting</c> rows on <see cref="EntityNames.Site"/> - the
    /// module's own keys, never the framework's <c>SettingKeys.cs</c> (the Metering convention).
    /// </summary>
    public static class ExportSiteSettingNames
    {
        /// <summary>Whole days an artifact is downloadable. Absent or unusable: <c>DataExport:RetentionDays</c>.</summary>
        public const string Retention = "ExportRetention";

        /// <summary>Whole days an audit entry is kept. Absent or unusable: <c>DataExport:AuditRetentionDays</c>.</summary>
        public const string AuditRetention = "ExportAuditRetention";
    }

    /// <summary>How long THIS site keeps an artifact and its trail. A port, so the passes are drivable without the framework store.</summary>
    public interface IExportSiteSettings
    {
        Task<TimeSpan> GetRetentionAsync(int siteId, CancellationToken ct = default);

        Task<TimeSpan> GetAuditRetentionAsync(int siteId, CancellationToken ct = default);
    }

    /// <summary>
    /// The production <see cref="IExportSiteSettings"/>: the site's row when it holds a usable value, the
    /// configuration otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A site can shorten or lengthen its retention, within a ceiling.</b> Artifacts sit on the host's disk, so a
    /// site administrator must not be able to pin them there for ever: a value above
    /// <see cref="MaximumRetentionDays"/> is LOWERED to it rather than refused, and anything that is not a positive
    /// whole number is ignored in favour of the configuration - a typo must not turn into "keep nothing" or "keep
    /// everything".
    /// </para>
    /// <para>
    /// <b>The trail outlives the artifact, per site too.</b> The audit retention is never shorter than the SAME
    /// site's artifact retention, whichever of the two the site overrode.
    /// </para>
    /// <para>
    /// The setting repository is optional: a composition of the module alone (the lease suites) has no framework
    /// store, and the configuration is then the whole answer.
    /// </para>
    /// </remarks>
    public sealed class SettingRepositoryExportSiteSettings : IExportSiteSettings
    {
        /// <summary>The longest a site may keep an artifact or its trail, whatever its setting says.</summary>
        public const int MaximumRetentionDays = 3650;

        private readonly DataExportOptions _options;
        private readonly ISettingRepository? _settings;

        public SettingRepositoryExportSiteSettings(DataExportOptions options, ISettingRepository? settings = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _settings = settings;
        }

        public async Task<TimeSpan> GetRetentionAsync(int siteId, CancellationToken ct = default) =>
            TimeSpan.FromDays(await ReadDaysAsync(siteId, ExportSiteSettingNames.Retention, (int)_options.Retention.TotalDays));

        public async Task<TimeSpan> GetAuditRetentionAsync(int siteId, CancellationToken ct = default)
        {
            var artifact = await ReadDaysAsync(siteId, ExportSiteSettingNames.Retention, (int)_options.Retention.TotalDays);
            var audit = await ReadDaysAsync(siteId, ExportSiteSettingNames.AuditRetention, (int)_options.AuditRetention.TotalDays);
            return TimeSpan.FromDays(Math.Max(artifact, audit));
        }

        private async Task<int> ReadDaysAsync(int siteId, string name, int fallback)
        {
            if (_settings is null)
            {
                return fallback;
            }

            var raw = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, name, string.Empty);
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0
                ? Math.Min(days, MaximumRetentionDays)
                : fallback;
        }
    }
}
