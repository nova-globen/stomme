#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The site's visitor cookie (<c>APP_VISITOR_{siteId}</c>) was the visitor's id in plain text, and three doors
    /// trusted it - the settings API's Visitor branch, <c>GET api/Visitor/{id}</c> (a visitor's IP address, user agent and
    /// pages) and the visit tracking. A caller with no account who wrote the cookie themselves passed as any visitor, by a
    /// sequential id. The cookie is now protected with the host's Data Protection (<see cref="VisitorCookie"/>).
    /// </summary>
    public class VisitorCookieTests
    {
        private readonly VisitorCookie _cookie = new(new EphemeralDataProtectionProvider());

        [Fact]
        public void A_cookie_the_host_issued_reads_back_as_its_visitor_and_refresh_time()
        {
            var refreshAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
            var value = _cookie.Protect(123, refreshAt);

            Assert.DoesNotContain("123", value);
            Assert.Equal((123, refreshAt), _cookie.Read(value));
        }

        [Theory]
        [InlineData("123")]                              // the legacy cookie: an id alone
        [InlineData("123|9/30/2026 10:00:00 PM")]        // the plain cookie every visitor carried before it was protected
        [InlineData("")]
        [InlineData("not-a-protected-value")]
        public void A_value_the_host_did_not_issue_proves_no_visitor(string value)
        {
            Assert.Equal((-1, DateTime.MinValue), _cookie.Read(value));
        }

        [Fact]
        public void A_tampered_value_or_one_protected_for_another_purpose_proves_no_visitor()
        {
            var provider = new EphemeralDataProtectionProvider();
            var cookie = new VisitorCookie(provider);
            var genuine = cookie.Protect(7, DateTime.UtcNow);
            var tampered = genuine.Substring(0, genuine.Length - 2) + (genuine.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
            var otherPurpose = provider.CreateProtector("Stomme.SomethingElse").Protect("7|" + DateTime.UtcNow.Ticks);

            Assert.Equal(-1, cookie.Read(tampered).VisitorId);
            Assert.Equal(-1, cookie.Read(otherPurpose).VisitorId);
            Assert.Equal(7, cookie.Read(genuine).VisitorId);
        }

        [Fact]
        public void A_protected_value_that_names_no_positive_visitor_proves_none()
        {
            var provider = new EphemeralDataProtectionProvider();
            var cookie = new VisitorCookie(provider);
            var protector = provider.CreateProtector(VisitorCookie.Purpose);

            Assert.Equal(-1, cookie.Read(protector.Protect("0|" + DateTime.UtcNow.Ticks)).VisitorId);
            Assert.Equal(-1, cookie.Read(protector.Protect("-5|" + DateTime.UtcNow.Ticks)).VisitorId);
            Assert.Equal(-1, cookie.Read(protector.Protect("12")).VisitorId);
        }
    }

    /// <summary>The three doors, driven on an installed host as an anonymous caller.</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class VisitorCookieDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public VisitorCookieDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_cookie_of_the_callers_own_making_opens_no_door_and_the_one_the_site_issues_does()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var site = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var visitor = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<IVisitorRepository>().AddVisitorAsync(new Visitor
                {
                    SiteId = site, IPAddress = "203.0.113.77", UserAgent = "bl185-agent", Language = "en", Url = "http://localhost/", Referrer = "",
                    Visits = 1, CreatedOn = DateTime.UtcNow, VisitedOn = DateTime.UtcNow,
                }));

                // 1. A cookie of the caller's own making - the plain id, the shape every visitor carried before.
                using (var forger = GateDrive.NewClient(app, Host))
                {
                    var forged = new CookieContainer();
                    forged.Add(new Cookie(VisitorCookie.Name(site), visitor.VisitorId.ToString(System.Globalization.CultureInfo.InvariantCulture), "/", Host));

                    var read = await GateDrive.SendJsonAsync(app, forger, forged, Host, null, HttpMethod.Get, $"/api/Visitor/{visitor.VisitorId}", null, _output, "a forged cookie reads the visitor's record");
                    Assert.Equal(HttpStatusCode.Forbidden, read.Status);
                    Assert.DoesNotContain("203.0.113.77", read.Body);

                    await GateDrive.SendJsonAsync(app, forger, forged, Host, null, HttpMethod.Put, $"/api/Setting/{EntityNames.Visitor}/{visitor.VisitorId}", new List<Setting>
                    {
                        new() { EntityName = EntityNames.Visitor, EntityId = visitor.VisitorId, SettingName = "Bl185Forged", SettingValue = "forged", IsPrivate = false },
                    }, _output, "a forged cookie writes the visitor's settings");
                    Assert.Null(await SettingAsync(app, visitor.VisitorId, "Bl185Forged"));
                }

                // 2. The cookie the site issues for that visitor opens both doors (non-vacuity: the refusal above is the cookie's).
                using (var genuineClient = GateDrive.NewClient(app, Host))
                {
                    var genuine = new CookieContainer();
                    genuine.Add(new Cookie(VisitorCookie.Name(site), app.Services.GetRequiredService<VisitorCookie>().Protect(visitor.VisitorId, DateTime.UtcNow.AddMinutes(5)), "/", Host));

                    var read = await GateDrive.SendJsonAsync(app, genuineClient, genuine, Host, null, HttpMethod.Get, $"/api/Visitor/{visitor.VisitorId}", null, _output, "the issued cookie reads the visitor's record");
                    Assert.Equal(HttpStatusCode.OK, read.Status);
                    Assert.Contains("203.0.113.77", read.Body);

                    await GateDrive.SendJsonAsync(app, genuineClient, genuine, Host, null, HttpMethod.Put, $"/api/Setting/{EntityNames.Visitor}/{visitor.VisitorId}", new List<Setting>
                    {
                        new() { EntityName = EntityNames.Visitor, EntityId = visitor.VisitorId, SettingName = "Bl185Own", SettingValue = "mine", IsPrivate = false },
                    }, _output, "the issued cookie writes the visitor's own settings");
                    Assert.Equal("mine", (await SettingAsync(app, visitor.VisitorId, "Bl185Own"))?.SettingValue);
                }

                // 3. A page view issues a PROTECTED cookie, never the plain id. A browser's headers: the site's default visitor
                //    filter skips a request with no user agent ("(none)") or no language ("??"), as it skips bots.
                using (var browser = GateDrive.NewClient(app, Host))
                {
                    var page = new HttpRequestMessage(HttpMethod.Get, "/");
                    page.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) bl185-drive");
                    page.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    var response = await browser.SendAsync(page);
                    var issued = response.Headers.TryGetValues("Set-Cookie", out var values)
                        ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(VisitorCookie.Name(site) + "=", StringComparison.Ordinal))
                        : null;
                    _output.WriteLine($"[page view] {(int)response.StatusCode}; visitor cookie: {(issued is null ? "(none)" : issued.Substring(0, Math.Min(60, issued.Length)) + "...")}");
                    Assert.NotNull(issued);
                    var value = Uri.UnescapeDataString(issued!.Substring(VisitorCookie.Name(site).Length + 1));
                    Assert.DoesNotMatch(@"^\d+(\|.*)?$", value);
                    Assert.True(app.Services.GetRequiredService<VisitorCookie>().Read(value).VisitorId > 0, "the issued cookie does not read back as a visitor");
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<Setting?> SettingAsync(WebApplication app, int visitorId, string name) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (Setting?)await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Visitor, visitorId, name));
    }
}
