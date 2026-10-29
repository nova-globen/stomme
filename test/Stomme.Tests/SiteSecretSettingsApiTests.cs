#nullable enable
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A secret in the per-site secret store is a private Site SETTING row, and the generic settings API is
    /// Administrator-gated for Site settings - so without a guard any site Administrator, holding none of the module's
    /// own keys, could read the ciphertext, overwrite the secret (it then reads back Unreadable) or delete it through
    /// <c>/api/Setting</c>, bypassing the module's <c>[Requires]</c> gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven over HTTP as the HOST - the most privileged caller the API has, so every refusal here holds for an
    /// Administrator a fortiori. The store's reference-key namespace is RESERVED: every door of <c>SettingController</c>
    /// refuses it, and the store itself - the one legitimate writer - still round-trips.
    /// </para>
    /// <para>
    /// The name is matched CASE-INSENSITIVELY, because SQL Server's default collation and RavenDB compare setting names that
    /// way: an upper-cased reference key would otherwise reach the secret's own row through the name-based doors.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SiteSecretSettingsApiTests
    {
        private const string Host = "localhost";
        private const string Key = "notifications.channel.0123456789abcdef0123456789abcdef.credential";
        private const string Secret = "bl147-the-real-token";

        private readonly ITestOutputHelper _output;

        public SiteSecretSettingsApiTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task No_door_of_the_settings_API_reads_writes_deletes_or_imports_a_stored_secret()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var site = alias.SiteId;
                await InSiteAsync(app, sp => sp.GetRequiredService<ISiteSecretStore>().SetAsync(site, Key, Secret));

                // An ORDINARY private site setting beside it, so the list below has something it must show: a fresh install
                // has no other Site settings, and an empty list would prove nothing (measured - it was empty).
                await InSiteAsync(app, sp => sp.GetRequiredService<ISettingRepository>().AddSettingAsync(
                    new Setting { EntityName = EntityNames.Site, EntityId = site, SettingName = "Bl147Seed", SettingValue = "seed", IsPrivate = true }));
                var row = (await InSiteAsync(app, sp => sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, site, Key)))!;

                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var failures = new List<string>();

                async Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object? body, string label) =>
                    await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, _output, label);

                // ---- READ: not listed, not fetchable by id --------------------------------------------------------------
                var list = await GateDrive.GetAsync(client, cookies, Host, $"/api/Setting?entityName=Site&entityId={site}", _output, "list site settings");
                if (list.Body.Contains(Key, System.StringComparison.OrdinalIgnoreCase)) failures.Add("the list carries the secret row");
                if (!list.Body.Contains("Bl147Seed", System.StringComparison.Ordinal)) failures.Add("the list does not carry the ordinary private setting - the read proves nothing");

                var byId = await GateDrive.GetAsync(client, cookies, Host, $"/api/Setting/{row.SettingId}/Site", _output, "get the secret by id");
                if (byId.Body.Contains(Key, System.StringComparison.OrdinalIgnoreCase) || byId.Body.Contains(row.SettingValue)) failures.Add($"GET by id returned the row ({(int)byId.Status})");

                // ---- WRITE: every door, in both cases of the name --------------------------------------------------------
                foreach (var name in new[] { Key, Key.ToUpperInvariant() })
                {
                    await Send(HttpMethod.Put, $"/api/Setting/Site/{site}/{name}", new Setting { SettingValue = "overwritten", IsPrivate = true }, $"PUT by name ({name[..13]})");
                    await Send(HttpMethod.Put, $"/api/Setting/Site/{site}", new List<Setting> { new() { EntityName = EntityNames.Site, EntityId = site, SettingName = name, SettingValue = "overwritten", IsPrivate = true } }, $"bulk PUT ({name[..13]})");
                    await Send(HttpMethod.Delete, $"/api/Setting/Site/{site}/{name}", null, $"DELETE by name ({name[..13]})");
                }

                // PUT by id REPLACES the row from the body, so each body is the row as a client loaded it (audit fields and all -
                // measured: a partial body is an empty 500 on the NOT NULL CreatedBy, which would prove nothing about the guard).
                Setting AsLoaded(string name, bool isPrivate) => new()
                {
                    SettingId = row.SettingId, EntityName = row.EntityName, EntityId = row.EntityId, SettingName = name, SettingValue = "overwritten",
                    IsPrivate = isPrivate, CreatedBy = row.CreatedBy, CreatedOn = row.CreatedOn, ModifiedBy = row.ModifiedBy, ModifiedOn = row.ModifiedOn,
                };

                await Send(HttpMethod.Put, $"/api/Setting/{row.SettingId}", AsLoaded(Key, isPrivate: true), "PUT by id");
                await Send(HttpMethod.Put, $"/api/Setting/{row.SettingId}", AsLoaded("Innocent", isPrivate: false), "PUT by id, renamed");
                // The same row by id, its own name, declared NOT private: the body alone looks harmless - the ROW is what decides.
                await Send(HttpMethod.Put, $"/api/Setting/{row.SettingId}", AsLoaded(Key, isPrivate: false), "PUT by id, declared public");
                await Send(HttpMethod.Delete, $"/api/Setting/{row.SettingId}/Site", null, "DELETE by id");
                await Send(HttpMethod.Post, "/api/Setting/import", new Result { Message = $"Site,{site},{Key},overwritten,true" }, "import");

                // ---- nor can a NEW row be planted in the namespace, to be read later as somebody's secret ---------------
                const string planted = "mcp.server.fedcba9876543210fedcba9876543210.api-key";
                var post = await Send(HttpMethod.Post, "/api/Setting", new Setting { EntityName = EntityNames.Site, EntityId = site, SettingName = planted, SettingValue = "planted", IsPrivate = true }, "POST a planted secret");
                if (post.Status != HttpStatusCode.Forbidden) failures.Add($"POST of a reserved name answered {(int)post.Status}");
                if (await InSiteAsync(app, sp => sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, site, planted)) is not null) failures.Add("a reserved-name row was planted");

                // ...not even UPPER-CASED: on SQL Server and RavenDB the store's own lookup would find that row later and read it
                // as the secret (Unreadable), so a pre-planted variant is a denial the store cannot see coming.
                var plantedUpper = await Send(HttpMethod.Put, $"/api/Setting/Site/{site}/{planted.ToUpperInvariant()}", new Setting { SettingValue = "planted", IsPrivate = true }, "PUT a planted, upper-cased secret");
                if (plantedUpper.Status != HttpStatusCode.Forbidden) failures.Add($"an upper-cased reserved name answered {(int)plantedUpper.Status}");

                // The upper-cased name must not have planted a TWIN either (on SQLite, whose name comparison is case-sensitive,
                // that is where an upper-cased write would land; on SQL Server and RavenDB it would hit the secret itself).
                var rows = await InSiteAsync(app, async sp => (await sp.GetRequiredService<ISettingRepository>().GetSettingsAsync(EntityNames.Site, site)).ToList());
                if (rows.Count(item => string.Equals(item.SettingName, Key, System.StringComparison.OrdinalIgnoreCase)) != 1) failures.Add("a case-variant twin of the secret row was written");

                // ---- the secret is exactly what the store wrote, and the store still round-trips -------------------------
                var read = await InSiteAsync(app, sp => sp.GetRequiredService<ISiteSecretStore>().GetAsync(site, Key));
                if (read.Status != SiteSecretStatus.Found || read.Secret != Secret) failures.Add($"the secret after the attempts: {read.Status}");
                await InSiteAsync(app, sp => sp.GetRequiredService<ISiteSecretStore>().SetAsync(site, Key, "rotated"));
                if ((await InSiteAsync(app, sp => sp.GetRequiredService<ISiteSecretStore>().GetAsync(site, Key))).Secret != "rotated") failures.Add("the store itself could not rotate the secret");

                // ---- NON-VACUITY: an ordinary private site setting is still the host's to read and write ------------------
                await Send(HttpMethod.Put, $"/api/Setting/Site/{site}/Bl147Ordinary", new Setting { SettingValue = "plain", IsPrivate = true }, "PUT an ordinary private setting");
                var ordinary = await GateDrive.GetAsync(client, cookies, Host, $"/api/Setting?entityName=Site&entityId={site}", _output, "list again");
                if (!ordinary.Body.Contains("Bl147Ordinary")) failures.Add("an ordinary private site setting is no longer writable or listed");

                // ...and a NON-private setting whose name has the reserved SHAPE is ordinary too: the MCP tool-approval override
                // (mcp.tool.{name}.approval) is exactly that, and an administrator is meant to set it.
                const string approval = "mcp.tool.pages.list.approval";
                await Send(HttpMethod.Put, $"/api/Setting/Site/{site}/{approval}", new Setting { SettingValue = "Deny", IsPrivate = false }, "PUT an MCP approval override");
                if ((await InSiteAsync(app, sp => sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, site, approval)))?.SettingValue != "Deny")
                {
                    failures.Add("a non-private dotted setting (the MCP approval override) can no longer be written");
                }

                Assert.True(failures.Count == 0, string.Join("; ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<T> InSiteAsync<T>(WebApplication app, System.Func<System.IServiceProvider, Task<T>> work) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => work(sp));

        private static Task InSiteAsync(WebApplication app, System.Func<System.IServiceProvider, Task> work) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => { await work(sp); return 0; });
    }
}
