#nullable enable
using System.Net;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-245 - a custom entity's settings (an app module's own) are closed to anonymous callers. A row its author left
    /// non-private was readable by anyone at <c>api/Setting?entityName=&lt;custom&gt;</c>, with no cookie. Driven: anonymous
    /// is refused (403, nothing listed, by id too); a signed-in user still reads the non-private row and not the private one;
    /// the Site's non-private settings, which the browser client renders from, stay public.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CustomEntitySettingsAnonymousTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Entity = "NotesConfig";

        [Fact]
        public async Task Anonymous_callers_are_refused_a_custom_entitys_settings_and_signed_in_users_still_read_them()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var publicId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    await settings.AddSettingAsync(new Setting { EntityName = Entity, EntityId = -1, SettingName = "Hidden", SettingValue = "private-value", IsPrivate = true });
                    return (await settings.AddSettingAsync(new Setting { EntityName = Entity, EntityId = -1, SettingName = "Shown", SettingValue = "public-value", IsPrivate = false })).SettingId;
                });

                using var anonymous = GateDrive.NewClient(app, Host);
                var list = await GateDrive.GetAsync(anonymous, null, Host, $"/api/Setting?entityName={Entity}&entityId=-1", output, "anonymous: the list");
                Assert.Equal(HttpStatusCode.Forbidden, list.Status);
                Assert.DoesNotContain("public-value", list.Body);
                var byId = await GateDrive.GetAsync(anonymous, null, Host, $"/api/Setting/{publicId}/{Entity}", output, "anonymous: by id");
                Assert.NotEqual(HttpStatusCode.OK, byId.Status);
                Assert.DoesNotContain("public-value", byId.Body);
                var site = await GateDrive.GetAsync(anonymous, null, Host, $"/api/Setting?entityName=Site&entityId={(await GateDrive.AliasAsync(app, Host)).SiteId}", output, "anonymous: the site's settings");
                Assert.Equal(HttpStatusCode.OK, site.Status);

                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plain = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, plain, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var signedIn = await GateDrive.GetAsync(plain, cookies, Host, $"/api/Setting?entityName={Entity}&entityId=-1", output, "a signed-in user: the list");
                Assert.Equal(HttpStatusCode.OK, signedIn.Status);
                Assert.Contains("public-value", signedIn.Body);
                Assert.DoesNotContain("private-value", signedIn.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
