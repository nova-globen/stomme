#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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
    /// BL-184: the settings API authorized one entity and wrote another. The bulk door (<c>PUT api/Setting/{entity}/{id}</c>)
    /// decided the ROUTE's entity - a registered user may edit their own User settings - then ADDED each new row with the
    /// entity its BODY named, so a user planted a Site row, and a Host row in the installation's master store. The by-id door
    /// (<c>PUT api/Setting/{id}</c>) decided the BODY's entity, then replaced whatever row the id named, so a user moved a
    /// Site row onto themselves - the site's setting gone. Both are driven here as a registered user with no other role.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SettingEntityBindingTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public SettingEntityBindingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_registered_user_writes_only_their_own_settings_whatever_entity_the_body_names()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var site = (await GateDrive.AliasAsync(app, Host)).SiteId;
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                var siteRow = await InSiteAsync(app, sp => sp.GetRequiredService<ISettingRepository>().AddSettingAsync(
                    new Setting { EntityName = EntityNames.Site, EntityId = site, SettingName = "Bl184SiteSetting", SettingValue = "keep", IsPrivate = false }));

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, _output);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object? body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, plainId, method, path, body, _output, label);

                // The bulk door, on the user's OWN settings: one row of their own, one naming the site, one naming the Host.
                await Send(HttpMethod.Put, $"/api/Setting/{EntityNames.User}/{plainId}", new List<Setting>
                {
                    new() { EntityName = EntityNames.User, EntityId = plainId, SettingName = "Bl184Own", SettingValue = "mine", IsPrivate = false },
                    new() { EntityName = EntityNames.Site, EntityId = site, SettingName = "Bl184PlantedSite", SettingValue = "planted", IsPrivate = false },
                    new() { EntityName = EntityNames.Host, EntityId = -1, SettingName = "Bl184PlantedHost", SettingValue = "planted", IsPrivate = false },
                }, "bulk save naming the site and the Host");

                Assert.Equal("mine", (await GetAsync(app, EntityNames.User, plainId, "Bl184Own"))?.SettingValue);
                Assert.Null(await GetAsync(app, EntityNames.Site, site, "Bl184PlantedSite"));
                Assert.Null(await GetAsync(app, EntityNames.Host, -1, "Bl184PlantedHost"));

                // The by-id door: the SITE's row by its id, with a body claiming it is the user's own.
                var moved = await Send(HttpMethod.Put, $"/api/Setting/{siteRow.SettingId}", new Setting
                {
                    SettingId = siteRow.SettingId, EntityName = EntityNames.User, EntityId = plainId, SettingName = siteRow.SettingName, SettingValue = "taken",
                    IsPrivate = false, CreatedBy = siteRow.CreatedBy, CreatedOn = siteRow.CreatedOn, ModifiedBy = siteRow.ModifiedBy, ModifiedOn = siteRow.ModifiedOn,
                }, "update the site's row by id, claimed as the user's");
                Assert.Equal(HttpStatusCode.Forbidden, moved.Status);
                Assert.Equal("keep", (await GetAsync(app, EntityNames.Site, site, "Bl184SiteSetting"))?.SettingValue);

                // Non-vacuity: the user's own row, by its id, is still theirs to change.
                var own = (await GetAsync(app, EntityNames.User, plainId, "Bl184Own"))!;
                var changed = await Send(HttpMethod.Put, $"/api/Setting/{own.SettingId}", new Setting
                {
                    SettingId = own.SettingId, EntityName = EntityNames.User, EntityId = plainId, SettingName = own.SettingName, SettingValue = "changed",
                    IsPrivate = false, CreatedBy = own.CreatedBy, CreatedOn = own.CreatedOn, ModifiedBy = own.ModifiedBy, ModifiedOn = own.ModifiedOn,
                }, "update the user's own row by id");
                Assert.Equal(HttpStatusCode.OK, changed.Status);
                Assert.Equal("changed", (await GetAsync(app, EntityNames.User, plainId, "Bl184Own"))?.SettingValue);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// The Visitor branch authorizes the visitor id in the site's visitor cookie, and the bulk door's body decided the
        /// rest. When this was found the cookie was the id in plain text, so a caller who wrote it themselves passed; since
        /// BL-185 only a cookie the host issued does (<see cref="VisitorCookieDriveTests"/> drives the forgery). The body-
        /// versus-route binding is still proven here, with a genuine cookie: an anonymous visitor writes their own settings
        /// and nothing else.
        /// </summary>
        [Fact]
        public async Task An_anonymous_visitor_writes_only_their_own_settings_whatever_entity_the_body_names()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var site = (await GateDrive.AliasAsync(app, Host)).SiteId;
                const int visitorId = 424242;
                using var client = GateDrive.NewClient(app, Host);
                var cookies = new CookieContainer();
                var issued = app.Services.GetRequiredService<Stomme.Infrastructure.Security.VisitorCookie>().Protect(visitorId, System.DateTime.UtcNow.AddMinutes(5));
                cookies.Add(new Cookie(Stomme.Infrastructure.Security.VisitorCookie.Name(site), issued, "/", Host));

                await GateDrive.SendJsonAsync(app, client, cookies, Host, null, HttpMethod.Put, $"/api/Setting/{EntityNames.Visitor}/{visitorId}", new List<Setting>
                {
                    new() { EntityName = EntityNames.Visitor, EntityId = visitorId, SettingName = "Bl184VisitorOwn", SettingValue = "mine", IsPrivate = false },
                    new() { EntityName = EntityNames.Site, EntityId = site, SettingName = "Bl184VisitorPlantedSite", SettingValue = "planted", IsPrivate = false },
                    new() { EntityName = EntityNames.Host, EntityId = -1, SettingName = "Bl184VisitorPlantedHost", SettingValue = "planted", IsPrivate = false },
                }, _output, "an anonymous bulk save naming the site and the Host");

                Assert.Null(await GetAsync(app, EntityNames.Site, site, "Bl184VisitorPlantedSite"));
                Assert.Null(await GetAsync(app, EntityNames.Host, -1, "Bl184VisitorPlantedHost"));
                // Non-vacuity: the Visitor door itself answered - the visitor's own row was written.
                Assert.Equal("mine", (await GetAsync(app, EntityNames.Visitor, visitorId, "Bl184VisitorOwn"))?.SettingValue);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<Setting?> GetAsync(WebApplication app, string entityName, int entityId, string settingName) =>
            InSiteAsync(app, async sp => (Setting?)await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(entityName, entityId, settingName));

        private static Task<T> InSiteAsync<T>(WebApplication app, System.Func<System.IServiceProvider, Task<T>> work) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => work(sp));
    }
}
