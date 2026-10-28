using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stomme.Extensions;
using Stomme.Transport;
using Xunit;

namespace Stomme.Tests
{
    // The combination no transport test had ever run (BL-53a): SignalR ON, gRPC OFF. Both existing transport
    // suites (HostCompositionTests and TransportSmokeTests) set BOTH flags true, which masked the defect this
    // suite pins: MapStommeTransport ran the IModuleEndpointContributor loop only inside the
    // `if (options.Grpc.Enabled)` gate, so a module contributing a SignalR hub was silently never mapped on a
    // SignalR-only install — while the SignalR branch's own comment claimed "Module hubs map themselves via
    // IModuleEndpointContributor". D10 asserts the two transports are independent opt-ins; this is the first
    // test of that independence.
    public class TransportContributorIndependenceTests : IAsyncLifetime
    {
        private WebApplication _app = null!;
        private string _httpUrl = null!;

        // A module-shaped SignalR hub, mapped by the contributor below — the exact scenario the defect
        // silently broke.
        public sealed class ContributorTestHub : Hub
        {
        }

        // A real IModuleEndpointContributor implementor living in a Stomme-named assembly, so the transport
        // pipeline's reflection discovery (GetStommeAssemblies + GetInstances) finds it exactly the way it
        // finds a shipped module's contributor.
        public sealed class SignalRContributor : IModuleEndpointContributor
        {
            public const string HubRoute = "/stomme-tests/contributor-hub";

            public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
            {
                // The generalized contract (BL-53): the loop runs when ANY transport is enabled, and the
                // per-transport gate is the contributor's own — exactly what a real module's hub needs.
                if (transports.SignalR.Enabled)
                {
                    endpoints.MapHub<ContributorTestHub>(HubRoute);
                }
            }
        }

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            });

            // The masking combination inverted: SignalR only. This is a legitimate deployment shape (a
            // realtime-only install), and it is the one no test had ever run.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transport:SignalR:Enabled"] = "true",
                ["Transport:Grpc:Enabled"] = "false",
            });

            builder.Services.AddRouting();
            builder.Services.AddStommeTransport(builder.Configuration);

            _app = builder.Build();
            _app.UseRouting();
            _app.UseStommeTransport(builder.Configuration);
            _app.MapStommeTransport(builder.Configuration);

            await _app.StartAsync();

            _httpUrl = _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
        }

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task Module_contributor_hub_is_reachable_on_a_signalr_only_install()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(_httpUrl + SignalRContributor.HubRoute, o => o.Transports = HttpTransportType.LongPolling)
                .Build();

            // On the defective code the contributor loop never ran (gRPC gate), the hub route 404s and
            // StartAsync throws. On fixed code the negotiate succeeds and the connection comes up.
            await connection.StartAsync();
            Assert.Equal(HubConnectionState.Connected, connection.State);
            await connection.DisposeAsync();
        }

        [Fact]
        public void Demo_grpc_service_is_not_mapped_when_grpc_is_disabled()
        {
            // The contributor loop running on a SignalR-only install must NOT drag gRPC endpoints in with it:
            // a contributor maps per transport, and gRPC is off here. (MapGrpcService would also throw without
            // AddGrpc, so an unconditional demo mapping would fail the host outright.)
            var endpoints = ((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints)
                .Select(endpoint => endpoint.DisplayName ?? "")
                .ToList();

            Assert.DoesNotContain(endpoints, name => name.Contains("TransportDemo", StringComparison.OrdinalIgnoreCase));
        }
    }
}
