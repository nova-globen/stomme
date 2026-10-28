#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 5 (D110) - a connection's secrets through the per-site secret store on an INSTALLED host, on SQLite,
    /// PostgreSQL and RavenDB: the declared fields are stored under the closed key grammar and resolved by name; a blank
    /// value leaves a field as it was; an undeclared field is refused BEFORE anything is written; a row whose protected value
    /// no longer unprotects reads as Unreadable, typed, never a throw; a delete removes every key; and the settings API - the
    /// door BL-147 closed - neither lists nor overwrites nor deletes a connection secret for the Host (ConnectionSecretSettingApiBypassTests).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ConnectionSecretsTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_connections_secrets_are_stored_under_the_closed_grammar_resolved_typed_and_removed_together(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);
            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var site = alias.SiteId;
                var connectionId = Guid.NewGuid();

                // ---- store: declared fields only, checked before the first write ----
                var stored = await InSecretsAsync(app, secrets => secrets.StoreAsync(site, connectionId, AuthenticationType.OAuth2ClientCredentials,
                    new Dictionary<string, string> { ["client-id"] = "app", ["client-secret"] = "s3cret", ["token-url"] = "" }, null));
                Assert.Equal(new[] { "client-id", "client-secret" }, stored);
                var refused = await Assert.ThrowsAsync<ArgumentException>(() => InSecretsAsync(app, secrets => secrets.StoreAsync(site, connectionId, AuthenticationType.OAuth2ClientCredentials,
                    new Dictionary<string, string> { ["token-url"] = "https://login.example.com/token", ["../x"] = "evil" }, stored)));
                Assert.Contains("../x", refused.Message);
                Assert.Equal(SiteSecretStatus.NotFound, (await ReadAsync(app, site, ConnectionCredentialKeys.For(connectionId, "token-url"))).Status); // nothing of that call was written

                var undeclared = await Assert.ThrowsAsync<ArgumentException>(() => InSecretsAsync(app, secrets => secrets.StoreAsync(site, connectionId, AuthenticationType.Basic,
                    new Dictionary<string, string> { ["client-secret"] = "x" }, null)));
                Assert.Contains("Basic", undeclared.Message);

                // ---- the rows: private, in the store's key grammar, not the plaintext ----
                var row = await RowAsync(app, site, ConnectionCredentialKeys.For(connectionId, "client-secret"));
                Assert.NotNull(row);
                Assert.True(row.IsPrivate);
                Assert.DoesNotContain("s3cret", row.SettingValue);

                // ---- resolve: values by field ----
                var connection = ConnectionAggregate.Create(connectionId, TenantId.Default, site, "OAuth API", "rest", ConnectorEndpointKind.Url, "https://api.example.com", null, stored, 30, DateTime.UtcNow);
                var resolved = await InSecretsAsync(app, secrets => secrets.ResolveAsync(site, connection));
                Assert.True(resolved.IsComplete);
                Assert.Equal(("app", "s3cret"), (resolved.Values["client-id"], resolved.Values["client-secret"]));

                // ---- a blank value leaves the field as it was ----
                await InSecretsAsync(app, secrets => secrets.StoreAsync(site, connectionId, AuthenticationType.OAuth2ClientCredentials, new Dictionary<string, string> { ["client-secret"] = "" }, stored));
                Assert.Equal("s3cret", (await InSecretsAsync(app, secrets => secrets.ResolveAsync(site, connection))).Values["client-secret"]);

                // ---- tampered: Unreadable, typed, the other field still read ----
                await RewriteRawAsync(app, site, ConnectionCredentialKeys.For(connectionId, "client-secret"), value => value.Substring(0, value.Length - 6) + (value.EndsWith("AAAAAA") ? "BBBBBB" : "AAAAAA"));
                var damaged = await InSecretsAsync(app, secrets => secrets.ResolveAsync(site, connection));
                Assert.False(damaged.IsComplete);
                Assert.Equal(new[] { "client-secret" }, damaged.Unreadable);
                Assert.Equal("app", damaged.Values["client-id"]);
                Assert.DoesNotContain("client-secret", damaged.Values.Keys);

                // ---- the settings API (the Host, every door) does not reach the rows (BL-147) ----
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var key = ConnectionCredentialKeys.For(connectionId, "client-id");
                var list = await GateDrive.GetAsync(client, cookies, Host, $"/api/Setting?entityName=Site&entityId={site}", output, "list site settings");
                Assert.DoesNotContain(key, list.Body, StringComparison.OrdinalIgnoreCase);
                var put = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Setting/Site/{site}/{key}", new Setting { SettingValue = "overwritten", IsPrivate = true }, output, "PUT the secret by name");
                Assert.NotEqual(HttpStatusCode.OK, put.Status);
                Assert.Equal("app", (await InSecretsAsync(app, secrets => secrets.ResolveAsync(site, connection))).Values["client-id"]);
                var delete = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Setting/Site/{site}/{key}", null, output, "DELETE the secret by name");
                Assert.NotEqual(HttpStatusCode.OK, delete.Status);
                Assert.NotNull(await RowAsync(app, site, key));

                // ---- delete removes every key, idempotently ----
                await InSecretsAsync(app, secrets => secrets.DeleteAsync(site, connectionId, connection.CredentialFields));
                Assert.Null(await RowAsync(app, site, ConnectionCredentialKeys.For(connectionId, "client-id")));
                Assert.Null(await RowAsync(app, site, ConnectionCredentialKeys.For(connectionId, "client-secret")));
                await InSecretsAsync(app, secrets => secrets.DeleteAsync(site, connectionId, connection.CredentialFields));
                var gone = await InSecretsAsync(app, secrets => secrets.ResolveAsync(site, connection));
                Assert.Equal(new[] { "client-id", "client-secret" }, gone.Missing);
                Assert.Empty(gone.Values);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<T> InSecretsAsync<T>(WebApplication app, Func<ConnectionSecrets, Task<T>> work) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => work(new ConnectionSecrets(sp.GetRequiredService<ISiteSecretStore>())));

        private static Task InSecretsAsync(WebApplication app, Func<ConnectionSecrets, Task> work) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => { await work(new ConnectionSecrets(sp.GetRequiredService<ISiteSecretStore>())); return 0; });

        private static Task<SiteSecretResult> ReadAsync(WebApplication app, int siteId, string key) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<ISiteSecretStore>().GetAsync(siteId, key));

        private static Task<Setting?> RowAsync(WebApplication app, int siteId, string key) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (Setting?)await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, siteId, key));

        private static Task RewriteRawAsync(WebApplication app, int siteId, string key, Func<string, string> rewrite) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var row = await settings.GetSettingAsync(EntityNames.Site, siteId, key);
                row.SettingValue = rewrite(row.SettingValue);
                await settings.UpdateSettingAsync(row);
                return 0;
            });
    }
}
