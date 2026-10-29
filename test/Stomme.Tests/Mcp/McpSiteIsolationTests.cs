#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Models;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// THE SITE IS THE UNIT AT <c>/mcp/v1</c> TOO: two sites in ONE tenant, and the
    /// endpoint answers for the site the REQUEST resolved - not for whichever site composed something first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test increment 4 left a live mutant for.</b> The bridge resolves the catalog from
    /// <c>RequestContext.Services</c>; swapping that for the server's own provider changed nothing measurable, because
    /// a STATELESS transport builds its server out of the request's scope and the two are the same object. So nothing
    /// in that increment could tell a per-request catalog from a per-host one. This suite can: the same user, the same
    /// HTTP host, two aliases - and the answers differ only by which site the request resolved to.
    /// </para>
    /// <para>
    /// <b>NON-VACUITY IS THE DESIGN</b>, borrowed from <c>LiftedRequiresSiteIsolationTests</c>. Site B's empty tool list
    /// would prove nothing if the user simply had no role there, so the role membership is asserted on BOTH sites
    /// first; and the loop is closed by granting the key on site B and watching the SAME request start answering. Only
    /// the permission row changes between those two calls.
    /// </para>
    /// <para>
    /// PostgreSQL and RavenDB - one relational, one document, both FAIL-NOT-SKIP when unreachable. SQLite is
    /// deliberately absent: the claim is about the query the provider runs for the ledger and the registrations, and
    /// the relational leg is worth more against the real thing.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class McpSiteIsolationTests
    {
        private const string HostA = "localhost";
        private const string HostB = "mcpsiteb.localhost";

        /// <summary>A VIRTUAL-FOLDER alias on site B - D101 item 10's documented route limitation, driven.</summary>
        private const string FolderAlias = "localhost/mcpfolder";

        private readonly ITestOutputHelper _output;

        public McpSiteIsolationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_endpoint_answers_for_the_site_the_request_resolved_and_audits_it_there(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, HostA, _output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // ---- 1. TWO SITES, ONE TENANT - the default install shape -----------------------------------------
                var aliasA = await GateDrive.AliasAsync(app, HostA);
                await CreateSecondSiteAsync(app);
                var aliasB = await GateDrive.AliasAsync(app, HostB);

                _output.WriteLine($"[{storeName}] site A = {aliasA.SiteId} (SiteKey {aliasA.SiteKey}); site B = {aliasB.SiteId} (SiteKey {aliasB.SiteKey})");
                Assert.Equal(aliasA.TenantId, aliasB.TenantId);     // one tenant, one database - the isolation claim
                Assert.NotEqual(aliasA.SiteId, aliasB.SiteId);

                // The per-site rows are materialised LAZILY on the first read of that site's module definitions
                // (LiftedRequiresSiteIsolationTests measured this); without it site B carries no rows at all and its
                // refusals below would be about the missing seeding rather than about the grant.
                await GateDrive.GateTestDefinitionAsync(app, HostB);

                // ---- 2. ONE user, in a role of the SAME NAME on BOTH sites ----------------------------------------
                await GateDrive.EnsureRoleAsync(app, HostA, GateDrive.GateRole);
                await GateDrive.EnsureRoleAsync(app, HostB, GateDrive.GateRole);

                var onA = await GateDrive.AddUserAsync(app, HostA, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                var onB = await GateDrive.AddUserAsync(app, HostB, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                Assert.Equal(onA.UserId, onB.UserId);               // the SAME user, not a namesake

                var rolesOnA = await GateDrive.RolesAsync(app, HostA, onA.UserId);
                var rolesOnB = await GateDrive.RolesAsync(app, HostB, onA.UserId);
                _output.WriteLine($"[{storeName}] user {onA.UserId} roles on A = {rolesOnA}; on B = {rolesOnB}");
                Assert.Contains(GateDrive.GateRole, rolesOnA);
                Assert.Contains(GateDrive.GateRole, rolesOnB);      // B's empty catalog cannot be a missing role
                Assert.DoesNotContain(RoleNames.Admin, rolesOnB);

                // ---- 3. THE GRANT, ON SITE A ONLY ------------------------------------------------------------------
                await GrantOnSiteAsync(app, HostA);
                Assert.DoesNotContain(await GateDrive.RowsAsync(app, HostB, "gatetest", "manage"), row => row.RoleName == GateDrive.GateRole);

                // ---- 4. THE SAME USER, SIGNED IN ON EACH ALIAS -----------------------------------------------------
                using var clientA = GateDrive.NewClient(app, HostA);
                using var clientB = GateDrive.NewClient(app, HostB);
                var cookiesA = await GateDrive.LoginAsync(app, clientA, HostA, GateDrive.GateUsername, GateDrive.GatePassword, _output);
                var cookiesB = await GateDrive.LoginAsync(app, clientB, HostB, GateDrive.GateUsername, GateDrive.GatePassword, _output);

                // ---- 5. THE CATALOG DIFFERS BY ALIAS ----------------------------------------------------------------
                var toolsOnA = await McpRpc.ToolNamesAsync(clientA, cookiesA, HostA, _output, storeName + " A tools");
                var toolsOnB = await McpRpc.ToolNamesAsync(clientB, cookiesB, HostB, _output, storeName + " B tools");
                _output.WriteLine($"[{storeName}] A offers [{string.Join(", ", toolsOnA)}]; B offers [{string.Join(", ", toolsOnB)}]");
                Assert.Contains(McpTestContributor.Echo, toolsOnA);
                Assert.Empty(toolsOnB);

                // ...and so do the resources, over the same endpoint in the same host.
                var resourcesOnA = await McpRpc.CallAsync(clientA, cookiesA, HostA, "resources/list", null, _output, storeName + " A resources");
                var resourcesOnB = await McpRpc.CallAsync(clientB, cookiesB, HostB, "resources/list", null, _output, storeName + " B resources");
                Assert.NotEmpty(resourcesOnA?["result"]?["resources"]?.AsArray() ?? new JsonArray());
                Assert.Empty(resourcesOnB?["result"]?["resources"]?.AsArray() ?? new JsonArray());

                // ---- 6. AND SO DOES A CALL - the handler runs for A and NOT for B -----------------------------------
                var ran = McpTestContributor.Invocations;
                var calledOnA = await McpRpc.CallAsync(clientA, cookiesA, HostA, "tools/call", Echo("from site A"), _output, storeName + " A call");
                Assert.NotEqual(true, calledOnA?["result"]?["isError"]?.GetValue<bool>());
                Assert.Equal(ran + 1, McpTestContributor.Invocations);

                var calledOnB = await McpRpc.CallAsync(clientB, cookiesB, HostB, "tools/call", Echo("from site B"), _output, storeName + " B call");
                Assert.True(calledOnB?["result"]?["isError"]?.GetValue<bool>());
                Assert.Equal(ran + 1, McpTestContributor.Invocations);   // B's call reached no handler at all

                // ---- 7. THE LEDGER IS PER SITE TOO ------------------------------------------------------------------
                // Each site's rows carry ITS SiteId, and neither site's ledger carries the other's call. A catalog
                // serving a captured site would have written both rows against one id.
                var ledgerA = await LedgerAsync(app, HostA, aliasA.SiteId);
                var ledgerB = await LedgerAsync(app, HostB, aliasB.SiteId);
                _output.WriteLine($"[{storeName}] ledger A = {Describe(ledgerA)}; ledger B = {Describe(ledgerB)}");

                Assert.All(ledgerA, row => Assert.Equal(aliasA.SiteId, row.SiteId));
                Assert.All(ledgerB, row => Assert.Equal(aliasB.SiteId, row.SiteId));
                Assert.Contains(ledgerA, row => row.UserId == onA.UserId && row.Verb == McpAuditVerb.ToolCall
                    && row.Subject == McpTestContributor.Echo && row.Outcome == McpAuditOutcome.Succeeded);
                Assert.Contains(ledgerB, row => row.UserId == onA.UserId && row.Verb == McpAuditVerb.ToolCall
                    && row.Subject == McpTestContributor.Echo && row.Outcome == McpAuditOutcome.Denied);
                Assert.DoesNotContain(ledgerA, row => row.Outcome == McpAuditOutcome.Denied);
                Assert.DoesNotContain(ledgerB, row => row.Outcome == McpAuditOutcome.Succeeded);

                // ---- 8. THE CLOSING CONTROL: grant on B, and B starts answering --------------------------------------
                // Without this, B's empty catalog could be anything about site B - a broken alias, a failed login, an
                // unseeded module definition. Between these two calls, ONLY the permission row changes.
                await GrantOnSiteAsync(app, HostB);

                var toolsOnBNow = await McpRpc.ToolNamesAsync(clientB, cookiesB, HostB, _output, storeName + " B tools (granted)");
                Assert.Contains(McpTestContributor.Echo, toolsOnBNow);

                var calledOnBNow = await McpRpc.CallAsync(clientB, cookiesB, HostB, "tools/call", Echo("from site B, granted"), _output, storeName + " B call (granted)");
                Assert.NotEqual(true, calledOnBNow?["result"]?["isError"]?.GetValue<bool>());
                Assert.Equal(ran + 2, McpTestContributor.Invocations);

                // ...and site A is untouched by site B's grant.
                Assert.Contains(McpTestContributor.Echo, await McpRpc.ToolNamesAsync(clientA, cookiesA, HostA, _output, storeName + " A tools (after B granted)"));

                // ---- 8b. THE BUILT-IN stomme://site RESOURCE NAMES THE SITE THAT ASKED --------------------------
                // MEASURED: every other MCP drive installs ONE site, whose SiteId is 1 - so a reader that ignored the
                // resolved alias and hardcoded site 1 SURVIVED all of them. This is the only place two sites exist, so
                // it is the only place that reader can be wrong in a way a test can see.
                await GrantOnSiteAsync(app, HostA, McpPermissions.Resource, "view");
                await GrantOnSiteAsync(app, HostB, McpPermissions.Resource, "view");

                var siteOnA = await SiteResourceAsync(clientA, cookiesA, HostA, storeName + " A site resource");
                var siteOnB = await SiteResourceAsync(clientB, cookiesB, HostB, storeName + " B site resource");
                _output.WriteLine($"[{storeName}] stomme://site on A = {siteOnA?.ToJsonString()}; on B = {siteOnB?.ToJsonString()}");

                Assert.Equal(aliasA.SiteId, siteOnA?["SiteId"]?.GetValue<int>());
                Assert.Equal(aliasB.SiteId, siteOnB?["SiteId"]?.GetValue<int>());
                Assert.NotEqual(siteOnA?["Name"]?.GetValue<string>(), siteOnB?["Name"]?.GetValue<string>());

                // ---- 9. A VIRTUAL-FOLDER ALIAS HAS NO MCP ENDPOINT -----------------------------------
                // MapMcp maps at the ROOT, so a tenant reachable only at a virtual folder is not an MCP server - the
                // same limitation D61 measured for the OpenIddict authority. Driven here so the documentation is a
                // statement about this host rather than about the SDK.
                //
                // CORRECTED against D101's gate list, which expected 404: a POST to an unmapped path is claimed by the
                // SSR catch-all, which rejects the JSON content type with 400. What is asserted is what can be: the
                // folder path does NOT serve the catalog, on a request whose ROOT path does.
                await AddFolderAliasAsync(app, aliasB);
                var folder = await McpRpc.PostAsync(clientA, cookiesA, HostA, McpRpc.Body("tools/list", null), _output,
                    storeName + " folder alias", "/mcpfolder" + McpRpc.Endpoint);
                Assert.NotEqual(HttpStatusCode.OK, folder.Status);
                Assert.DoesNotContain(McpTestContributor.Echo, folder.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Every_by_id_operation_refuses_another_sites_registration_with_the_same_not_found_answer(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, HostA, _output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);
            await using var external = await McpLoopbackServer.StartAsync();

            try
            {
                var aliasA = await GateDrive.AliasAsync(app, HostA);
                await CreateSecondSiteAsync(app);
                var aliasB = await GateDrive.AliasAsync(app, HostB);
                Assert.Equal(aliasA.TenantId, aliasB.TenantId);     // ONE tenant - the shape that made the predecessor platform reachable

                // ---- REGISTERING DOES NOT DIAL, through the REAL service ----------------------------
                // Asserted on the LISTENER, not on the return value: the predecessor platform dialled on registration and rendered the
                // answer, which is an SSRF oracle whatever the row says afterwards.
                //
                // THE ALLOWLIST IS SET FIRST, AND THAT IS THE POINT. Measured: with loopback NOT allowlisted, a planted
                // dial inside RegisterAsync SURVIVED this assertion - the outbound guard refused the connection, so the
                // listener's counter stayed 0 and a registration that dialled looked exactly like one that did not.
                // Allowlisting site A up front means a dial WOULD arrive, so the zero below is a statement about the
                // service rather than about the guard. (The guard stopping it too is defence in depth, and welcome -
                // but it is not what this line claims.)
                await AllowLoopbackAsync(app, HostA, aliasA.SiteId);

                var registered = await InSiteAsync(app, HostA, service => service.RegisterAsync("External A", external.Address, null));
                Assert.NotNull(registered);
                Assert.Equal(McpServerTrustState.Unverified, registered!.TrustState);
                Assert.Empty(registered.DiscoveredTools.Tools());
                Assert.Equal(0, external.Connections);

                // A site may not register one name twice; another site may use the same name freely.
                Assert.Null(await InSiteAsync(app, HostA, service => service.RegisterAsync("External A", external.Address, null)));
                var onB = await InSiteAsync(app, HostB, service => service.RegisterAsync("External A", external.Address, null));
                Assert.NotNull(onB);
                Assert.NotEqual(registered.Id, onB!.Id);

                // ---- THE LIST IS THIS SITE'S ---------------------------------------------------------------------
                var listA = await InSiteAsync(app, HostA, async service => (await service.ListAsync()).Select(row => row.Id).ToList());
                var listB = await InSiteAsync(app, HostB, async service => (await service.ListAsync()).Select(row => row.Id).ToList());
                Assert.Equal(new[] { registered.Id }, listA);
                Assert.Equal(new[] { onB.Id }, listB);

                // ---- ALL FIVE BY-ID OPERATIONS, FROM THE WRONG SITE ----------------------------------------------
                // The answer for "site A's registration" must be the SAME as for a Guid that exists nowhere, or the id
                // space tells a site B administrator what site A has.
                var foreign = registered.Id;
                var nowhere = Guid.NewGuid();
                foreach (var (name, operation) in ByIdOperations())
                {
                    var wrongSite = await InSiteAsync(app, HostB, service => operation(service, foreign));
                    var unknown = await InSiteAsync(app, HostB, service => operation(service, nowhere));
                    _output.WriteLine($"[{storeName}] {name}: another site -> {wrongSite.Outcome}; nowhere -> {unknown.Outcome}");

                    Assert.Equal(McpRegistrationOutcome.NotFound, wrongSite.Outcome);
                    Assert.Equal(unknown, wrongSite);               // the reason too: ONE answer, not two shaped alike
                }

                // ...and GetAsync, the read those five go through, answers null both ways.
                Assert.Null(await InSiteAsync(app, HostB, service => service.GetAsync(foreign)));
                Assert.Null(await InSiteAsync(app, HostB, service => service.GetAsync(nowhere)));

                // Nothing above reached the external server: a refused by-id refresh must not dial either.
                Assert.Equal(0, external.Connections);

                // ---- AND THE SAME OPERATIONS WORK FROM THE OWNING SITE -------------------------------------------
                // The non-vacuity control. Without it, every NotFound above could be a service that answers NotFound to
                // everything - which is what a wrong-site check that is too TIGHT would also look like.
                Assert.Equal(McpRegistrationOutcome.Done, (await InSiteAsync(app, HostA, service => service.RefreshAsync(foreign))).Outcome);
                Assert.True(external.Connections > 0);

                var refreshed = await InSiteAsync(app, HostA, service => service.GetAsync(foreign));
                Assert.NotNull(refreshed);
                Assert.Equal(McpServerTrustState.Verified, refreshed!.TrustState);
                Assert.Equal(new[] { McpLoopbackServer.ToolName }, refreshed.DiscoveredTools.Tools().Select(tool => tool.Name));

                // ---- AND THE TOOL SOURCE OFFERS IT TO SITE A AND TO NOBODY ELSE ----------------------------------
                Assert.Equal(new[] { McpLoopbackServer.ToolName }, (await ToolSourceAsync(app, HostA)).Select(tool => tool.Name));
                Assert.Empty(await ToolSourceAsync(app, HostB));

                // Disabled offers nothing, though the row and its discovered tools remain.
                Assert.Equal(McpRegistrationOutcome.Done, (await InSiteAsync(app, HostA, service => service.DisableAsync(foreign))).Outcome);
                Assert.Empty(await ToolSourceAsync(app, HostA));

                // BOTH layers of "disabled offers nothing" are pinned SEPARATELY, because each masks the other: with
                // only the composed assertion above, removing EITHER the store filter or the aggregate rule left the
                // suite green (measured - two surviving mutants). The store contract:
                var enabledRows = await RegistrationsAsync(app, HostA, repository => repository.ListEnabledAsync(aliasA.SiteId));
                var allRows = await RegistrationsAsync(app, HostA, repository => repository.ListBySiteAsync(aliasA.SiteId));
                Assert.Empty(enabledRows);
                Assert.NotEmpty(allRows);

                // ...and the aggregate rule:
                var disabled = await InSiteAsync(app, HostA, service => service.GetAsync(foreign));
                Assert.NotEmpty(disabled!.DiscoveredTools.Tools());      // the row KEPT what it discovered...
                Assert.Empty(disabled.ExposedTools());                   // ...and offers none of it while disabled

                Assert.Equal(McpRegistrationOutcome.Done, (await InSiteAsync(app, HostA, service => service.EnableAsync(foreign))).Outcome);
                Assert.NotEmpty(await ToolSourceAsync(app, HostA));

                // ---- THE KEY IS NAMED BY THE ROW AND GOES WITH IT ------------------------------------------------
                Assert.Equal(McpRegistrationOutcome.Done, (await InSiteAsync(app, HostA, service => service.SetApiKeyAsync(foreign, "a-real-key"))).Outcome);
                Assert.True((await InSiteAsync(app, HostA, service => service.GetAsync(foreign)))!.HasApiKey);

                // The derived name is one the secret store will ACCEPT - two files that have to agree, and a mismatch
                // would only ever show up the first time an operator saved a key.
                Assert.True(SiteSecretStore.IsValidReferenceKey(refreshed.ApiKeySecretName()));
                Assert.Equal(SiteSecretStatus.Found,
                    (await SecretAsync(app, HostA, aliasA.SiteId, refreshed.ApiKeySecretName())).Status);

                Assert.Equal(McpRegistrationOutcome.Done, (await InSiteAsync(app, HostA, service => service.RemoveAsync(foreign))).Outcome);
                Assert.Null(await InSiteAsync(app, HostA, service => service.GetAsync(foreign)));

                // ...and removing the registration removed its secret. A secret whose row is gone can never be reached
                // again, so leaving it would be a protected value nothing reads and nothing will ever clean up.
                Assert.Equal(SiteSecretStatus.NotFound,
                    (await SecretAsync(app, HostA, aliasA.SiteId, refreshed.ApiKeySecretName())).Status);

                // Site B's own registration is untouched by every one of those.
                Assert.NotNull(await InSiteAsync(app, HostB, service => service.GetAsync(onB.Id)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>The five by-id operations D101 item 4 names, each as one call on the service.</summary>
        private static IEnumerable<(string Name, Func<McpRegistrationService, Guid, Task<McpRegistrationResult>> Operation)> ByIdOperations() =>
        [
            ("refresh", (service, id) => service.RefreshAsync(id)),
            ("enable", (service, id) => service.EnableAsync(id)),
            ("disable", (service, id) => service.DisableAsync(id)),
            ("remove", (service, id) => service.RemoveAsync(id)),
            ("secret", (service, id) => service.SetApiKeyAsync(id, "a-key")),
        ];

        private static Task<IReadOnlyList<McpServerRegistrationAggregate>> RegistrationsAsync(
            WebApplication app, string host, Func<IMcpServerRegistrationRepository, Task<IReadOnlyList<McpServerRegistrationAggregate>>> work) =>
            GateDrive.InTenantAsync(app, host, (sp, _) => work(sp.GetRequiredService<IMcpServerRegistrationRepository>()));

        private static Task<T> InSiteAsync<T>(WebApplication app, string host, Func<McpRegistrationService, Task<T>> work) =>
            GateDrive.InTenantAsync(app, host, (sp, _) => work(sp.GetRequiredService<McpRegistrationService>()));

        private static Task<IReadOnlyList<Stomme.Mcp.McpClientToolDescriptor>> ToolSourceAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, (sp, _) => sp.GetRequiredService<Stomme.Mcp.IMcpClientToolSource>().GetAvailableToolsAsync());

        private static Task<SiteSecretResult> SecretAsync(WebApplication app, string host, int siteId, string referenceKey) =>
            GateDrive.InTenantAsync(app, host, (sp, _) => sp.GetRequiredService<ISiteSecretStore>().GetAsync(siteId, referenceKey));

        /// <summary>Allowlists loopback for ONE site, through the real site setting the guard reads.</summary>
        private static Task<bool> AllowLoopbackAsync(WebApplication app, string host, int siteId) =>
            GateDrive.InTenantAsync(app, host, async (sp, _) =>
            {
                await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = McpEndpointGuard.AllowedHostsSettingName,
                    SettingValue = "127.0.0.1",
                    IsPrivate = false,
                });

                return true;
            });

        /// <summary>Reads <c>stomme://site</c> over the endpoint and returns its JSON payload.</summary>
        private async Task<JsonNode?> SiteResourceAsync(System.Net.Http.HttpClient client, System.Net.CookieContainer cookies, string host, string label)
        {
            var read = await McpRpc.CallAsync(client, cookies, host, "resources/read",
                new JsonObject { ["uri"] = Stomme.Modules.Mcp.Contributions.StommeSiteContributor.SiteUri }, _output, label);
            return JsonNode.Parse(read?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}");
        }

        private static JsonObject Echo(string text) => new()
        {
            ["name"] = McpTestContributor.Echo,
            ["arguments"] = new JsonObject { ["text"] = text },
        };

        private static Task<List<McpAuditEntryAggregate>> LedgerAsync(WebApplication app, string host, int siteId) =>
            GateDrive.InTenantAsync(app, host, async (sp, _) =>
                (await sp.GetRequiredService<IMcpAuditRepository>().ListBySiteAsync(siteId)).ToList());

        private static string Describe(IEnumerable<McpAuditEntryAggregate> rows) =>
            "[" + string.Join(", ", rows.Select(row => $"site {row.SiteId} user {row.UserId} {row.Verb} {row.Subject} {row.Outcome}")) + "]";

        /// <summary>A second site on the SAME tenant, through the call the Host's "Add Site" page makes.</summary>
        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "MCP Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = true
                });

                return site;
            });

        /// <summary>A second, VIRTUAL-FOLDER alias for site B: <c>localhost/mcpfolder</c>.</summary>
        private static Task<bool> AddFolderAliasAsync(WebApplication app, Alias siteB) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = FolderAlias,
                    TenantId = alias.TenantId,
                    SiteId = siteB.SiteId,
                    IsDefault = false
                });

                return true;
            });

        /// <summary>Grants <c>gatetest:manage</c> to the gate role on the site the given alias resolves to.</summary>
        private static Task<bool> GrantOnSiteAsync(WebApplication app, string host) =>
            GrantOnSiteAsync(app, host, "gatetest", "manage");

        /// <summary>Grants one key to the gate role on the site the given alias resolves to.</summary>
        private static Task<bool> GrantOnSiteAsync(WebApplication app, string host, string resource, string operation) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, resource, operation))
                    .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                    .ToList();
                submitted.Add(new Permission(alias.SiteId, resource, PermissionKeys.KeyEntityId, operation, GateDrive.GateRole, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, resource, PermissionKeys.KeyEntityId, submitted);
                return true;
            });
    }
}
