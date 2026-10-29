#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Domain;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The MCP endpoint itself, driven over real HTTP with raw JSON-RPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first thing it drives is the thing the design said to drive first:</b> that the SDK's per-request scope sees
    /// Stomme's resolved alias. The SDK owns the scope the handlers run in; Stomme's tenant resolution puts the
    /// alias in a SCOPED SiteState. If those are not the same scope the catalog's caller check returns null and
    /// everything denies - the safe direction, and completely silent. So the FIRST assertion is that an authenticated
    /// holder can list a tool: a green "denied" would otherwise look exactly like a green "gated".
    /// </para>
    /// <para>
    /// Raw JSON-RPC rather than the SDK client: this asserts what goes over the wire, including the refusals, and does
    /// not need the client half. The SDK client round-trip is <c>McpProtocolHandshakeTests</c>, still to come.
    /// </para>
    /// </remarks>
    [Trait("Category", "ExternalDatabase")]
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpEndpointDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpEndpointDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_endpoint_serves_the_catalog_to_a_holder_refuses_everyone_else_and_audits_what_it_did(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // The other direction of the disabled-surface assertion below: with the switch ON the route IS there.
                Assert.NotEmpty(McpRoutes(app));

                // The catalog cannot be served from a captured ROOT provider: it is scoped, and so is the SiteState it
                // reads the alias from. A handler that held one could not get a catalog at all - which is why the
                // bridge takes RequestContext.Services. Asserted rather than assumed.
                Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<Stomme.Mcp.IMcpServerCatalog>());

                // ...and the PRECONDITION that makes the bridge's RequestContext.Services line unobservable is pinned
                // here rather than left in a comment. MEASURED twice (increments 4 and 5): swapping that line for the
                // server's own provider survives BOTH this suite and the per-site drive, because a STATELESS transport
                // builds its server out of the request's scope, so the two are the same object - an equivalent mutant,
                // not a hole in a test. The equivalence holds only while Stateless is true. If it is ever turned off,
                // this assertion goes red FIRST and the bridge's line becomes load-bearing and needs a drive of its own.
                Assert.True(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpServerTransportOptions>>().Value.Stateless,
                    "The HTTP transport is no longer stateless: the SDK now reuses a server across requests, so the catalog "
                    + "the bridge resolves is no longer necessarily this request's. Re-drive the bridge's scope before changing this.");

                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);

                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, _output);

                // ---- THE ONE THAT MATTERS FIRST: the handler's scope sees the alias -------------------------------
                // A host whose SDK scope did not carry Stomme's resolved site would answer this with an EMPTY list,
                // and every other assertion below would still pass. See the class remarks.
                var tools = await RpcAsync(hostClient, hostCookies, "tools/list", null, storeName + " host tools");
                var names = tools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()).ToList() ?? new();
                _output.WriteLine($"[{storeName}] tools/list -> {string.Join(", ", names)}");
                Assert.Contains(McpTestContributor.Echo, names);
                Assert.Contains(McpTestContributor.Confirm, names);
                Assert.DoesNotContain(McpTestContributor.Never, names);

                // ---- a template is listed as a TEMPLATE, not as a resource a client would fetch literally ----------
                var resources = await RpcAsync(hostClient, hostCookies, "resources/list", null, storeName + " host resources");
                var uris = resources?["result"]?["resources"]?.AsArray().Select(item => item?["uri"]?.GetValue<string>()).ToList() ?? new();
                Assert.Contains(McpTestContributor.Greeting, uris);

                // The framework's OWN contributor is published over the same endpoint, and reads: the
                // built-in resource is gated on mcp:view, which the MCP module's ModuleInfo declares and the installer
                // seeds to Administrators - so the host account gets it and a registered-only user does not.
                Assert.Contains(Stomme.Modules.Mcp.Contributions.StommeSiteContributor.SiteUri, uris);

                var site = await RpcAsync(hostClient, hostCookies, "resources/read",
                    new JsonObject { ["uri"] = Stomme.Modules.Mcp.Contributions.StommeSiteContributor.SiteUri }, storeName + " host site resource");
                var siteJson = JsonNode.Parse(site?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "{}");
                _output.WriteLine($"[{storeName}] stomme://site -> {siteJson?.ToJsonString()}");
                Assert.Equal(siteId, siteJson?["SiteId"]?.GetValue<int>());
                Assert.False(string.IsNullOrWhiteSpace(siteJson?["RenderMode"]?.GetValue<string>()));

                var templates = await RpcAsync(hostClient, hostCookies, "resources/templates/list", null, storeName + " host templates");
                var templateUris = templates?["result"]?["resourceTemplates"]?.AsArray().Select(item => item?["uriTemplate"]?.GetValue<string>()).ToList() ?? new();
                Assert.Equal(new[] { McpTestContributor.ItemTemplate }, templateUris);

                // ---- a call runs, and reads through the template's arguments ---------------------------------------
                var ran = McpTestContributor.Invocations;
                var called = await RpcAsync(hostClient, hostCookies, "tools/call",
                    new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject { ["text"] = "over the wire" } }, storeName + " host call");
                Assert.NotEqual(true, called?["result"]?["isError"]?.GetValue<bool>());
                Assert.Contains("over the wire", called?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>() ?? "");
                Assert.Equal(ran + 1, McpTestContributor.Invocations);

                var read = await RpcAsync(hostClient, hostCookies, "resources/read",
                    new JsonObject { ["uri"] = "mcptest://item/7" }, storeName + " host read");
                Assert.Equal("item 7", read?["result"]?["contents"]?.AsArray()[0]?["text"]?.GetValue<string>());

                // ---- a caller without the key SEES NOTHING and can invoke nothing, over the same endpoint ----------
                ran = McpTestContributor.Invocations;
                var plainTools = await RpcAsync(plainClient, plainCookies, "tools/list", null, storeName + " plain tools");
                Assert.Empty(plainTools?["result"]?["tools"]?.AsArray() ?? new JsonArray());

                // The framework's own resource is gated like any other: a registered-only user sees nothing at all.
                var plainResources = await RpcAsync(plainClient, plainCookies, "resources/list", null, storeName + " plain resources");
                Assert.Empty(plainResources?["result"]?["resources"]?.AsArray() ?? new JsonArray());

                var plainCall = await RpcAsync(plainClient, plainCookies, "tools/call",
                    new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject() }, storeName + " plain call");
                Assert.True(plainCall?["result"]?["isError"]?.GetValue<bool>());
                Assert.Equal(ran, McpTestContributor.Invocations);

                // An unknown tool gives a HOLDER the same answer a denied one gives a non-holder: no oracle.
                var unknown = await RpcAsync(hostClient, hostCookies, "tools/call",
                    new JsonObject { ["name"] = "no_such_tool", ["arguments"] = new JsonObject() }, storeName + " host unknown");
                Assert.Equal(
                    plainCall?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>(),
                    unknown?["result"]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>());

                // ---- anonymous is refused by the ROUTE, before any of this --------------------------------------
                // 401, and it is the ONE path in this host that answers a cookie challenge that way. 403 is what
                // ConfigureApplicationCookie answers everywhere else, deliberately (an HTML login page is
                // useless in a fetch()). But 403 means "you may not" and 401 means "authenticate" - an MCP client
                // cannot tell "sign in" from "this is not for you" out of a 403, and 401 is what the protocol's own
                // authorization flow keys on. The carve-out is endpoint METADATA, not a path match, so it cannot
                // drift from a configured Mcp:Server:Path.
                var anonymous = await PostAsync(anonymousClient, null, Body("tools/list", null), storeName + " anonymous");
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
                Assert.DoesNotContain(McpTestContributor.Echo, anonymous.Body);
                Assert.Equal(ran, McpTestContributor.Invocations);

                // ...and the carve-out is EXACTLY one endpoint: every other unauthenticated request still gets this
                // host's 403, so that decision is intact rather than quietly widened.
                // A genuinely GATED endpoint - api/McpTest is deliberately ungated, so it answers 200 with an empty
                // list and would prove nothing.
                var elsewhere = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/GateTest/manage", _output, storeName + " anonymous elsewhere");
                Assert.Equal(HttpStatusCode.Forbidden, elsewhere.Status);

                // ---- and the ledger holds what the endpoint did ---------------------------------------------------
                var ledger = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IMcpAuditRepository>().ListBySiteAsync(siteId)).ToList());
                Assert.Contains(ledger, row => row.UserId == hostUserId && row.Verb == McpAuditVerb.ToolCall && row.Subject == McpTestContributor.Echo && row.Outcome == McpAuditOutcome.Succeeded);
                Assert.Contains(ledger, row => row.UserId == hostUserId && row.Verb == McpAuditVerb.ResourceRead && row.Subject == "mcptest://item/7" && row.Outcome == McpAuditOutcome.Succeeded);
                Assert.Contains(ledger, row => row.Verb == McpAuditVerb.ToolCall && row.Subject == "no_such_tool" && row.Outcome == McpAuditOutcome.Denied);
                Assert.All(ledger, row => Assert.Equal(siteId, row.SiteId));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_server_half_off_the_route_does_not_exist()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // The ENDPOINT TABLE, not the status code. A POST to an unmapped path does NOT answer 404 here: the
                // SSR catch-all claims it and rejects the JSON content type with 400 ("The request has an incorrect
                // Content-type"), which is indistinguishable from a mapped-but-unhappy MCP endpoint. Measured, and the
                // reason this assertion is not the obvious one.
                // By ROUTE PATTERN, not by display name: the display names carry the fixture's own api/McpTest
                // CONTROLLER, which exists either way, and a name match called it a leak on the first run.
                Assert.Empty(McpRoutes(app));

                // Non-vacuity: the table IS populated, so "no MCP route" means something.
                Assert.NotEmpty(app.Services.GetRequiredService<EndpointDataSource>().Endpoints);

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var response = await PostAsync(client, cookies, Body("tools/list", null), "off");
                Assert.NotEqual(HttpStatusCode.OK, response.Status);
                Assert.DoesNotContain(McpTestContributor.Echo, response.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Every route mapped under the MCP path, by PATTERN - the only exact way to ask.</summary>
        private static List<string> McpRoutes(WebApplication app) =>
            app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText ?? "")
                .Where(pattern => pattern.TrimStart('/').StartsWith("mcp/v1", StringComparison.OrdinalIgnoreCase))
                .ToList();

        // ---- raw JSON-RPC over the streamable-HTTP transport (McpRpc - shared with McpSiteIsolationTests) ----

        private static JsonObject Body(string method, JsonObject? parameters) => McpRpc.Body(method, parameters);

        private Task<JsonNode?> RpcAsync(HttpClient client, CookieContainer cookies, string method, JsonObject? parameters, string label) =>
            McpRpc.CallAsync(client, cookies, Host, method, parameters, _output, label);

        private Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, CookieContainer? cookies, JsonObject body, string label) =>
            McpRpc.PostAsync(client, cookies, Host, body, _output, label);
    }
}
