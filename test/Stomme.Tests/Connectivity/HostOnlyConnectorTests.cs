#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// A connector that reaches the HOST itself (a process, the file system) is created or
    /// edited by the Host role alone: an Administrator holding <c>connections:manage</c> is refused, the Host is admitted,
    /// and the catalog says which connectors are host-only so an editor can say so before the refusal.
    /// </summary>
    public sealed class HostOnlyConnectorTests
    {
        [Fact]
        public async Task An_administrator_is_refused_and_the_host_is_admitted()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("shell") { HostOnly = true, AuthType = AuthenticationType.None, DisplayName = "Shell" });
            Assert.Contains(RoleNames.Admin, harness.User.Roles);
            Assert.DoesNotContain(RoleNames.Host, harness.User.Roles);

            var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.SendAsync(new CreateConnectionCommand("Box", "shell", "host=local")));
            Assert.Contains("Host", refused.Message);
            Assert.Empty(await harness.SendAsync(new ListConnectionsQuery()));

            harness.User.RoleSet.Add(RoleNames.Host);
            var id = await harness.SendAsync(new CreateConnectionCommand("Box", "shell", "host=local"));
            Assert.Single(await harness.SendAsync(new ListConnectionsQuery()));

            // An edit is gated the same way, whoever created the connection.
            harness.User.RoleSet.Remove(RoleNames.Host);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.SendAsync(new UpdateConnectionCommand(id, "Box 2", "host=local2")));
            harness.User.RoleSet.Add(RoleNames.Host);
            await harness.SendAsync(new UpdateConnectionCommand(id, "Box 2", "host=local2"));
            Assert.Equal("Box 2", (await harness.SendAsync(new ListConnectionsQuery()))[0].Name);
        }

        [Fact]
        public async Task The_catalog_says_which_connectors_are_host_only()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new IConnector[]
            {
                new ProbeConnector("shell") { HostOnly = true, DisplayName = "Shell" },
                new ProbeConnector("rest") { DisplayName = "REST" },
            });

            var connectors = await harness.SendAsync(new ListConnectorsQuery());

            Assert.Equal(new[] { ("rest", false), ("shell", true) }, connectors.Select(connector => (connector.ConnectorType, connector.HostOnly)));
        }
    }
}
