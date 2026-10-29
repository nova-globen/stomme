#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Modules.Sessions.Application;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Driven on an installed host: one account signed in on two "devices", ONE of them revoked, and the revoked
    /// cookie - still presented, as a thief would - refused on its next request while the other keeps working. Then who
    /// may revoke whose session, the administrator's site view and page, and that a sign-out and "log out everywhere"
    /// end the rows server-side, so a copy of a signed-out cookie is refused too.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class SessionRevokeDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        protected SessionRevokeDriveTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        [Fact]
        public async Task One_session_is_revoked_and_its_cookie_refused_while_the_others_keep_working()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // ---- two devices, one account ---------------------------------------------------------------------------
                using var laptop = GateDrive.NewClient(app, Host);
                var laptopCookies = await GateDrive.LoginAsync(app, laptop, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                using var phone = GateDrive.NewClient(app, Host);
                var phoneCookies = await GateDrive.LoginAsync(app, phone, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var fromLaptop = await MineAsync(laptop, laptopCookies, "laptop lists its sessions");
                Assert.Equal(2, fromLaptop.Count);
                var laptopSession = Assert.Single(fromLaptop, s => s.IsCurrent).Id;
                var phoneSession = Assert.Single(fromLaptop, s => !s.IsCurrent).Id;
                Assert.Equal(phoneSession, Assert.Single(await MineAsync(phone, phoneCookies, "phone lists its sessions"), s => s.IsCurrent).Id);

                // ---- the laptop revokes the phone ------------------------------------------------------------------------
                var revoked = await GateDrive.SendJsonAsync(app, laptop, laptopCookies, Host, hostId, HttpMethod.Delete, $"/api/Session/{phoneSession}", null, _output, "laptop revokes the phone");
                Assert.Equal(HttpStatusCode.NoContent, revoked.Status);

                // The phone's cookie - still presented, as a stolen copy would be - is refused on its very next request.
                var phoneAfter = await GateDrive.GetAsync(phone, phoneCookies, Host, "/api/Session", _output, "the revoked phone cookie");
                Assert.True(phoneAfter.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a revoked cookie got {(int)phoneAfter.Status}");

                // The laptop is untouched and now sees one session.
                Assert.Equal(laptopSession, Assert.Single(await MineAsync(laptop, laptopCookies, "laptop after the revoke")).Id);

                // ---- who may revoke whose ---------------------------------------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, "bl119-plain", "Bl119!Plain$2026", "bl119-plain@gate.test");
                var plainId = await GateDrive.UserIdAsync(app, Host, "bl119-plain");
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, "bl119-plain", "Bl119!Plain$2026", _output);
                var notMine = await GateDrive.SendJsonAsync(app, plain, plainCookies, Host, plainId, HttpMethod.Delete, $"/api/Session/{laptopSession}", null, _output, "a plain user revokes the host's session as their own");
                Assert.Equal(HttpStatusCode.NotFound, notMine.Status);
                var notAdmin = await GateDrive.SendJsonAsync(app, plain, plainCookies, Host, plainId, HttpMethod.Delete, $"/api/Session/site/{laptopSession}", null, _output, "a plain user uses the admin revoke");
                Assert.True(notAdmin.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a plain user got {(int)notAdmin.Status}");
                var siteAsPlain = await GateDrive.GetAsync(plain, plainCookies, Host, "/api/Session/site", _output, "a plain user reads the site's sessions");
                Assert.True(siteAsPlain.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a plain user got {(int)siteAsPlain.Status}");

                // A site Administrator who is not a Host cannot sign the Host out.
                await GateDrive.AddUserAsync(app, Host, "bl119-admin", "Bl119!Admin$2026", "bl119-admin@gate.test", RoleNames.Admin);
                var adminId = await GateDrive.UserIdAsync(app, Host, "bl119-admin");
                using var admin = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, admin, Host, "bl119-admin", "Bl119!Admin$2026", _output);
                var hostProtected = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, adminId, HttpMethod.Delete, $"/api/Session/site/{laptopSession}", null, _output, "an administrator revokes the host");
                Assert.Equal(HttpStatusCode.Forbidden, hostProtected.Status);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(laptop, laptopCookies, Host, "/api/Session", _output, "the host after the refused revoke")).Status);

                // The Host sees every session of the site, and ends the plain user's.
                var site = await SessionsAsync(laptop, laptopCookies, "/api/Session/site", "the host reads the site's sessions");
                Assert.Contains(site, s => s.Id == laptopSession && s.IsCurrent);
                var plainSession = Assert.Single(site, s => s.UserId == plainId).Id;
                Assert.DoesNotContain(site, s => s.Id == phoneSession);
                var hostRevokes = await GateDrive.SendJsonAsync(app, laptop, laptopCookies, Host, hostId, HttpMethod.Delete, $"/api/Session/site/{plainSession}", null, _output, "the host revokes the plain user");
                Assert.Equal(HttpStatusCode.NoContent, hostRevokes.Status);
                var plainAfter = await GateDrive.GetAsync(plain, plainCookies, Host, "/api/Session", _output, "the plain user's revoked cookie");
                Assert.True(plainAfter.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a revoked cookie got {(int)plainAfter.Status}");

                // The administrator's page is placed and served.
                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/sessions", alias.SiteId));
                Assert.NotNull(page);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(laptop, laptopCookies, Host, "/admin/sessions", _output, "the admin page")).Status);

                // ---- a sign-out ends the row: a copy of the signed-out cookie is refused ------------------------------------
                var adminCopy = Copy(adminCookies, Host);
                var (token, antiforgery) = GateDrive.Antiforgery(app, adminId);
                var logout = new HttpRequestMessage(HttpMethod.Post, "/pages/logout")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnurl"] = "/", ["everywhere"] = "false", ["__RequestVerificationToken"] = token }),
                };
                logout.Headers.TryAddWithoutValidation("Cookie", adminCookies.GetCookieHeader(new Uri("http://" + Host)) + "; " + antiforgery);
                _output.WriteLine($"[admin signs out] POST /pages/logout -> {(int)(await admin.SendAsync(logout)).StatusCode}");
                var copyAfter = await GateDrive.GetAsync(admin, adminCopy, Host, "/api/Session", _output, "a copy of the signed-out cookie");
                Assert.True(copyAfter.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a signed-out cookie's copy got {(int)copyAfter.Status}");

                // ---- "log out everywhere" ends every row of the account at once ---------------------------------------------
                // A third device, signed in and idle: its row must end NOW, before its cookie is ever presented again (a
                // stale stamp would refuse the cookie later on its own; the ROW is what the session list shows).
                using var tablet = GateDrive.NewClient(app, Host);
                var tabletCookies = await GateDrive.LoginAsync(app, tablet, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                Assert.Equal(2, (await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<ISessionRegistry>().ListForUserAsync(alias, hostId))).Count);
                var user = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IUserRepository>().GetUserAsync(hostId));
                var everywhere = await GateDrive.SendJsonAsync(app, laptop, laptopCookies, Host, hostId, HttpMethod.Post, "/api/User/logouteverywhere", user, _output, "the host logs out everywhere");
                Assert.True(everywhere.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"logout everywhere got {(int)everywhere.Status}");
                var left = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<ISessionRegistry>().ListForUserAsync(alias, hostId));
                Assert.Empty(left);
                var tabletAfter = await GateDrive.GetAsync(tablet, tabletCookies, Host, "/api/Session", _output, "the tablet after log out everywhere");
                Assert.True(tabletAfter.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"the tablet got {(int)tabletAfter.Status}");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private sealed record SessionRow(Guid Id, int UserId, bool IsCurrent);

        private Task<List<SessionRow>> MineAsync(HttpClient client, System.Net.CookieContainer cookies, string label) =>
            SessionsAsync(client, cookies, "/api/Session", label);

        private async Task<List<SessionRow>> SessionsAsync(HttpClient client, System.Net.CookieContainer cookies, string path, string label)
        {
            var read = await GateDrive.GetAsync(client, cookies, Host, path, _output, label);
            Assert.Equal(HttpStatusCode.OK, read.Status);
            return JsonDocument.Parse(read.Body).RootElement.EnumerateArray()
                .Select(e => new SessionRow(e.GetProperty("id").GetGuid(), e.GetProperty("userId").GetInt32(), e.GetProperty("isCurrent").GetBoolean()))
                .ToList();
        }

        private static System.Net.CookieContainer Copy(System.Net.CookieContainer source, string host)
        {
            var uri = new Uri("http://" + host);
            var copy = new System.Net.CookieContainer();
            foreach (System.Net.Cookie cookie in source.GetCookies(uri))
            {
                copy.Add(uri, new System.Net.Cookie(cookie.Name, cookie.Value));
            }

            return copy;
        }
    }

    public sealed class SqliteSessionRevokeDriveTests(ITestOutputHelper output) : SessionRevokeDriveTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresSessionRevokeDriveTests(ITestOutputHelper output) : SessionRevokeDriveTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenSessionRevokeDriveTests(ITestOutputHelper output) : SessionRevokeDriveTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
