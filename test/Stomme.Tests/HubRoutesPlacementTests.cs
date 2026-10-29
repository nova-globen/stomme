using System.Runtime.CompilerServices;
using Stomme.Transport.Realtime;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The Client's live notification feed must name the hub route, and the Client cannot see
    /// <c>Stomme.Server</c>. <see cref="HubRoutes"/> therefore lives in <c>Stomme.Shared</c> - under its original
    /// namespace, so every consumer compiles unchanged - and the Server forwards the type for binaries built against
    /// the old location.
    /// </summary>
    public sealed class HubRoutesPlacementTests
    {
        [Fact]
        public void HubRoutes_lives_in_the_shared_assembly_with_its_routes_unchanged()
        {
            Assert.Equal("Stomme.Shared", typeof(HubRoutes).Assembly.GetName().Name);
            Assert.Equal("Stomme.Transport.Realtime", typeof(HubRoutes).Namespace);
            Assert.Equal("/hubs/v1/notification", HubRoutes.Notification);
            Assert.Equal("/hubs/v1/health", HubRoutes.Health);
        }

        [Fact]
        public void The_server_assembly_forwards_HubRoutes_for_binary_compatibility()
        {
            var forwarded = typeof(NotificationHub).Assembly.GetForwardedTypes();

            Assert.Contains(typeof(HubRoutes), forwarded);
        }
    }
}