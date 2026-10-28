using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Health;
using Stomme.Models;
using Stomme.Models.Health;
using Stomme.Modules.Health.Services;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HealthIndex = Stomme.Modules.Health.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// The Health admin control, RENDERED and CLICKED (BL-83). Until 2026-09-23 it had never rendered anywhere observable,
    /// so the BL-54 dead-click class was not excluded for it; a browser drive has now clicked it, and this pins what it
    /// showed.
    /// </summary>
    /// <remarks>
    /// <b>Two empty states, and they must not be confused</b> (found by that drive): with probing switched OFF
    /// (<c>Health:Enabled = false</c>, which the shipped configuration carried until BL-83 closed and an operator can still
    /// set) the page used to promise that "the health job runs every minute" - a tick that would never come.
    /// </remarks>
    public sealed class HealthControlTests : BunitContext
    {
        private readonly RecordingHealthService _service = new();

        public HealthControlTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddSingleton<IHealthService>(_service);

            // The Host-only "Thresholds" ActionLink renders from the header: it needs a localizer and IUserService.
            Services.AddLocalization();
            Services.AddSingleton(HelpControlClickTests.Inert<IUserService>.Create());
        }

        [Fact]
        public void With_probing_OFF_the_page_says_so_and_does_NOT_promise_a_tick()
        {
            _service.Next = new HealthDashboard(HealthState.Healthy, null, [], [], ProbingEnabled: false);
            var cut = Render();

            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#health-probing-off")));
            Assert.DoesNotContain("runs every minute", cut.Markup, StringComparison.Ordinal);
        }

        [Fact]
        public void With_probing_ON_and_nothing_yet_the_page_says_the_first_tick_is_coming()
        {
            _service.Next = new HealthDashboard(HealthState.Healthy, null, [], [], ProbingEnabled: true);
            var cut = Render();

            cut.WaitForAssertion(() => Assert.Contains("runs every minute", cut.Markup, StringComparison.Ordinal));
            Assert.Empty(cut.FindAll("#health-probing-off"));
            Assert.Equal("Unknown", cut.Find(".db-health-worst").TextContent);     // no verdict without a check
        }

        [Fact]
        public void The_subjects_render_and_REFRESH_is_a_bound_click_that_re_reads()
        {
            var tick = new DateTime(2026, 9, 23, 2, 10, 9, DateTimeKind.Utc);
            _service.Next = new HealthDashboard(HealthState.Healthy, tick,
                [new HealthSubjectView("stomme:store", "store", HealthProbeScope.Tenant, HealthState.Healthy, null, 0d, null, null, null, 0)], []);
            var cut = Render();

            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-health-table tbody tr")));
            var reads = _service.Reads;

            // What the browser drive measured: a Refresh click re-reads, and the page shows the newer tick.
            _service.Next = _service.Next with { LastTickUtc = tick.AddMinutes(1) };
            cut.Find("#health-refresh").Click();

            cut.WaitForAssertion(() => Assert.Equal(reads + 1, _service.Reads));
            cut.WaitForAssertion(() => Assert.Contains("02:11:09", cut.Markup, StringComparison.Ordinal));
        }

        [Fact]
        public void The_sparkline_column_draws_a_subjects_hourly_availability_and_nothing_under_two_periods()
        {
            // BL-96 increment 10 (re-booked from D104): a "Last hours" column, one inline SVG per subject with two or more
            // hourly rollups; a subject with fewer draws nothing and the column stays.
            var tick = new DateTime(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc);
            _service.Next = new HealthDashboard(HealthState.Healthy, tick,
                [
                    new HealthSubjectView("stomme:store", "store", HealthProbeScope.Tenant, HealthState.Healthy, null, 0d, null, null, null, 0),
                    new HealthSubjectView("stomme:cache", "cache", HealthProbeScope.Installation, HealthState.Healthy, null, 0d, null, null, null, 0),
                ], []);
            _service.Rollups =
            [
                new HealthRollupView("stomme:store", HealthProbeScope.Tenant, "Hour", tick.AddHours(-2), 60, 60, 0, 0, 1, 2, 100),
                new HealthRollupView("stomme:store", HealthProbeScope.Tenant, "Hour", tick.AddHours(-1), 60, 30, 0, 30, 1, 2, 50),
                new HealthRollupView("stomme:cache", HealthProbeScope.Installation, "Hour", tick.AddHours(-1), 60, 60, 0, 0, 1, 2, 100),
            ];
            var cut = Render();

            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-health-table tbody tr").Count));
            Assert.Contains("Last hours", cut.Markup, StringComparison.Ordinal);
            var sparklines = cut.FindAll("svg.db-sparkline");
            var svg = Assert.Single(sparklines);
            Assert.Equal("stomme:store availability", svg.GetAttribute("aria-label"));
            Assert.Equal(HealthSparkline.Points(_service.Rollups, "stomme:store"), svg.QuerySelector("polyline")!.GetAttribute("points"));
            Assert.Equal("1,1 95,10", svg.QuerySelector("polyline")!.GetAttribute("points"));
        }

        [Fact]
        public void RUN_PROBES_NOW_is_a_bound_click_that_runs_then_re_reads_and_says_what_ran()
        {
            var cut = Render();
            cut.WaitForAssertion(() => Assert.Equal(1, _service.Reads));

            cut.Find("#health-run-now").Click();

            cut.WaitForAssertion(() => Assert.Equal(1, _service.Runs));
            cut.WaitForAssertion(() => Assert.Equal(2, _service.Reads));              // the page re-reads what the run wrote
            Assert.Equal("Ran 2 probe(s): Healthy.", cut.Find("#health-run-message").TextContent);
        }

        [Fact]
        public void A_run_already_in_flight_is_SAID_not_swallowed()
        {
            _service.RunResult = null;                                               // the server's 409
            var cut = Render();

            cut.Find("#health-run-now").Click();
            cut.WaitForAssertion(() => Assert.Contains("already running", cut.Find("#health-run-message").TextContent, StringComparison.Ordinal));
        }

        [Fact]
        public void A_VIEWER_without_health_manage_is_not_offered_the_button()
        {
            var cut = Render(canManage: false);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#health-refresh")));
            Assert.Empty(cut.FindAll("#health-run-now"));
        }

        private IRenderedComponent<HealthIndex> Render(bool canManage = true) =>
            Render<HealthIndex>(parameters => parameters
                .AddCascadingValue(new PageState
                {
                    Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
                    Site = new Site { SiteId = 1, Name = "Site" },
                    Page = new Page { PageId = 5, SiteId = 1, Path = "admin/health", Name = "Health" },
                    User = new User { UserId = 1, Username = "host", Roles = ";" + RoleNames.Admin + ";" },
                    QueryString = new Dictionary<string, string>(),
                    UrlParameters = "",
                })
                .AddCascadingValue(new ModuleModel
                {
                    ModuleId = 21,
                    SiteId = 1,
                    PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(HealthPermissions.Declarations))
                        .Where(key => canManage || key.Operation != "manage")
                        .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                        .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Admin, true) })
                        .ToList(),
                }));

        private sealed class RecordingHealthService : IHealthService
        {
            public HealthDashboard Next = new(HealthState.Healthy, null, [], []);
            public int Reads;

            public Task<HealthDashboard> GetDashboardAsync()
            {
                Reads++;
                return Task.FromResult(Next);
            }

            public List<HealthRollupView> Rollups { get; set; } = new();

            public Task<List<HealthRollupView>> GetRollupsAsync(string window = "Hour", int take = 200) => Task.FromResult(Rollups);

            public Task AcknowledgeAsync(Guid alertId) => Task.CompletedTask;

            public HealthRunNowResult RunResult = new(2, HealthState.Healthy);
            public int Runs;

            public Task<HealthRunNowResult> RunNowAsync()
            {
                Runs++;
                return Task.FromResult(RunResult);
            }
        }
    }
}
