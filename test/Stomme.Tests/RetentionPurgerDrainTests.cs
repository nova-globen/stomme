using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <see cref="PurgeJob"/> drains contributed <see cref="IRetentionPurger"/> passes per
    /// site, after its own five.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam exists so that a module with rows to age out does not need a second <c>HostedServiceBase</c> job -
    /// a second Job row, a second schedule, and a second switch an operator can turn off without knowing what they
    /// turned off. What has to be true for that to be a safe trade is exactly what is asserted here: the pass runs
    /// for EVERY site with THAT site's retention, an unreadable setting falls back the way the framework's own
    /// passes fall back rather than however the module felt like, one module's failure costs neither the other
    /// modules nor the remaining sites, and a stock install's job notes are exactly the framework's passes plus
    /// the contributed passes that actually ship.
    /// </para>
    /// <para>
    /// <b>The stock-notes case is a vacuity guard, not a formality</b> — every other assertion here would still
    /// pass if the drain ran unconditionally and did nothing useful. <b>It changed meaning on 2026-09-20 and the
    /// change is recorded rather than smoothed over:</b> it used to assert the framework's six labels ALONE, under
    /// the name "an install with no contributing module". That stopped being reachable the moment the health
    /// module shipped a purger, because the module is always present in-tree. So it now pins the exact label SET
    /// including the shipped contributors, and still fails on an unexpected one — which is the property that
    /// matters: a module that starts contributing a pass must come here and say so, in the same breath as
    /// admitting that a stock install's job notes changed.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class RetentionPurgerDrainTests
    {
        private const string HostA = "localhost";
        private const string HostB = "second.localhost";

        private readonly ITestOutputHelper _output;

        public RetentionPurgerDrainTests(ITestOutputHelper output) => _output = output;

        /// <summary>A purger that records what it was asked, and answers with a count derived from the site.</summary>
        private sealed class RecordingPurger : IRetentionPurger
        {
            private readonly List<(int SiteId, int Days)> _calls = new();

            public RecordingPurger(string settingKey, int defaultDays, string displayName)
            {
                SettingKey = settingKey;
                DefaultDays = defaultDays;
                DisplayName = displayName;
            }

            public string SettingKey { get; }
            public int DefaultDays { get; }
            public string DisplayName { get; }

            public IReadOnlyList<(int SiteId, int Days)> Calls
            {
                get { lock (_calls) { return _calls.ToList(); } }
            }

            public Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
            {
                lock (_calls) { _calls.Add((siteId, days)); }
                return Task.FromResult(days);
            }
        }

        /// <summary>A purger that always throws - the containment case.</summary>
        private sealed class ThrowingPurger : IRetentionPurger
        {
            public string SettingKey => "ThrowingRetention";
            public int DefaultDays => 30;
            public string DisplayName => "Exploding Rows";

            public Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("the module's purge blew up");
        }

        [Fact]
        public async Task Every_site_is_purged_with_its_own_retention_and_an_unreadable_one_falls_back()
        {
            var purger = new RecordingPurger("TestRetention", defaultDays: 45, displayName: "Test Rows");

            using var scratch = new ScratchGateInstall(new SqliteGateStore(), HostA);
            var app = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.AddSingleton<IRetentionPurger>(purger), start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteA = (await GateDrive.AliasAsync(app, HostA)).SiteId;
                var siteB = (await CreateSecondSiteAsync(app)).SiteId;
                _output.WriteLine($"[sites] A={siteA} B={siteB}");

                // Site A says 7 days. Site B says "soon", which is not a number - the framework's own answer to
                // that must be the answer the contributed pass gets too.
                await SetSiteSettingAsync(app, siteA, "TestRetention", "7");
                await SetSiteSettingAsync(app, siteB, "TestRetention", "soon");

                var notes = await RunPurgeJobAsync(app);
                _output.WriteLine($"[notes] {notes}");

                var calls = purger.Calls;
                _output.WriteLine("[calls] " + string.Join(", ", calls.Select(call => $"site {call.SiteId} => {call.Days}d")));

                Assert.Equal(2, calls.Count);
                Assert.Equal(7, calls.Single(call => call.SiteId == siteA).Days);
                Assert.Equal(45, calls.Single(call => call.SiteId == siteB).Days);

                // The count came back through the notes in the framework's own shape, and the fallback said so.
                Assert.Contains("7 Test Rows Purged", notes);
                Assert.Contains("45 Test Rows Purged", notes);
                Assert.Contains("The TestRetention setting is not a whole number of days", notes);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_throwing_purger_costs_neither_the_other_purgers_nor_the_later_sites()
        {
            var survivor = new RecordingPurger("SurvivorRetention", defaultDays: 30, displayName: "Survivor Rows");

            using var scratch = new ScratchGateInstall(new SqliteGateStore(), HostA);
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                // Registration ORDER is the point: the exploding one runs first.
                builder.Services.AddSingleton<IRetentionPurger>(new ThrowingPurger());
                builder.Services.AddSingleton<IRetentionPurger>(survivor);
            }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteA = (await GateDrive.AliasAsync(app, HostA)).SiteId;
                var siteB = (await CreateSecondSiteAsync(app)).SiteId;

                var notes = await RunPurgeJobAsync(app);
                _output.WriteLine($"[notes] {notes}");

                var sites = purgedSites(survivor);
                _output.WriteLine("[survivor saw sites] " + string.Join(", ", sites));

                // Both sites still got the second purger, so the throw was contained per PURGER - not per site,
                // and not per run.
                Assert.Contains(siteA, sites);
                Assert.Contains(siteB, sites);
                Assert.Equal(2, sites.Count);

                // The failure is reported by NAME rather than swallowed, once per site.
                Assert.Contains("Error Purging Exploding Rows", notes);
                Assert.Equal(2, CountOccurrences(notes, "Error Purging Exploding Rows"));

                static List<int> purgedSites(RecordingPurger recorder) =>
                    recorder.Calls.Select(call => call.SiteId).Distinct().ToList();
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_stock_installs_purge_notes_are_the_framework_passes_plus_exactly_the_shipped_contributors()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), HostA);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var notes = await RunPurgeJobAsync(app);
                _output.WriteLine($"[notes] {notes}");

                // The framework's own passes are all still there, and NOTHING else is. Asserting the exact SET of
                // purge labels rather than a count is deliberate: a count that drifts tells you a number changed,
                // while this tells you WHICH line appeared or vanished - and a contributed pass is exactly a new
                // label. It is also what caught the first version of this test, which claimed five: a stock
                // install emits SIX, because the default site has VisitorTracking on and the visitor-tracking
                // feature flag is enabled, so the optional visitor pass does run.
                var labels = PurgeLabels(notes);
                _output.WriteLine("[purge labels] " + string.Join(" | ", labels));

                // The framework's own six, PLUS exactly the contributed passes that ship in-tree.
                //
                // CHANGED 2026-09-20, and the change is the point: this used to assert the six alone, under the
                // name "an install with no contributing module". That stopped being achievable the moment the
                // health module shipped a purger - it is always present - so the assertion was updated to the new
                // truth rather than relaxed. It still fails on an UNEXPECTED label, which is what it is for: a
                // module that starts contributing a pass has to come here and say so, in the same breath as
                // admitting a stock install's job notes changed.
                Assert.Equal(
                    new[]
                    {
                        "Broken Urls", "Completed Tasks", "Events",
                        "Health Rollups",   // The first contributed pass
                        "Identity Audit",   // The identity ledger, retention floor 30 days
                        "Notifications", "Orphaned Upload Parts",
                        "Portability",      // Retires package artifacts and old audit rows; 0 while disabled
                        "Sessions",         // Ended sessions, 30 days after they ended
                        "Visitors"
                    },
                    labels);
                Assert.DoesNotContain("Error Purging", notes);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        /// <summary>
        /// Runs the job body the way <c>HostedServiceBase</c> runs it for one tenant: a fresh DI scope with the
        /// tenant set. The scheduling half is not under test here and has its own cover.
        /// </summary>
        private static async Task<string> RunPurgeJobAsync(WebApplication app)
        {
            var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
            var job = new PurgeJob(scopes);

            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(HostA);
            var tenant = (await sp.GetRequiredService<ITenantRepository>().GetTenantsAsync())
                .Single(item => item.TenantId == alias.TenantId);
            sp.GetRequiredService<ITenantManager>().SetTenant(tenant);

            return await job.ExecuteJobAsync(sp);
        }

        private static Task SetSiteSettingAsync(WebApplication app, int siteId, string name, string value) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, _) =>
            {
                await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = name,
                    SettingValue = value,
                    IsPrivate = false
                });
                return true;
            });

        /// <summary>
        /// The sorted, distinct labels of every "&lt;n&gt; &lt;label&gt; Purged" line in the notes - the framework's
        /// own passes and any contributed one, in the one shape they all share.
        /// </summary>
        private static string[] PurgeLabels(string notes) =>
            System.Text.RegularExpressions.Regex.Matches(notes, @"\d+ (?<label>[^<]+?) Purged<br />")
                .Select(match => match.Groups["label"].Value.Trim())
                .Distinct()
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var at = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                count++;
                at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
            }

            return count;
        }

        /// <summary>A second site on the SAME tenant, through the call the Host's "Add Site" page makes.</summary>
        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Retention Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = false
                });

                return site;
            });
    }
}
