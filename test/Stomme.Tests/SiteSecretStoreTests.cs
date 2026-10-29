using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The shared per-site secret store (<see cref="ISiteSecretStore"/>), DRIVEN through the
    /// production composition of an INSTALLED host on SQLite, PostgreSQL and RavenDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONLY parity cover the store has: it rides the Setting seam and lives outside
    /// <c>Stomme.Repository.Stores</c>, so <c>RavenFrameworkStoreRegistrationTests</c> never discovers it.
    /// PostgreSQL and RavenDB FAIL rather than skip when unreachable.
    /// </para>
    /// <para>
    /// Pinned: set/get/delete round trip in fresh scopes; per-site isolation; the stored row is PRIVATE and its value
    /// is not the plaintext; a replace updates the one row; a tampered value and a value protected under a FOREIGN key
    /// ring (a lost key ring) read as <see cref="SiteSecretStatus.Unreadable"/> without throwing; a
    /// maximum-length (200) reference key persists; a malformed key is rejected.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class SiteSecretStoreTests
    {
        private const string Host = "localhost";
        private const string Plaintext = "whsec_Pl41nT3xt-never-stored";

        private readonly ITestOutputHelper _output;

        public SiteSecretStoreTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new TheoryData<string>
        {
            SqliteGateStore.StoreName,
            PostgresGateStore.StoreName,
            RavenGateStore.StoreName
        };

        [Fact]
        public void Reference_keys_are_lower_case_dotted_and_bounded()
        {
            Assert.True(SiteSecretStore.IsValidReferenceKey($"notifications.channel.{Guid.NewGuid():N}.credential"));
            Assert.True(SiteSecretStore.IsValidReferenceKey($"mcp.server.{Guid.NewGuid():N}.api-key"));
            Assert.True(SiteSecretStore.IsValidReferenceKey(MaxLengthKey()));

            Assert.False(SiteSecretStore.IsValidReferenceKey(null));
            Assert.False(SiteSecretStore.IsValidReferenceKey(""));
            Assert.False(SiteSecretStore.IsValidReferenceKey("credential"));                 // one segment
            Assert.False(SiteSecretStore.IsValidReferenceKey("Notifications.channel.x"));    // upper case
            Assert.False(SiteSecretStore.IsValidReferenceKey("notifications..credential"));  // empty segment
            Assert.False(SiteSecretStore.IsValidReferenceKey("notifications.channel.x "));   // whitespace
            Assert.False(SiteSecretStore.IsValidReferenceKey("notifications:channel.x"));    // separator
            Assert.False(SiteSecretStore.IsValidReferenceKey("mcp.server.-key"));            // dangling hyphen
            Assert.False(SiteSecretStore.IsValidReferenceKey(MaxLengthKey() + "x"));         // 201
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_site_secret_round_trips_protected_private_and_isolated_per_site(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var siteA = alias.SiteId;
                var siteB = alias.SiteId + 1000;
                var key = $"notifications.channel.{Guid.NewGuid():N}.credential";

                // ---- set, then read back in a FRESH scope ------------------------------------------------------
                await InSecretsAsync(app, (secrets, _) => secrets.SetAsync(siteA, key, Plaintext));
                var read = await ReadAsync(app, siteA, key);
                Assert.Equal(SiteSecretStatus.Found, read.Status);
                Assert.Equal(Plaintext, read.Secret);
                Assert.DoesNotContain(Plaintext, read.ToString());

                // ---- the row: private, and NOT the plaintext -------------------------------------------------------
                var row = await RowAsync(app, siteA, key);
                Assert.NotNull(row);
                Assert.True(row.IsPrivate);
                Assert.Equal(EntityNames.Site, row.EntityName);
                Assert.NotEqual(Plaintext, row.SettingValue);
                Assert.DoesNotContain(Plaintext, row.SettingValue);
                _output.WriteLine($"[{store.Name}] stored value length {row.SettingValue.Length}, private={row.IsPrivate}");

                // ---- per-site isolation ---------------------------------------------------------------------------
                Assert.Equal(SiteSecretStatus.NotFound, (await ReadAsync(app, siteB, key)).Status);
                Assert.Null(await RowAsync(app, siteB, key));

                // ---- replace updates the one row --------------------------------------------------------------------
                await InSecretsAsync(app, (secrets, _) => secrets.SetAsync(siteA, key, "rotated-" + Plaintext));
                Assert.Equal("rotated-" + Plaintext, (await ReadAsync(app, siteA, key)).Secret);
                var rows = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<ISettingRepository>().GetSettingsAsync(EntityNames.Site, siteA))
                        .Count(setting => setting.SettingName == key));
                Assert.Equal(1, rows);

                // ---- tampered: a clean Unreadable, never a throw -------------------------------------------------
                await RewriteRawAsync(app, siteA, key, value => value.Substring(0, value.Length - 6) + (value.EndsWith("AAAAAA") ? "BBBBBB" : "AAAAAA"));
                Assert.Equal(new SiteSecretResult(SiteSecretStatus.Unreadable, null), await ReadAsync(app, siteA, key));

                // ---- protected under a FOREIGN key ring (F8, the lost-key-ring shape) ---------------------------------
                var foreign = new EphemeralDataProtectionProvider().CreateProtector("Stomme.Identity.Secrets.v1").Protect(Plaintext);
                await RewriteRawAsync(app, siteA, key, _ => foreign);
                Assert.Equal(SiteSecretStatus.Unreadable, (await ReadAsync(app, siteA, key)).Status);

                // ---- not a protected payload at all ------------------------------------------------------------------
                await RewriteRawAsync(app, siteA, key, _ => "not-a-protected-payload");
                Assert.Equal(SiteSecretStatus.Unreadable, (await ReadAsync(app, siteA, key)).Status);

                // ---- delete, idempotently ------------------------------------------------------------------------
                await InSecretsAsync(app, (secrets, _) => secrets.DeleteAsync(siteA, key));
                Assert.Equal(SiteSecretStatus.NotFound, (await ReadAsync(app, siteA, key)).Status);
                Assert.Null(await RowAsync(app, siteA, key));
                await InSecretsAsync(app, (secrets, _) => secrets.DeleteAsync(siteA, key));

                // ---- the column bound, on the live store: a 200-character key persists ------------------------------
                var longKey = MaxLengthKey();
                await InSecretsAsync(app, (secrets, _) => secrets.SetAsync(siteA, longKey, Plaintext));
                Assert.Equal(Plaintext, (await ReadAsync(app, siteA, longKey)).Secret);

                // ---- a malformed key is rejected before any store is touched ------------------------------------------
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    InSecretsAsync(app, (secrets, _) => secrets.SetAsync(siteA, "Notifications.Channel", Plaintext)));
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    InSecretsAsync(app, (secrets, _) => secrets.SetAsync(siteA, key, "")));

                if (store.Name == PostgresGateStore.StoreName)
                {
                    await AssertPostgresSettingNameBoundAsync(scratch.ContentRoot);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // 200 characters, valid: "k." + 198 lower-case letters.
        private static string MaxLengthKey() => "k." + new string('a', SiteSecretStore.MaxReferenceKeyLength - 2);

        private static Task InSecretsAsync(WebApplication app, Func<ISiteSecretStore, IServiceProvider, Task> work) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await work(sp.GetRequiredService<ISiteSecretStore>(), sp);
                return true;
            });

        private static Task<SiteSecretResult> ReadAsync(WebApplication app, int siteId, string key) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<ISiteSecretStore>().GetAsync(siteId, key));

        private static Task<Setting> RowAsync(WebApplication app, int siteId, string key) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) =>
                sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, siteId, key));

        private static Task RewriteRawAsync(WebApplication app, int siteId, string key, Func<string, string> rewrite) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var row = await settings.GetSettingAsync(EntityNames.Site, siteId, key);
                row.SettingValue = rewrite(row.SettingValue);
                await settings.UpdateSettingAsync(row);
                return true;
            });

        // The EntityBuilders lie (CLAUDE.md): read the bound off the LIVE migrated PostgreSQL catalogue.
        private async Task AssertPostgresSettingNameBoundAsync(string contentRoot)
        {
            // The scratch install's own connection string, as ScratchGateInstall wrote it to the override file.
            var overrides = System.Text.Json.Nodes.JsonNode.Parse(
                await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(contentRoot, "appsettings.override.json")))!;
            var connectionString = overrides["ConnectionStrings"]!["DefaultConnection"]!.GetValue<string>();
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT table_name, character_maximum_length FROM information_schema.columns " +
                "WHERE column_name = 'setting_name' ORDER BY table_name";
            await using var reader = await command.ExecuteReaderAsync();
            var bounds = new System.Collections.Generic.List<string>();
            while (await reader.ReadAsync())
            {
                bounds.Add($"{reader.GetString(0)}={(reader.IsDBNull(1) ? "unbounded" : reader.GetInt32(1).ToString())}");
                if (!reader.IsDBNull(1))
                {
                    Assert.Equal(SiteSecretStore.MaxReferenceKeyLength, reader.GetInt32(1));
                }
            }

            _output.WriteLine($"[PostgreSQL] live setting_name bounds: {string.Join(", ", bounds)}");
            Assert.NotEmpty(bounds);
        }
    }
}
