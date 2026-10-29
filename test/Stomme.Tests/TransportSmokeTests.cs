using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stomme.Extensions;
using Stomme.Transport.Realtime;
using Stomme.Transport.V1;
using Xunit;

namespace Stomme.Tests
{
    // Live end-to-end proof for brief 04 / D10: stands up a real Kestrel host with the opt-in transports
    // enabled and drives them over the wire — a typed gRPC unary call + a gRPC server-stream, and a SignalR
    // channel push gated by an authorization policy. Nothing is mocked: real sockets, the real interceptor,
    // the real hub. These are the Definition-of-Done checks the brief calls out.
    public class TransportSmokeTests : IAsyncLifetime
    {
        private WebApplication _app = null!;
        private string _grpcUrl = null!; // cleartext HTTP/2 endpoint (native gRPC / h2c)
        private string _httpUrl = null!; // HTTP/1.1 endpoint (gRPC-Web + SignalR)

        public async Task InitializeAsync()
        {
            // Allow gRPC over cleartext HTTP/2 (h2c) so the native-gRPC path needs no TLS cert in the test host.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // Cleartext HTTP/2 cannot be negotiated without ALPN, so native gRPC needs an Http2-only endpoint
            // (h2c, prior knowledge); gRPC-Web and SignalR long-polling ride a separate HTTP/1.1 endpoint.
            // Port 0 = OS-assigned; the addresses feature reports them back in listen order.
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
                k.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transport:Grpc:Enabled"] = "true",
                ["Transport:SignalR:Enabled"] = "true",
            });

            builder.Services.AddRouting();
            builder.Services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // The system under test: the framework's opt-in transport registration.
            builder.Services.AddStommeTransport(builder.Configuration);

            _app = builder.Build();
            _app.UseRouting();
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.UseStommeTransport(builder.Configuration);   // gRPC-Web middleware
            _app.MapStommeTransport(builder.Configuration);   // gRPC services (via module contributor) + hubs

            await _app.StartAsync();

            var addresses = _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.ToList();
            _grpcUrl = addresses[0];
            _httpUrl = addresses[1];
        }

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task Grpc_native_h2c_round_trips_a_typed_unary_call()
        {
            using var channel = GrpcChannel.ForAddress(_grpcUrl);
            var client = new TransportDemo.TransportDemoClient(channel);

            var reply = await client.EchoAsync(new EchoRequest { Message = "ping" });

            Assert.Equal("ping", reply.Message);
            Assert.False(string.IsNullOrEmpty(reply.ServerTime));
        }

        [Fact]
        public async Task Grpc_native_h2c_streams_a_server_stream()
        {
            using var channel = GrpcChannel.ForAddress(_grpcUrl);
            var client = new TransportDemo.TransportDemoClient(channel);

            var beats = new List<int>();
            using var call = client.Heartbeat(new HeartbeatRequest { Count = 3 });
            await foreach (var beat in call.ResponseStream.ReadAllAsync())
            {
                beats.Add(beat.Sequence);
            }

            Assert.Equal(new[] { 1, 2, 3 }, beats);
        }

        [Fact]
        public async Task GrpcWeb_round_trips_a_typed_unary_call()
        {
            // The exact transport a WASM/MAUI client uses: gRPC frames tunneled over HTTP/1.1.
            using var channel = CreateGrpcWebChannel();
            var client = new TransportDemo.TransportDemoClient(channel);

            var reply = await client.EchoAsync(new EchoRequest { Message = "over-grpc-web" });

            Assert.Equal("over-grpc-web", reply.Message);
        }

        [Fact]
        public async Task Grpc_boundary_interceptor_maps_bad_input_to_invalid_argument()
        {
            using var channel = GrpcChannel.ForAddress(_grpcUrl);
            var client = new TransportDemo.TransportDemoClient(channel);

            var ex = await Assert.ThrowsAsync<RpcException>(
                () => client.EchoAsync(new EchoRequest { Message = "" }).ResponseAsync);

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }

        [Fact]
        public async Task SignalR_hub_rejects_an_unauthenticated_connection()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(_httpUrl + HubRoutes.Notification, o => o.Transports = HttpTransportType.LongPolling)
                .Build();

            // No access token -> the hub's authorization policy denies the connection at negotiate.
            await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
            await connection.DisposeAsync();
        }

        [Fact]
        public async Task SignalR_hub_pushes_to_an_authenticated_client()
        {
            const string userId = "user-1";
            var received = new TaskCompletionSource<NotificationRealtimeMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var connection = new HubConnectionBuilder()
                .WithUrl(_httpUrl + HubRoutes.Notification, o =>
                {
                    o.Transports = HttpTransportType.LongPolling;
                    o.AccessTokenProvider = () => Task.FromResult<string?>(userId);
                })
                .Build();

            connection.On<NotificationRealtimeMessage>(
                NotificationHub.NotificationMethod, msg => received.TrySetResult(msg));

            await connection.StartAsync();

            // Push from the server through the reference adapter, targeted to the connected user.
            var push = _app.Services.GetRequiredService<INotificationRealtimePush>();
            await push.PushAsync(new NotificationRealtimeMessage(userId, "Hello", "World"));

            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Hello", message.Subject);
            Assert.Equal("World", message.Body);

            await connection.DisposeAsync();
        }

        // A gRPC-Web channel over the HTTP/1.1 endpoint. GrpcChannel defaults requests to HTTP/2, so an inner
        // handler pins the version to 1.1 for the Http1-only endpoint (the browser fetch stack does this for us
        // in a real WASM client).
        private GrpcChannel CreateGrpcWebChannel()
        {
            var web = new GrpcWebHandler(GrpcWebMode.GrpcWeb)
            {
                InnerHandler = new Http11Handler { InnerHandler = new HttpClientHandler() },
            };
            return GrpcChannel.ForAddress(_httpUrl, new GrpcChannelOptions { HttpHandler = web });
        }

        private sealed class Http11Handler : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                request.Version = System.Net.HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                return base.SendAsync(request, cancellationToken);
            }
        }

        // Minimal test authentication: a bearer token (any non-empty value) authenticates as that user id.
        // Drives both the gRPC auth principal and SignalR's per-user grouping (ClaimTypes.NameIdentifier).
        private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string SchemeName = "Test";

            public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                string? token = null;
                var header = Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = header.Substring("Bearer ".Length).Trim();
                }
                token ??= Request.Query["access_token"].ToString();

                if (string.IsNullOrEmpty(token))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, token) }, SchemeName);
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }
    }
}
