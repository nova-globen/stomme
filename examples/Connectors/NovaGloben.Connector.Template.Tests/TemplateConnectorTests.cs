#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using NovaGloben.Connector.Template;
using Xunit;

namespace NovaGloben.Connector.Template.Tests
{
    /// <summary>The template's tests: every request goes through the egress the test hands in; the scheme's key rides the configured header; a denial the egress recognises propagates.</summary>
    public sealed class TemplateConnectorTests
    {
        [Fact]
        public async Task A_reachable_endpoint_is_ok_and_the_api_key_rides_the_configured_header()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var egress = new StubEgress(handler);
            var context = new ConnectorTestContext("https://api.example.com/", new Dictionary<string, string> { ["path"] = "health", ["api_key_header"] = "X-Token" }, TimeSpan.FromSeconds(5), egress)
            {
                Credentials = new Dictionary<string, string> { ["api-key"] = "k-1" },
            };

            var result = await new TemplateConnector().TestConnectionAsync(context);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, egress.Clients);
            Assert.Equal("https://api.example.com/health", handler.Request?.RequestUri?.ToString());
            Assert.Equal("k-1", string.Join(",", handler.Request!.Headers.GetValues("X-Token")));
        }

        [Fact]
        public async Task A_non_success_status_and_a_bad_endpoint_are_fails_and_a_denial_propagates()
        {
            Assert.False((await new TemplateConnector().TestConnectionAsync(Context(new StubEgress(new StubHandler(HttpStatusCode.BadGateway)), "https://api.example.com/"))).Success);
            Assert.False((await new TemplateConnector().TestConnectionAsync(Context(new StubEgress(new StubHandler(HttpStatusCode.OK)), "not-a-url"))).Success);

            var denying = new StubEgress(new ThrowingHandler()) { DeniesEverything = true };
            await Assert.ThrowsAsync<HttpRequestException>(() => new TemplateConnector().TestConnectionAsync(Context(denying, "http://10.0.0.1/")));

            var failing = new StubEgress(new ThrowingHandler());
            Assert.False((await new TemplateConnector().TestConnectionAsync(Context(failing, "http://10.0.0.1/"))).Success); // the same exception, not a denial: the remote's failure
        }

        [Fact]
        public void The_scheme_declares_the_key_the_connector_reads()
        {
            IConnector connector = new TemplateConnector(); // the capabilities are the contract's default member
            Assert.Equal(AuthenticationType.ApiKey, connector.AuthType);
            Assert.Contains(AuthenticationScheme.FieldsFor(connector.AuthType), field => field.Name == "api-key" && field.Secret);
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.TestConnection));
            Assert.False(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
        }

        private static ConnectorTestContext Context(IConnectorEgress egress, string endpoint) => new(endpoint, new Dictionary<string, string>(), TimeSpan.FromSeconds(5), egress);

        private sealed class StubEgress(HttpMessageHandler handler) : IConnectorEgress
        {
            public int Clients { get; private set; }

            public bool DeniesEverything { get; init; }

            public HttpClient CreateHttpClient(TimeSpan timeout)
            {
                Clients++;
                return new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
            }

            public Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default) => Task.FromResult<IPAddress?>(DeniesEverything ? null : IPAddress.Loopback);

            public bool IsDenial(Exception exception) => DeniesEverything;
        }

        private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
        {
            public HttpRequestMessage? Request { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                return Task.FromResult(new HttpResponseMessage(status));
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("refused");
        }
    }
}
