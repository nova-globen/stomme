#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Models;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tests.Fixtures.McpTestModule;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// Personal MCP tokens (D101a, BL-101), driven: a signed-in user makes tokens through the API, and a client with NO cookie
    /// reaches /mcp/v1 with one, as that user. A token reads or calls only what its scopes allow, on its own site only; a
    /// revoked, unknown or other-site token is a 401; a user without an MCP key cannot make one. The browser session's path is
    /// unchanged (McpEndpointDriveTests).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PersonalMcpTokenDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string OtherHost = "mcptokens-b.localhost";

        [Fact]
        public async Task A_personal_token_signs_its_user_in_on_its_site_within_its_scopes_until_revoked()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var hostClient = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);

                var readOnly = await MakeAsync(app, hostClient, cookies, hostUserId, "Desktop", McpTokenScopes.Resources);
                var both = await MakeAsync(app, hostClient, cookies, hostUserId, "Agent", McpTokenScopes.Resources, McpTokenScopes.Tools);
                Assert.StartsWith("smcp_", readOnly.Secret);
                Assert.StartsWith(readOnly.Token.Prefix, readOnly.Secret);

                // The list is the caller's own, and never carries a secret.
                var listed = await GateDrive.GetAsync(hostClient, cookies, Host, "/api/Mcp/tokens", output, "list tokens");
                Assert.Equal(HttpStatusCode.OK, listed.Status);
                Assert.Equal(new[] { "Agent", "Desktop" }, JsonNode.Parse(listed.Body)!.AsArray().Select(token => token!["name"]!.GetValue<string>()).OrderBy(name => name));
                Assert.DoesNotContain(readOnly.Secret, listed.Body);

                // Only its hash is stored.
                var stored = await GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IMcpPersonalTokenRepository>().GetByIdAsync(readOnly.Token.Id))!);
                Assert.Equal(McpPersonalTokenAggregate.Hash(readOnly.Secret), stored.SecretHash);
                Assert.DoesNotContain(readOnly.Secret, JsonSerializer.Serialize(stored));

                using var tool = GateDrive.NewClient(app, Host); // no cookie at all

                // resources only: the site's resources, no tool listed, and a tool call refused (and audited as denied).
                var resources = await RpcAsync(tool, Host, readOnly.Secret, "resources/list", null, "read-only resources");
                Assert.Equal(HttpStatusCode.OK, resources.Status);
                Assert.Contains(Stomme.Modules.Mcp.Contributions.StommeSiteContributor.SiteUri, resources.Body);
                var noTools = await RpcAsync(tool, Host, readOnly.Secret, "tools/list", null, "read-only tools");
                Assert.Equal(HttpStatusCode.OK, noTools.Status);
                Assert.DoesNotContain(McpTestContributor.Echo, noTools.Body);
                var refused = await RpcAsync(tool, Host, readOnly.Secret, "tools/call", new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject { ["text"] = "hi" } }, "read-only call");
                Assert.Contains("\"isError\":true", refused.Body.Replace(" ", string.Empty));

                // both: the tools its user may call, called as that user.
                var tools = await RpcAsync(tool, Host, both.Secret, "tools/list", null, "agent tools");
                Assert.Contains(McpTestContributor.Echo, tools.Body);
                var called = await RpcAsync(tool, Host, both.Secret, "tools/call", new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject { ["text"] = "hi" } }, "agent call");
                Assert.DoesNotContain("\"isError\":true", called.Body.Replace(" ", string.Empty));
                var audited = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IMcpAuditRepository>().ListBySiteAsync(alias.SiteId));
                Assert.Contains(audited, entry => entry.UserId == hostUserId && entry.Subject == McpTestContributor.Echo && entry.Outcome == McpAuditOutcome.Denied);
                Assert.Contains(audited, entry => entry.UserId == hostUserId && entry.Subject == McpTestContributor.Echo && entry.Outcome == McpAuditOutcome.Succeeded);

                // An unknown token, and a token on another site of the same tenant, sign no one in.
                Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(tool, Host, "smcp_" + new string('x', 43), "tools/list", null, "unknown")).Status);
                await CreateOtherSiteAsync(app);
                using var other = GateDrive.NewClient(app, OtherHost);
                Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(other, OtherHost, both.Secret, "tools/list", null, "other site")).Status);

                // Revoked: refused at once.
                var revoked = await GateDrive.SendJsonAsync(app, hostClient, cookies, Host, hostUserId, HttpMethod.Delete, "/api/Mcp/tokens/" + both.Token.Id, null, output, "revoke");
                Assert.Equal(HttpStatusCode.OK, revoked.Status);
                Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(tool, Host, both.Secret, "tools/list", null, "revoked")).Status);
                Assert.Equal(HttpStatusCode.OK, (await RpcAsync(tool, Host, readOnly.Secret, "resources/list", null, "the other still works")).Status);

                // A user without an MCP key cannot make a token.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                var plainMake = await GateDrive.SendJsonAsync(app, plainClient, plainCookies, Host, plainUserId, HttpMethod.Post, "/api/Mcp/tokens",
                    new McpTokenRequest { Name = "Mine", Days = 7, Scopes = new[] { McpTokenScopes.Resources } }, output, "plain user makes a token");
                Assert.Equal(HttpStatusCode.Forbidden, plainMake.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void A_token_always_expires_is_refused_once_expired_or_revoked_and_keeps_only_its_hash()
        {
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            var (token, secret) = McpPersonalTokenAggregate.Issue(Guid.NewGuid(), TenantId.Default, 1, 2, "Desktop", 7, new[] { McpTokenScopes.Tools }, now);
            Assert.True(token.IsUsable(now.AddDays(7).AddSeconds(-1)));
            Assert.False(token.IsUsable(now.AddDays(7)));
            Assert.Equal(McpPersonalTokenAggregate.Hash(secret), token.SecretHash);
            token.Revoke(now.AddHours(1));
            Assert.False(token.IsUsable(now.AddHours(2)));

            Assert.Throws<ArgumentException>(() => McpPersonalTokenAggregate.Issue(Guid.NewGuid(), TenantId.Default, 1, 2, "x", 0, new[] { McpTokenScopes.Tools }, now));
            Assert.Throws<ArgumentException>(() => McpPersonalTokenAggregate.Issue(Guid.NewGuid(), TenantId.Default, 1, 2, "x", 366, new[] { McpTokenScopes.Tools }, now));
            Assert.Throws<ArgumentException>(() => McpPersonalTokenAggregate.Issue(Guid.NewGuid(), TenantId.Default, 1, 2, "x", 7, Array.Empty<string>(), now));
            Assert.Throws<ArgumentException>(() => McpPersonalTokenAggregate.Issue(Guid.NewGuid(), TenantId.Default, 1, 2, "x", 7, new[] { "admin" }, now));
        }

        private async Task<McpTokenCreated> MakeAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string name, params string[] scopes)
        {
            var made = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, "/api/Mcp/tokens",
                new McpTokenRequest { Name = name, Days = 30, Scopes = scopes }, output, "make " + name);
            Assert.Equal(HttpStatusCode.OK, made.Status);
            return JsonSerializer.Deserialize<McpTokenCreated>(made.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        private async Task<(HttpStatusCode Status, string Body)> RpcAsync(HttpClient client, string host, string token, string method, JsonObject? parameters, string label)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + host), McpRpc.Endpoint))
            {
                Content = new StringContent(McpRpc.Body(method, parameters).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] {method} -> {(int)response.StatusCode} {GateDrive.Truncate(body)}");
            return (response.StatusCode, body);
        }

        private static Task<Site> CreateOtherSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Tokens Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    CultureCode = "en",
                });
                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias { Name = OtherHost, TenantId = alias.TenantId, SiteId = site.SiteId, IsDefault = true });
                return site;
            });
    }
}
