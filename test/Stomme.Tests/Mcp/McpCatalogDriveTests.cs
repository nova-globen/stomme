#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Models;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The MCP catalog DRIVEN on an installed site, over HTTP, with real principals, the
    /// real access guard and the real stores - on SQLite, PostgreSQL and RavenDB. FAIL-NOT-SKIP.
    /// </summary>
    /// <remarks>
    /// This is also <c>McpAuditLedgerTests</c>: the host is configured with <c>Logging:LogLevel:Default = Warning</c>,
    /// the setting under which an earlier version's logged "audit" disappeared, and every outcome below is asserted as a ROW.
    /// </remarks>
    [Trait("Category", "ExternalDatabase")]
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpCatalogDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpCatalogDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        /// <summary>
        /// BOTH MCP switches on, for every driven MCP suite. <b>One settings object means ONE installed template per
        /// provider</b> shared by the catalog, endpoint and site-isolation drives; a second object, however
        /// small the difference, is a second real install per provider per process.
        /// </summary>
        /// <remarks>
        /// The two halves are SEPARATE switches and stay separate in the product: <c>Mcp:Enabled</c> is the
        /// client half - registering external servers - and <c>Mcp:Server:Enabled</c> is the endpoint this install
        /// publishes. They are turned on together HERE only because these suites drive both.
        /// <c>McpSwitchIndependenceTests</c> is what asserts neither implies the other. The <c>Warning</c> log level is
        /// the setting an earlier version's logged "audit" vanished under, and every audit assertion is made through it.
        /// </remarks>
        internal static JsonObject McpEnabled() => new()
        {
            ["Mcp"] = new JsonObject { ["Enabled"] = true, ["Server"] = new JsonObject { ["Enabled"] = true } },
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning" } },
        };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_catalog_gates_approves_dispatches_and_audits_every_outcome_as_a_row(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var plain = await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, _output);

                Task<string> AsHost(string path) => BodyAsync(hostClient, hostCookies, path, storeName + " host");
                Task<string> AsPlain(string path) => BodyAsync(plainClient, plainCookies, path, storeName + " plain");

                // ---- what each caller is SHOWN ------------------------------------------------------------------
                var hostTools = (await AsHost("/api/McpTest/tools")).Split(',', StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains(McpTestContributor.Echo, hostTools);
                Assert.Contains(McpTestContributor.Confirm, hostTools);
                Assert.DoesNotContain(McpTestContributor.Never, hostTools);
                // The FRAMEWORK'S OWN contribution is here too, and that is asserted rather than tolerated: the host
                // account holds mcp:view through the seeded Administrators row, so stomme://site is visible to it.
                // An exact-equality list here would have to be edited every time a module contributes, and would say
                // nothing about WHO may see what - which is the property these two lines are for.
                var hostResources = (await AsHost("/api/McpTest/resources")).Split(',', StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains(McpTestContributor.Greeting, hostResources);
                Assert.Contains(McpTestContributor.ItemTemplate, hostResources);
                Assert.Contains(Stomme.Modules.Mcp.Contributions.StommeSiteContributor.SiteUri, hostResources);

                Assert.Equal(string.Empty, await AsPlain("/api/McpTest/tools"));
                // ...and a registered-only user sees NEITHER the fixture's resources nor the framework's own, which is
                // what makes the line above a statement about the gate rather than about the contributor list.
                Assert.Equal(string.Empty, await AsPlain("/api/McpTest/resources"));
                Assert.Equal(string.Empty, await BodyAsync(anonymousClient, null, "/api/McpTest/tools", storeName + " anonymous"));

                // Listing is not audited, and an anonymous caller cannot be named, so nothing is on the ledger yet.
                Assert.Empty(await LedgerAsync(app, siteId));

                // ---- a permitted Auto tool RUNS, and the ledger keeps the hash, never the arguments ---------------
                const string arguments = "{\"text\":\"a secret the ledger must not keep\"}";
                var ran = McpTestContributor.Invocations;
                Assert.Equal("ok:{\"echo\":" + arguments + "}", await AsHost($"/api/McpTest/call?name={McpTestContributor.Echo}&arguments={Uri.EscapeDataString(arguments)}"));
                Assert.Equal(ran + 1, McpTestContributor.Invocations);

                var entry = Assert.Single(await LedgerAsync(app, siteId));
                Assert.Equal((hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Echo, McpAuditOutcome.Succeeded),
                    (entry.UserId, entry.Verb, entry.Subject, entry.Outcome));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments))).ToLowerInvariant(), entry.ArgumentsSha256);
                Assert.Equal(arguments.Length, entry.ArgumentsLength);
                Assert.Equal(DateTimeKind.Utc, entry.OccurredOnUtc.Kind);

                // ---- a caller without the key gets "unknown", the handler never runs, and it is a Denied ROW ------
                ran = McpTestContributor.Invocations;
                Assert.Equal(McpTestController.Unknown, await AsPlain($"/api/McpTest/call?name={McpTestContributor.Echo}&arguments=1"));
                Assert.Equal(McpTestController.Unknown, await AsPlain($"/api/McpTest/read?uri={Uri.EscapeDataString(McpTestContributor.Greeting)}"));
                Assert.Equal(ran, McpTestContributor.Invocations);
                await AssertLastAsync(app, siteId, plain.UserId, McpAuditVerb.ResourceRead, McpTestContributor.Greeting, McpAuditOutcome.Denied);
                Assert.Contains(await LedgerAsync(app, siteId), row => row.UserId == plain.UserId && row.Verb == McpAuditVerb.ToolCall && row.Outcome == McpAuditOutcome.Denied);

                // ---- unknown is the SAME answer as denied - and still a row, with the subject as asked ------------
                Assert.Equal(McpTestController.Unknown, await AsHost("/api/McpTest/call?name=no_such_tool"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, "no_such_tool", McpAuditOutcome.Denied);
                Assert.Equal(McpTestController.Unknown, await AsHost("/api/McpTest/read?uri=" + Uri.EscapeDataString("mcptest://nothing")));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ResourceRead, "mcptest://nothing", McpAuditOutcome.Denied);

                // ---- Deny never runs; Confirm never runs EITHER, with its own outcome and one loud log -------------
                ran = McpTestContributor.Invocations;
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Never}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Never, McpAuditOutcome.Denied);

                GateDrive.DrainCapturedErrors();
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Confirm}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Confirm, McpAuditOutcome.ConfirmDenied);
                Assert.Equal(ran, McpTestContributor.Invocations);
                Assert.Single(GateDrive.DrainCapturedErrors(), line => line.Contains("approval pipeline", StringComparison.OrdinalIgnoreCase) && line.Contains(McpTestContributor.Confirm));

                // ---- resources: a plain URI and a template, both read through the contributor that published them --
                Assert.Equal("hello", await AsHost("/api/McpTest/read?uri=" + Uri.EscapeDataString(McpTestContributor.Greeting)));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ResourceRead, McpTestContributor.Greeting, McpAuditOutcome.Succeeded);
                Assert.Equal("item 42", await AsHost("/api/McpTest/read?uri=" + Uri.EscapeDataString("mcptest://item/42")));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ResourceRead, "mcptest://item/42", McpAuditOutcome.Succeeded);

                // ---- a handler that throws is a Failed row and an error result WITHOUT the handler's words --------
                var failed = await AsHost($"/api/McpTest/call?name={McpTestContributor.Throws}");
                Assert.StartsWith("error:", failed);
                Assert.DoesNotContain(McpTestContributor.SecretInException, failed);
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Throws, McpAuditOutcome.Failed);

                // ---- the per-site override: it can TIGHTEN anything ----------------------------------------------
                await SetApprovalAsync(app, siteId, McpTestContributor.Echo, "Deny");
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Echo}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Echo, McpAuditOutcome.Denied);
                Assert.DoesNotContain(McpTestContributor.Echo, (await AsHost("/api/McpTest/tools")).Split(','));

                // ...a value that is not one of the three names is a Deny, never "whatever the descriptor said" ....
                await SetApprovalAsync(app, siteId, McpTestContributor.Echo, "Atuo");
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Echo}"));
                await SetApprovalAsync(app, siteId, McpTestContributor.Echo, "2");
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Echo}"));

                // ...it can open a Confirm tool whose name does NOT say it changes something .......................
                await SetApprovalAsync(app, siteId, McpTestContributor.Confirm, "auto");
                Assert.StartsWith("ok:", await AsHost($"/api/McpTest/call?name={McpTestContributor.Confirm}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Confirm, McpAuditOutcome.Succeeded);

                // ...and it can NOT make a mutating-named tool Auto, nor open one the contributor marked Deny ......
                ran = McpTestContributor.Invocations;
                await SetApprovalAsync(app, siteId, McpTestContributor.Mutating, "Auto");
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Mutating}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Mutating, McpAuditOutcome.ConfirmDenied);
                await SetApprovalAsync(app, siteId, McpTestContributor.Never, "Auto");
                Assert.Equal(McpTestController.Unknown, await AsHost($"/api/McpTest/call?name={McpTestContributor.Never}"));
                await AssertLastAsync(app, siteId, hostUserId, McpAuditVerb.ToolCall, McpTestContributor.Never, McpAuditOutcome.Denied);
                Assert.Equal(ran, McpTestContributor.Invocations);

                // ---- every row is this site's, and none of them is a copy of what was sent -------------------------
                var ledger = await LedgerAsync(app, siteId);
                Assert.All(ledger, row => Assert.Equal(siteId, row.SiteId));
                Assert.DoesNotContain(ledger, row => row.Subject.Contains("secret", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_server_half_off_there_is_no_catalog_at_all()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                using var scope = app.Services.CreateScope();
                Assert.Null(scope.ServiceProvider.GetService<Stomme.Mcp.IMcpServerCatalog>());
                Assert.Null(scope.ServiceProvider.GetService<McpContributorRegistry>());
                Assert.Empty(scope.ServiceProvider.GetServices<Stomme.Mcp.IMcpContributor>());

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var (status, body, _) = await GateDrive.GetAsync(client, cookies, Host, "/api/McpTest/tools", _output, "off");
                Assert.Equal(HttpStatusCode.NotFound, status);
                Assert.Contains("mcp-disabled", body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> BodyAsync(HttpClient client, CookieContainer? cookies, string path, string label)
        {
            var (status, body, _) = await GateDrive.GetAsync(client, cookies, Host, path, _output, label);
            Assert.Equal(HttpStatusCode.OK, status);
            return body;
        }

        private static Task<List<McpAuditEntryAggregate>> LedgerAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IMcpAuditRepository>().ListBySiteAsync(siteId)).OrderBy(row => row.OccurredOnUtc).ToList());

        private static async Task AssertLastAsync(WebApplication app, int siteId, int userId, McpAuditVerb verb, string subject, McpAuditOutcome outcome)
        {
            var last = (await LedgerAsync(app, siteId)).Last();
            Assert.Equal((userId, verb, subject, outcome), (last.UserId, last.Verb, last.Subject, last.Outcome));
        }

        private static Task<bool> SetApprovalAsync(WebApplication app, int siteId, string tool, string value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var name = McpServerCatalog.ApprovalSettingName(tool);
                var existing = await settings.GetSettingAsync(EntityNames.Site, siteId, name);
                if (existing is null)
                {
                    await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = siteId, SettingName = name, SettingValue = value, IsPrivate = true });
                }
                else
                {
                    existing.SettingValue = value;
                    await settings.UpdateSettingAsync(existing);
                }

                return true;
            });
    }
}
