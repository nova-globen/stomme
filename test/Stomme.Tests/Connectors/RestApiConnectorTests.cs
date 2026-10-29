#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.RestApi;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The predecessor platform's RestApiConnectorTests (7) by name over the port. CHANGED: the connector has no
    /// handler-factory constructor; the client is the egress's, so a test hands in a <see cref="StubEgress"/> over its
    /// handler. The two Live cases now say what the port means: a real HTTP server on loopback is reached through a
    /// permissive stub egress (the connector's HTTP path is real), and through the HOST's egress the same loopback address
    /// is REFUSED - and the refusal is not swallowed into a Fail, it propagates as the guard's denial.
    /// </summary>
    public sealed class RestApiConnectorTests
    {
        [Fact]
        public async Task TestConnection_SuccessStatus_ReturnsOk_AndSendsBearerCredential()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), "https://api.example.com/health", credentials: new Dictionary<string, string> { ["token"] = "tok123" }));

            Assert.True(result.Success, result.Message);
            Assert.Equal(("Bearer", "tok123"), (handler.Request?.Headers.Authorization?.Scheme, handler.Request?.Headers.Authorization?.Parameter));
        }

        [Fact]
        public async Task TestConnection_AppliesThePathSettingRelativeToTheEndpoint()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), "https://api.example.com/", new Dictionary<string, string> { ["path"] = "health/ready" }));

            Assert.True(result.Success);
            Assert.Equal("https://api.example.com/health/ready", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public void ConfigSchema_DeclaresTheOptionalConnectionFields()
        {
            // CHANGED: proxy_url and danger_accept_any_certificate are gone - the host's egress owns the proxy and the certificate policy.
            var fields = new RestApiConnector().ConfigSchema.Fields;
            Assert.Equal(new[] { "path", "headers" }, fields.Select(f => f.Name).ToArray());
            Assert.All(fields, f => Assert.False(f.Required));
            Assert.Equal(ConnectorConfigFieldType.Multiline, fields.Single(f => f.Name == "headers").Type);
        }

        [Fact]
        public async Task TestConnection_NonSuccessStatus_ReturnsFail() =>
            Assert.False((await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.InternalServerError)), "https://api.example.com"))).Success);

        [Fact]
        public async Task TestConnection_InvalidEndpoint_ReturnsFail()
        {
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK));
            Assert.False((await new RestApiConnector().TestConnectionAsync(Contexts.Test(egress, "not-a-url"))).Success);
            Assert.Equal(0, egress.Clients); // decided before any client is created
        }

        [Fact]
        public async Task TestConnection_Live_SucceedsAgainstARealHttpServer()
        {
            var port = FreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            });

            // A permissive egress (a real socket handler, no policy): the connector's own HTTP path, end to end.
            var egress = new StubEgress();
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(egress, $"http://127.0.0.1:{port}/"));
            await serve;
            listener.Stop();

            Assert.True(result.Success, result.Message);
            Assert.Equal(TimeSpan.FromSeconds(5), egress.LastTimeout); // the context's timeout is the client's
        }

        [Fact]
        public async Task TestConnection_Live_FailsForUnreachableServer()
        {
            // CHANGED: a closed port answers with a refusal (a Fail) or, on a host whose firewall drops rather than resets
            // (measured here: 2 s to the client timeout), with the timeout - which the port lets propagate as the
            // cancellation it is, so the runner says TimedOut rather than a Fail with the driver's words. Either is "not reached".
            try
            {
                var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), $"http://127.0.0.1:{FreePort()}/", timeoutSeconds: 2));
                Assert.False(result.Success);
            }
            catch (OperationCanceledException)
            {
                // timed out: the runner's TimedOut category
            }
        }

        // ---- the port's additions ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Through_the_hosts_egress_a_loopback_endpoint_is_refused_and_the_refusal_is_not_swallowed()
        {
            var egress = new HostConnectorEgress(Array.Empty<string>());
            var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => new RestApiConnector().TestConnectionAsync(Contexts.Test(egress, $"http://127.0.0.1:{FreePort()}/")));
            Assert.True(OutboundConnectGuard.IsDenial(ex), ex.ToString());
        }

        [Fact]
        public async Task A_denial_the_egress_recognises_propagates_and_any_other_failure_is_a_fail()
        {
            var thrown = new ThrowingHandler();
            await Assert.ThrowsAsync<HttpRequestException>(() => new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(thrown) { DeniesEverything = true }, "http://10.0.0.1/")));
            Assert.False((await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(thrown), "http://10.0.0.1/"))).Success);
        }

        [Fact]
        public async Task A_refused_private_address_never_opens_a_socket_and_a_custom_authorization_header_wins_over_the_token()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var settings = new Dictionary<string, string> { ["headers"] = "Authorization: Basic abc" };
            await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), "https://api.example.com/", settings, new Dictionary<string, string> { ["token"] = "ignored" }));
            Assert.Equal("Basic abc", Contexts.Header(handler.Request, "Authorization"));

            var denied = await Assert.ThrowsAnyAsync<HttpRequestException>(() => new RestApiConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), "http://10.255.255.1/", timeoutSeconds: 2)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) => throw new HttpRequestException("refused");
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
