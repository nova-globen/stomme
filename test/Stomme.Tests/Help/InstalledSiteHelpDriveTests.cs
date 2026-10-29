#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-84's D30 bar: contextual help on a REAL installed site, on PostgreSQL AND RavenDB - installed, booted twice,
    /// the admin page rendering, the framework topics reconciled once, a Static page view with help and no circuit, an
    /// authored topic that saves and re-renders, and the locale chain falling back.
    /// </summary>
    /// <remarks>
    /// SQLite cannot reproduce the renderer-deadlock class (its async surface completes synchronously - D29/D30), so the
    /// render half of this is run where it can fail. Both providers FAIL rather than skip.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class InstalledSiteHelpDriveTests
    {
        private const string Host = HelpDrive.Host;
        private const string Api = "/api/Help";

        private static readonly Regex InteractiveMarker = new("<!--Blazor:", RegexOptions.CultureInvariant);

        private readonly ITestOutputHelper _output;

        public InstalledSiteHelpDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Help_installs_reconciles_once_renders_and_round_trips_an_authored_topic(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var site = await HelpDrive.SiteAsync(app, Host);

                // ---- boot twice: every contributed topic exactly once ------------------------------------------------
                int contributed;
                using (var scope = app.Services.CreateScope())
                {
                    contributed = scope.ServiceProvider.GetRequiredService<HelpTopicContributorRegistry>().Topics.Count;
                }

                var first = await HelpDrive.TopicsAsync(app, site);
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                var second = await HelpDrive.TopicsAsync(app, site);
                _output.WriteLine($"[{storeName}] {contributed} contributed; after install {first.Count}, after a second boot {second.Count}");
                Assert.Equal(contributed, first.Count);
                Assert.Equal(first.Select(topic => topic.Id).OrderBy(id => id), second.Select(topic => topic.Id).OrderBy(id => id));
                Assert.All(second, topic => Assert.Equal(1, topic.ContentVersion));

                // ---- the admin page renders -------------------------------------------------------------------------
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var admin = await GateDrive.GetAsync(client, cookies, Host, "/admin/help", _output, $"[{storeName}] host: admin/help");
                Assert.Equal(HttpStatusCode.OK, admin.Status);
                Assert.Matches("<title>[^<]*Help[^<]*</title>", admin.Body);
                Assert.DoesNotContain("Installer", admin.Body, StringComparison.Ordinal);

                // ---- an authored topic for the HOME page saves, publishes and renders to an anonymous reader ------------
                var home = (await HelpDrive.InSiteAsync(app, site, async sp =>
                        (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(site.Alias.SiteId))
                        .Where(page => !page.Path.StartsWith("admin", StringComparison.Ordinal) && !page.IsDeleted)
                        .OrderBy(page => page.Order)
                        .First()))
                    .Path;
                Assert.True(HelpKeys.TryPageAnchor(home, out var homeAnchor));

                var saved = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, Api,
                    new { Anchor = homeAnchor, Locale = "en", Title = "Welcome", Markdown = "Start **here**." }, _output, $"[{storeName}] save");
                Assert.Equal(HttpStatusCode.OK, saved.Status);
                var id = JsonSerializer.Deserialize<Guid>(saved.Body);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post,
                    $"{Api}/{id}/publish?published=true", null, _output, $"[{storeName}] publish")).Status);

                var anonymous = GateDrive.NewClient(app, Host);
                var page = await GateDrive.GetAsync(anonymous, null, Host, "/" + home, _output, $"[{storeName}] anonymous: home");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("<details class=\"db-help\"", page.Body, StringComparison.Ordinal);
                Assert.Contains("<strong>here</strong>", page.Body, StringComparison.Ordinal);
                Assert.Empty(InteractiveMarker.Matches(page.Body));

                // ... and an edit re-renders (the cache key carries the version).
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, Api,
                    new { Id = id, Anchor = homeAnchor, Locale = "en", Title = "Welcome", Markdown = "Begin **there**." }, _output, $"[{storeName}] edit")).Status);
                var again = await GateDrive.GetAsync(anonymous, null, Host, "/" + home, _output, $"[{storeName}] anonymous: home after edit");
                Assert.Contains("<strong>there</strong>", again.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("<strong>here</strong>", again.Body, StringComparison.Ordinal);

                // ---- the locale chain: Swedish asked, English answered --------------------------------------------------
                var swedish = await GateDrive.GetAsync(anonymous, null, Host, $"{Api}/topic?anchor=page:admin&locale=sv-SE", _output, $"[{storeName}] sv-SE");
                Assert.Equal(HttpStatusCode.OK, swedish.Status);
                using var parsed = JsonDocument.Parse(swedish.Body);
                Assert.Equal("en", parsed.RootElement.GetProperty("locale").GetString());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
