#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-84 increment 7 (D103 items 8-9, refutations 10 and 15): <c>api/Help</c> DRIVEN over HTTP as an anonymous
    /// visitor, a registered user and the host - what each may do, what the wire can send without producing a 500, and
    /// what a stock install exposes (nothing).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refutation 15 is asserted as behaviour, not as a declaration.</b> The per-key <c>Declare</c> gives
    /// <c>help:view</c> to Everyone and the other two keys to Administrators; the uniform one would have handed
    /// <c>help:manage</c> to All Users, which admits an ANONYMOUS caller. So an anonymous client is driven through every
    /// action: it reads published help and is refused everything else.
    /// </para>
    /// <para>
    /// <b>Revocation is the next call.</b> The anonymous reader is admitted by a ROW (<c>help:view</c> for Everyone), not
    /// by a class-level <c>[AllowAnonymous]</c> - so deleting the row closes the door, which this suite drives.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HelpApiDriveTests
    {
        private const string Host = HelpDrive.Host;
        private const string Api = "/api/Help";

        private readonly ITestOutputHelper _output;

        public HelpApiDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Each_principal_holds_exactly_its_keys_and_an_anonymous_reader_holds_ONLY_help_view()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var anonymous = new Session(GateDrive.NewClient(app, Host), null, null);
                var plain = await GateDrive.AddUserAsync(app, Host, "help-plain", "Pla1n!Help$2026x", "help-plain@gate.test");
                var asPlain = await SessionAsync(app, "help-plain", "Pla1n!Help$2026x", plain.UserId);
                var asHost = await SessionAsync(app, GateDrive.HostUsername, GateDrive.HostPassword, await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername));

                // ---- help:view - everyone, the anonymous reader included --------------------------------------------
                foreach (var who in new[] { anonymous, asPlain, asHost })
                {
                    var topic = await GetAsync(who, $"{Api}/topic?anchor=page:admin&locale=en", "read page:admin");
                    Assert.Equal(HttpStatusCode.OK, topic.Status);
                    using var parsed = JsonDocument.Parse(topic.Body);
                    Assert.Equal("Administration", parsed.RootElement.GetProperty("title").GetString());
                    Assert.Contains("<strong>administration dashboard</strong>", parsed.RootElement.GetProperty("safeHtml").GetString(), StringComparison.Ordinal);
                    Assert.Equal(HttpStatusCode.OK, (await GetAsync(who, $"{Api}/search?q=users", "search")).Status);
                }

                // ---- everything else is REFUSED to the anonymous reader and to a plain registered user ---------------
                var draftId = await CreatedAsync(app, asHost, new { Anchor = "page:drafted", Locale = "en", Title = "Drafted", Markdown = "Not *yet*." }, "host saves a draft");
                foreach (var who in new[] { anonymous, asPlain })
                {
                    var label = who.UserId is null ? "anonymous" : "plain";
                    Assert.True(IsRefusal((await GetAsync(who, Api, $"{label}: list all")).Status));
                    Assert.True(IsRefusal((await GetAsync(who, $"{Api}/conflicts", $"{label}: conflicts")).Status));
                    Assert.True(IsRefusal((await GetAsync(who, $"{Api}/{draftId}", $"{label}: edit view")).Status));
                    Assert.True(IsRefusal((await SendAsync(app, who, HttpMethod.Post, Api, new { Anchor = "page:x", Locale = "en", Title = "X", Markdown = "X" }, $"{label}: save")).Status));
                    Assert.True(IsRefusal((await SendAsync(app, who, HttpMethod.Post, $"{Api}/preview", new { Markdown = "**x**" }, $"{label}: preview")).Status));
                    Assert.True(IsRefusal((await SendAsync(app, who, HttpMethod.Post, $"{Api}/{draftId}/publish?published=true", null, $"{label}: publish")).Status));
                    Assert.True(IsRefusal((await SendAsync(app, who, HttpMethod.Post, $"{Api}/{draftId}/reset", null, $"{label}: reset")).Status));
                    Assert.True(IsRefusal((await SendAsync(app, who, HttpMethod.Delete, $"{Api}/{draftId}", null, $"{label}: delete")).Status));

                    // And the draft stays invisible to them through the one door they DO hold.
                    Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(who, $"{Api}/topic?anchor=page:drafted&locale=en", $"{label}: read the draft")).Status);
                }

                // ---- the host (Administrators) holds all three ---------------------------------------------------------
                var list = await GetAsync(asHost, Api, "host: list all");
                Assert.Equal(HttpStatusCode.OK, list.Status);
                Assert.Contains("page:drafted", list.Body, StringComparison.Ordinal);                  // drafts included

                var edit = await GetAsync(asHost, $"{Api}/{draftId}", "host: edit view");
                Assert.Equal(HttpStatusCode.OK, edit.Status);
                Assert.Contains("Not *yet*.", edit.Body, StringComparison.Ordinal);                    // the MARKDOWN, for the author

                var preview = await SendAsync(app, asHost, HttpMethod.Post, $"{Api}/preview", new { Markdown = "**bold** <script>alert(1)</script>" }, "host: preview");
                Assert.Equal(HttpStatusCode.OK, preview.Status);
                Assert.Contains("<strong>bold</strong>", preview.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("<script>", preview.Body, StringComparison.Ordinal);               // escaped text, never a live element

                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(app, asHost, HttpMethod.Post, $"{Api}/{draftId}/publish?published=true", null, "host: publish")).Status);
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(anonymous, $"{Api}/topic?anchor=page:drafted&locale=en", "anonymous: read the PUBLISHED topic")).Status);

                // An edit of a CONTRIBUTED topic makes it the site's, and reset hands it back.
                var adminId = HelpTopicIds.For(new Guid("00000000-0000-0000-0000-000000000001"), (await GateDrive.AliasAsync(app, Host)).SiteId, "page:admin", "en");
                Assert.Equal(adminId, await CreatedAsync(app, asHost, new { Id = adminId, Anchor = "page:admin", Locale = "en", Title = "Ours", Markdown = "Our text." }, "host: edits a contributed topic"));
                Assert.Contains("\"isSeeded\":false", (await GetAsync(asHost, $"{Api}/{adminId}", "host: after the edit")).Body, StringComparison.Ordinal);
                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(app, asHost, HttpMethod.Post, $"{Api}/{adminId}/reset", null, "host: reset to seed")).Status);
                var reset = await GetAsync(asHost, $"{Api}/{adminId}", "host: after the reset");
                Assert.Contains("\"isSeeded\":true", reset.Body, StringComparison.Ordinal);
                Assert.Contains("administration dashboard", reset.Body, StringComparison.Ordinal);

                // Refused author input is a 400 with a reason; an unknown id is a 404 - never a 500.
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(app, asHost, HttpMethod.Post, Api, new { Anchor = "has space", Locale = "en", Title = "X", Markdown = "X" }, "host: bad anchor")).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(app, asHost, HttpMethod.Post, $"{Api}/preview", new { Markdown = new string('x', HelpTopicContribution.MaximumMarkdownCharacters + 1) }, "host: preview too long")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(asHost, $"{Api}/{Guid.NewGuid()}", "host: unknown id")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, asHost, HttpMethod.Delete, $"{Api}/{Guid.NewGuid()}", null, "host: delete unknown")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, asHost, HttpMethod.Post, $"{Api}/{Guid.NewGuid()}/publish?published=true", null, "host: publish unknown")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(app, asHost, HttpMethod.Post, Api, new { Id = Guid.NewGuid(), Anchor = "page:x", Locale = "en", Title = "X", Markdown = "X" }, "host: save unknown id")).Status);

                // The manage list is ordered by anchor, then locale (the predecessor platform's List_IsSortedByAnchorThenLocale).
                using (var listed = JsonDocument.Parse((await GetAsync(asHost, Api, "host: list order")).Body))
                {
                    var keys = listed.RootElement.EnumerateArray()
                        .Select(item => item.GetProperty("anchor").GetString() + "|" + item.GetProperty("locale").GetString())
                        .ToList();
                    Assert.True(keys.Count > 2);
                    Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
                }

                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(app, asHost, HttpMethod.Delete, $"{Api}/{draftId}", null, "host: delete")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await GetAsync(anonymous, $"{Api}/topic?anchor=page:drafted&locale=en", "anonymous: after delete")).Status);

                // ---- revocation is the NEXT call: the anonymous reader is admitted by a row, not an attribute ---------
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var everyone = (await GateDrive.RowsAsync(app, Host, HelpPermissions.Resource, "view"))
                    .Where(row => row.RoleName == RoleNames.Everyone)
                    .ToList();
                _output.WriteLine($"help:view rows for Everyone on site {siteId}: {everyone.Count}");
                Assert.NotEmpty(everyone);
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var row in everyone)
                    {
                        await permissions.DeletePermissionAsync(row.PermissionId);
                    }

                    return true;
                });

                Assert.True(IsRefusal((await GetAsync(anonymous, $"{Api}/topic?anchor=page:admin&locale=en", "anonymous: after revocation")).Status));
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(asHost, $"{Api}/topic?anchor=page:admin&locale=en", "host: after revocation")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("anchor=")]
        [InlineData("anchor=has%20space")]
        [InlineData("anchor=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
        [InlineData("anchor=page:admin&locale=not%20a%20locale")]
        [InlineData("anchor=page:admin&locale=zz-ZZ-this-is-far-too-long")]
        [InlineData("anchor=page:admin%7Cen")]
        public async Task A_malformed_query_is_NO_TOPIC_or_a_fallback_and_never_a_500(string query)
        {
            // D103 refutation 10: the anchor arrives from the wire on an endpoint an ANONYMOUS visitor can reach.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var anonymous = new Session(GateDrive.NewClient(app, Host), null, null);
                var response = await GetAsync(anonymous, $"{Api}/topic?{query}", "anonymous: " + query);

                // A bad LOCALE is not a bad request: the chain falls back to the site default and English.
                var expected = query.StartsWith("anchor=page:admin&", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NoContent;
                Assert.Equal(expected, response.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_Help_OFF_every_route_is_a_404_even_for_the_host_and_there_is_no_admin_page()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var asHost = await SessionAsync(app, GateDrive.HostUsername, GateDrive.HostPassword, await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername));
                var anonymous = new Session(GateDrive.NewClient(app, Host), null, null);
                var id = Guid.NewGuid();

                foreach (var path in new[] { $"{Api}/topic?anchor=page:admin", $"{Api}/search?q=x", Api, $"{Api}/conflicts", $"{Api}/{id}" })
                {
                    // 404, not 403: a disabled feature has no surface to be refused from.
                    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(asHost, path, "off, host: " + path)).Status);
                    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(anonymous, path, "off, anonymous: " + path)).Status);
                }

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
                    Assert.DoesNotContain(pages, page => page.Path == "admin/help");

                    // The module DEFINITION is discovered on every install, so an operator can place the control by hand:
                    // its server-side service must resolve and answer "nothing" - not dispatch into handlers that are
                    // not composed while help is off.
                    var service = sp.GetRequiredService<Stomme.Modules.ContextualHelp.Services.IHelpService>();
                    Assert.Null(await service.GetTopicAsync("page:admin", "en"));
                    Assert.Empty(await service.GetTopicsAsync());
                    Assert.Empty(await service.SearchAsync("admin"));
                    Assert.Equal(Guid.Empty, await service.SaveTopicAsync(new Stomme.Models.Help.HelpTopicSaveInput(null, "page:x", "en", "X", "X")));
                    Assert.Null(await service.PreviewAsync("**x**"));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_Help_ON_a_fresh_install_carries_the_admin_page_for_Administrators_only()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
                    var page = Assert.Single(pages, item => item.Path == "admin/help");
                    Assert.Contains(page.PermissionList, p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Admin && p.IsAuthorized);
                    Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------------------------------

        private sealed record Session(HttpClient Client, CookieContainer? Cookies, int? UserId);

        /// <summary>A refusal is a 401 or a 403 - anything else (a 200, or a 500) is a finding.</summary>
        private static bool IsRefusal(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        private async Task<Session> SessionAsync(WebApplication app, string username, string password, int userId)
        {
            var client = GateDrive.NewClient(app, Host);
            return new Session(client, await GateDrive.LoginAsync(app, client, Host, username, password, _output), userId);
        }

        private async Task<(HttpStatusCode Status, string Body)> GetAsync(Session who, string path, string label)
        {
            var response = await GateDrive.GetAsync(who.Client, who.Cookies, Host, path, _output, label);
            if (response.Status == HttpStatusCode.InternalServerError)
            {
                // An unhandled exception is an EMPTY 500; the host's captured errors are the only account of its cause.
                foreach (var error in GateDrive.DrainCapturedErrors())
                {
                    _output.WriteLine($"[{label}] host log: {error}");
                }
            }

            return (response.Status, response.Body);
        }

        private Task<(HttpStatusCode Status, string Body)> SendAsync(WebApplication app, Session who, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, who.Client, who.Cookies, Host, who.UserId, method, path, body, _output, label);

        private async Task<Guid> CreatedAsync(WebApplication app, Session who, object body, string label)
        {
            var response = await SendAsync(app, who, HttpMethod.Post, Api, body, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<Guid>(response.Body);
        }
    }
}
