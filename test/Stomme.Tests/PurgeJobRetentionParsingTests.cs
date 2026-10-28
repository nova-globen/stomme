using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-113: one unreadable retention setting must not stop the purge for every LATER site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PurgeJob</c> read its four retention settings with <c>int.Parse</c> OUTSIDE the try blocks meant to
    /// contain a failing purge, and its per-site loop has no catch of its own. A value that is not a whole number
    /// therefore threw out of <c>ExecuteJobAsync</c> into <c>HostedServiceBase</c>'s single catch: every later
    /// site - and every later tenant - was skipped, and the job notes were replaced by the exception message, so
    /// the sites already purged lost their record too.
    /// </para>
    /// <para>
    /// The admin pages use a number input, so a bad value arrives through the Host settings import, the Setting
    /// API or the database. Driven here on a real installed site by writing the setting the way those paths do.
    /// Both directions: with a good value the purge still reports its work, and the note names the SETTING but
    /// never its value (BL-112, these rows are private).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PurgeJobRetentionParsingTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public PurgeJobRetentionParsingTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("abc", "not a number")]
        [InlineData("", "empty")]
        [InlineData("-5", "negative - would purge rows dated in the future")]
        [InlineData("2147483648", "larger than int")]
        public async Task An_unreadable_retention_setting_does_not_stop_the_purge(string badValue, string why)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    await settings.AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Site,
                        EntityId = alias.SiteId,
                        SettingName = "LogRetention",
                        SettingValue = badValue,
                        IsPrivate = false
                    });
                    return true;
                });

                var notes = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var job = new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>());
                    return await job.ExecuteJobAsync(sp);
                });

                _output.WriteLine($"[{why}] notes: {notes}");

                // The purge RAN: the later steps of the same site report their work, which is exactly what the
                // throw used to prevent.
                Assert.Contains("Notifications Purged", notes);
                Assert.Contains("Broken Urls Purged", notes);

                // It said which setting it could not read, and did NOT echo the value (BL-112).
                Assert.Contains("LogRetention", notes);
                if (!string.IsNullOrEmpty(badValue))
                {
                    Assert.DoesNotContain(badValue, notes);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Non-vacuity: a readable value purges without the fallback note.</summary>
        [Fact]
        public async Task A_readable_retention_setting_purges_without_a_note()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    await settings.AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Site,
                        EntityId = alias.SiteId,
                        SettingName = "LogRetention",
                        SettingValue = "7",
                        IsPrivate = false
                    });
                    return true;
                });

                var notes = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var job = new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>());
                    return await job.ExecuteJobAsync(sp);
                });

                _output.WriteLine($"[good value] notes: {notes}");
                Assert.Contains("Events Purged", notes);
                Assert.DoesNotContain("is not a whole number of days", notes);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
