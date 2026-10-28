#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Repository;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The CLIENT half's outbound guard (BL-80 increment 5, D101 item 5), driven against a REAL MCP server on loopback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registering must not dial, and refreshing must not reach an internal address.</b> the predecessor platform's registration
    /// DIALLED the address and rendered the answer, which made the registration form an SSRF oracle: anyone who could
    /// reach it learned what was listening inside. So the first case here counts CONNECTIONS on a real listener and
    /// asserts the count is zero after a registration.
    /// </para>
    /// <para>
    /// <b>The deny set is not re-tested here.</b> Every address range is already driven by
    /// <c>WebhookSsrfGuardTests</c> and <c>FileIngestionGuardTests</c> against the same mechanism - MCP is the THIRD
    /// caller of <c>OutboundConnectGuard</c>, not a third copy of it, and a third table of addresses would be a third
    /// place for one range to be forgotten. What is asserted here is what is MCP's own: that a refresh goes through
    /// that guard at all, that the per-site allowlist is read from BOTH the configuration and the site setting, that an
    /// allowlisted host really does discover, and that a refused refresh leaves the row - and the API key - alone.
    /// </para>
    /// <para>
    /// The loopback server is a real MCP server built on the same SDK, so a discovery here is a protocol round-trip and
    /// not a stub returning a list. No Stomme host: these cases are about the wire, and a driven install would add
    /// forty seconds to say nothing more. The Stomme-side half - the five by-id operations, per site - is
    /// <c>McpSiteIsolationTests</c>.
    /// </para>
    /// </remarks>
    public class McpExternalServerSsrfGuardTests
    {
        private readonly ITestOutputHelper _output;

        public McpExternalServerSsrfGuardTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_new_registration_is_unverified_with_no_tools()
        {
            await using var server = await McpLoopbackServer.StartAsync();

            var registration = Register(server.Address);

            Assert.Equal(McpServerTrustState.Unverified, registration.TrustState);
            Assert.Empty(registration.DiscoveredTools.Tools());
            Assert.Null(registration.LastRefreshedOnUtc);

            // This is the FACTORY, so a zero here says only that the factory does not dial - which it plainly does not.
            // "Registering does not dial" is a claim about the SERVICE, and it is asserted where the service is real:
            // McpSiteIsolationTests drives McpRegistrationService.RegisterAsync against a counting listener.
            Assert.Equal(0, server.Connections);
        }

        [Fact]
        public async Task A_refresh_of_a_loopback_server_that_is_not_allowlisted_is_refused_before_anything_is_sent()
        {
            await using var server = await McpLoopbackServer.StartAsync();
            var registration = Register(server.Address);

            var result = await Connector(allowlist: null).DiscoverToolsAsync(registration, "the-api-key");

            _output.WriteLine($"refused: {result.Refused}; reason: {result.Failure}");
            Assert.True(result.Refused, "a loopback address must be refused by the outbound guard");
            Assert.False(result.Succeeded);

            // The connection never happened, so the bearer never left this process. That is the property - not merely
            // that the ANSWER was discarded.
            Assert.Equal(0, server.Connections);
            Assert.Empty(server.AuthorizationHeaders);

            // ...and the row is untouched: a refused refresh must not turn a server into a verified empty one.
            Assert.Equal(McpServerTrustState.Unverified, registration.TrustState);
            Assert.Empty(registration.DiscoveredTools.Tools());
        }

        [Fact]
        public async Task An_allowlisted_loopback_server_is_discovered_and_the_bearer_travels_with_the_request()
        {
            await using var server = await McpLoopbackServer.StartAsync();
            var registration = Register(server.Address);

            var result = await Connector(allowlist: "127.0.0.1").DiscoverToolsAsync(registration, "the-api-key");

            Assert.True(result.Succeeded, result.Failure);
            Assert.False(result.Refused);
            Assert.Equal(new[] { McpLoopbackServer.ToolName }, result.Tools!.Tools().Select(tool => tool.Name));
            Assert.True(server.Connections > 0);
            Assert.Contains("Bearer the-api-key", server.AuthorizationHeaders);

            // The non-vacuity control for the case above: the SAME registration, the SAME listener, refused when the
            // allowlist does not name the host. Only the allowlist differs between the two.
            Assert.True((await Connector(allowlist: null).DiscoverToolsAsync(registration, "the-api-key")).Refused);
        }

        [Fact]
        public async Task The_allowlist_names_ONE_host_and_does_not_open_every_denied_address()
        {
            await using var server = await McpLoopbackServer.StartAsync();
            var registration = Register(server.Address);

            // An entry for a host that is not this one must not admit this one.
            var result = await Connector(allowlist: "mcp.example.test").DiscoverToolsAsync(registration, null);

            Assert.True(result.Refused, "allowlisting one host must not allowlist loopback");
            Assert.Equal(0, server.Connections);
        }

        [Fact]
        public async Task The_site_setting_and_the_configuration_are_BOTH_read_and_neither_cancels_the_other()
        {
            // An install-wide entry and a site's own entry are both an operator's statement. The guard unions them, so
            // a site that names its own host does not lose the install's, and vice versa.
            var fromConfiguration = new McpEndpointGuard(new SettingsStub(null), Configuration("shared.example.test"));
            var fromSetting = new McpEndpointGuard(new SettingsStub("site.example.test"), Configuration(null));
            var both = new McpEndpointGuard(new SettingsStub("site.example.test"), Configuration("shared.example.test"));

            Assert.Equal(new[] { "shared.example.test" }, await fromConfiguration.AllowedHostsAsync(1));
            Assert.Equal(new[] { "site.example.test" }, await fromSetting.AllowedHostsAsync(1));
            Assert.Equal(new[] { "shared.example.test", "site.example.test" }, (await both.AllowedHostsAsync(1)).OrderBy(host => host));

            // A configured LIST and one delimited string mean the same thing, and an empty setting adds nothing.
            var asList = new McpEndpointGuard(new SettingsStub(""), ConfigurationList("a.example.test", "b.example.test"));
            Assert.Equal(new[] { "a.example.test", "b.example.test" }, (await asList.AllowedHostsAsync(1)).OrderBy(host => host));
            Assert.Equal(
                new[] { "a.example.test", "b.example.test" },
                (await new McpEndpointGuard(new SettingsStub("a.example.test, b.example.test"), Configuration(null)).AllowedHostsAsync(1)).OrderBy(host => host));
        }

        [Fact]
        public void An_address_that_is_not_an_http_url_cannot_be_REGISTERED_at_all()
        {
            // The scheme is refused where it can be refused ONCE: in the transport value object's constructor. So a
            // non-http address cannot be stored, which is why nothing downstream has to cope with one.
            Assert.Throws<ArgumentException>(() => McpServerTransport.Create("file:///etc/passwd"));
            Assert.Throws<ArgumentException>(() => McpServerTransport.Create("ftp://mcp.example.test/"));
            Assert.Throws<ArgumentException>(() => McpServerTransport.Create("/relative/mcp"));

            // The connector re-checks the scheme anyway, and that line is deliberately UNREACHABLE: the value object
            // runs on materialisation too, so a row carrying a bad scheme throws when it is read, not when it is
            // dialled. It is kept as the fail-closed answer if that ever stops being true, and is recorded here as
            // untested-because-unreachable rather than covered by a reflected row that no store can produce.
            Assert.False(Stomme.Infrastructure.Security.OutboundConnectGuard.IsPermittedScheme(new Uri("file:///etc/passwd")));
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static McpServerRegistrationAggregate Register(string endpoint) =>
            McpServerRegistrationAggregate.Register(
                Guid.NewGuid(), TenantId.From(Guid.NewGuid()), siteId: 1, "External", McpServerTransport.Create(endpoint), null, DateTime.UtcNow);

        private static McpClientConnector Connector(string? allowlist) =>
            new(new McpEndpointGuard(new SettingsStub(allowlist), Configuration(null)), NullLogger<McpClientConnector>.Instance);

        private static IConfigurationRoot Configuration(string? allowlist) =>
            new ConfigurationBuilder().AddInMemoryCollection(allowlist is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [McpEndpointGuard.AllowedHostsConfigurationKey] = allowlist }).Build();

        private static IConfigurationRoot ConfigurationList(params string[] hosts) =>
            new ConfigurationBuilder().AddInMemoryCollection(
                hosts.Select((host, index) => new KeyValuePair<string, string?>($"{McpEndpointGuard.AllowedHostsConfigurationKey}:{index}", host))).Build();

        /// <summary>The site setting, and nothing else. Every other member throws: nothing here may reach the store.</summary>
        private sealed class SettingsStub(string? allowedHosts) : ISettingRepository
        {
            public Task<Setting> GetSettingAsync(string entityName, int entityId, string settingName) =>
                Task.FromResult(settingName == McpEndpointGuard.AllowedHostsSettingName && allowedHosts is not null
                    ? new Setting { SettingName = settingName, SettingValue = allowedHosts }
                    : null!);

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName) => throw new NotSupportedException();

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName, int entityId) => throw new NotSupportedException();

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName1, int entityId1, string entityName2, int entityId2) => throw new NotSupportedException();

            public Task<Setting> AddSettingAsync(Setting setting) => throw new NotSupportedException();

            public Task<Setting> UpdateSettingAsync(Setting setting) => throw new NotSupportedException();

            public Task<Setting> GetSettingAsync(string entityName, int settingId) => throw new NotSupportedException();

            public Task DeleteSettingAsync(string entityName, int settingId) => throw new NotSupportedException();

            public Task DeleteSettingsAsync(string entityName, int entityId) => throw new NotSupportedException();

            public Task<IEnumerable<string>> GetEntityNamesAsync() => throw new NotSupportedException();

            public Task<IEnumerable<int>> GetEntityIdsAsync(string entityName) => throw new NotSupportedException();

            public Task<string> GetSettingValueAsync(string entityName, int entityId, string settingName, string defaultValue) => Task.FromResult(defaultValue);
        }

    }
}
