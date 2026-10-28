#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-84 increment 8 (D103 item 6, refutation 3): the launcher on a REAL installed site, rendered over HTTP - an
    /// anonymous reader of a Static page gets help as a native disclosure and NO interactive island.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refutation 3(b): today's interactive island in the header exists only for admins and editors; putting the launcher
    /// in the ungated slot with the ControlPanel's render-mode bridge would have opened an Interactive Server circuit for
    /// EVERY anonymous page view on a Static site. Blazor marks every interactive component it prerenders with a
    /// <c>&lt;!--Blazor:</c> comment, so "no circuit" is measurable as "no marker".
    /// </para>
    /// <para>
    /// <b>The oracle is validated the other way round</b>: the HOST's view of the same page carries the admin control
    /// panel's island, so it must contain a marker - an oracle that could not see one would pass everything.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HelpLauncherPageRenderTests
    {
        private const string Host = HelpDrive.Host;

        private static readonly Regex InteractiveMarker = new("<!--Blazor:", RegexOptions.CultureInvariant);

        private readonly ITestOutputHelper _output;

        public HelpLauncherPageRenderTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task An_ANONYMOUS_static_page_view_shows_help_as_a_disclosure_and_opens_NO_circuit()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var home = await PublishHomeTopicAsync(app, "Welcome help", "Start **here**, then <script>alert(1)</script> nothing.");

                var anonymous = await GateDrive.GetAsync(GateDrive.NewClient(app, Host), null, Host, "/" + home, _output, "anonymous: home");
                Assert.Equal(HttpStatusCode.OK, anonymous.Status);

                Assert.Contains("<details class=\"db-help\"", anonymous.Body, StringComparison.Ordinal);
                Assert.Contains("Welcome help", anonymous.Body, StringComparison.Ordinal);
                Assert.Contains("<strong>here</strong>", anonymous.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("<script>alert(1)</script>", anonymous.Body, StringComparison.Ordinal);   // escaped, never live
                Assert.Empty(InteractiveMarker.Matches(anonymous.Body));                                     // refutation 3(b)

                // The oracle, validated: the host's view of the same page carries the control panel's island.
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var asHost = await GateDrive.GetAsync(client, cookies, Host, "/" + home, _output, "host: home");
                Assert.NotEmpty(InteractiveMarker.Matches(asHost.Body));
                Assert.Contains("<details class=\"db-help\"", asHost.Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_admin_page_shows_the_FRAMEWORK_topic_for_its_control()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // No topic was written by hand: these come from StommeHelpContributor through the boot reconciler, and
                // are found through the anchors the ROUTER produced for this page and its control.
                var users = await GateDrive.GetAsync(client, cookies, Host, "/admin/users", _output, "host: admin/users");
                Assert.Equal(HttpStatusCode.OK, users.Status);
                Assert.Contains("db-help", users.Body, StringComparison.Ordinal);
                Assert.Contains("User Management", users.Body, StringComparison.Ordinal);
                Assert.Contains("data-anchor=\"stomme.modules.admin.users.index\"", users.Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_Help_OFF_no_page_carries_the_launcher()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var anonymous = await GateDrive.GetAsync(GateDrive.NewClient(app, Host), null, Host, "/", _output, "off: anonymous home");
                Assert.Equal(HttpStatusCode.OK, anonymous.Status);
                Assert.DoesNotContain("db-help", anonymous.Body, StringComparison.Ordinal);

                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var users = await GateDrive.GetAsync(client, cookies, Host, "/admin/users", _output, "off: host admin/users");
                Assert.DoesNotContain("db-help", users.Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Publishes a topic for the site's HOME page and answers the page's path ("" for the root).</summary>
        private static async Task<string> PublishHomeTopicAsync(WebApplication app, string title, string markdown)
        {
            var site = await HelpDrive.SiteAsync(app, Host);
            return await HelpDrive.InSiteAsync(app, site, async sp =>
            {
                var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(site.Alias.SiteId);
                var home = pages.Where(page => !page.Path.StartsWith("admin", StringComparison.Ordinal) && !page.IsDeleted)
                    .OrderBy(page => page.Order)
                    .First();
                Assert.True(HelpKeys.TryPageAnchor(home.Path, out var anchor));

                var topic = HelpTopicAggregate.Author(site.TenantId, site.Alias.SiteId, anchor, "en", title, markdown, DateTime.UtcNow);
                topic.Publish(DateTime.UtcNow);
                await sp.GetRequiredService<IHelpTopicRepository>().AddAsync(topic);
                return home.Path;
            });
        }
    }
}
