#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Stomme.Tests.Notifications;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 6 (D110) - a connector dials only through the members the HOST hands it. With the real
    /// <see cref="HostConnectorEgress"/> behind the harness: a test's HTTP call to a private address is refused at connect
    /// and answered as the policy's category, an allowlisted host is admitted and the receiver hears the request; a
    /// driver-style connector asking for an address to dial is refused BEFORE dialling (null) and admitted when the host
    /// is allowlisted; an extraction rides the same egress. The probe path joins in increment 11.
    /// </summary>
    public sealed class ConnectionEgressTests
    {
        private static ProbeConnector HttpProbe() => new("http-probe", async (context, ct) =>
        {
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var response = await client.GetAsync(context.Endpoint, ct);
            return response.IsSuccessStatusCode ? ConnectionTestResult.Ok() : ConnectionTestResult.Fail("status " + (int)response.StatusCode);
        })
        { AuthType = AuthenticationType.None, EndpointKind = ConnectorEndpointKind.Url };

        [Fact]
        public async Task A_tests_call_to_a_private_address_is_refused_at_connect()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(HttpProbe(), egress: new HostConnectorEgress(Array.Empty<string>()));
            var id = await harness.SendAsync(new CreateConnectionCommand("Loopback", "http-probe", receiver.Url()));

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((false, ConnectorOutcome.DeniedByPolicy, "Failing"), (result.Success, result.Outcome, result.Status));
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task An_allowlisted_host_is_admitted_and_the_receiver_hears_the_request()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(HttpProbe(), egress: new HostConnectorEgress(new[] { "127.0.0.1" }));
            var id = await harness.SendAsync(new CreateConnectionCommand("Loopback", "http-probe", receiver.Url()));

            var result = await harness.SendAsync(new TestConnectionCommand(id));

            Assert.Equal((true, ConnectorOutcome.Succeeded, "Healthy"), (result.Success, result.Outcome, result.Status));
            Assert.Single(receiver.Requests);
        }

        [Fact]
        public async Task A_driver_style_call_is_refused_before_dialling_and_admitted_when_allowlisted()
        {
            IPAddress? pinned = null;
            var driver = new ProbeConnector("driver-probe", async (context, ct) =>
            {
                pinned = await context.Egress.ResolveAndPinAsync("127.0.0.1", 5432, ct);
                return pinned is null ? ConnectionTestResult.Fail("no permitted address") : ConnectionTestResult.Ok();
            })
            { AuthType = AuthenticationType.None };

            await using (var refused = await ConnectivityApplicationHarness.StartAsync(driver, egress: new HostConnectorEgress(Array.Empty<string>())))
            {
                var id = await refused.SendAsync(new CreateConnectionCommand("DB", "driver-probe", "Host=127.0.0.1;Port=5432"));
                Assert.Equal(ConnectorOutcome.Failed, (await refused.SendAsync(new TestConnectionCommand(id))).Outcome);
                Assert.Null(pinned);
            }

            await using (var admitted = await ConnectivityApplicationHarness.StartAsync(driver, egress: new HostConnectorEgress(new[] { "127.0.0.1" })))
            {
                var id = await admitted.SendAsync(new CreateConnectionCommand("DB", "driver-probe", "Host=127.0.0.1;Port=5432"));
                Assert.Equal(ConnectorOutcome.Succeeded, (await admitted.SendAsync(new TestConnectionCommand(id))).Outcome);
                Assert.Equal(IPAddress.Loopback, pinned);
            }
        }

        [Fact]
        public async Task An_extraction_rides_the_same_egress_and_is_refused_for_a_private_address()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var extractor = new FetchingExtractor();
            await using var harness = await ConnectivityApplicationHarness.StartAsync(extractor, egress: new HostConnectorEgress(Array.Empty<string>()));
            var id = await harness.SendAsync(new CreateConnectionCommand("Loopback", "fetching", receiver.Url()));

            var result = await harness.ExtractAsync(id, "fetch");

            Assert.Equal((false, ConnectorOutcome.DeniedByPolicy), (result.Success, result.Outcome));
            Assert.Empty(receiver.Requests);

            await using var admitted = await ConnectivityApplicationHarness.StartAsync(extractor, egress: new HostConnectorEgress(new[] { "127.0.0.1" }));
            var id2 = await admitted.SendAsync(new CreateConnectionCommand("Loopback", "fetching", receiver.Url()));
            var ok = await admitted.ExtractAsync(id2, "fetch");
            Assert.True(ok.Success, ok.Message);
            Assert.Single(ok.Records);
            Assert.Single(receiver.Requests);
        }

        private sealed class FetchingExtractor : IConnector, IConnectorExtractor
        {
            public string ConnectorType => "fetching";

            public string DisplayName => "Fetching";

            public IReadOnlyList<ConnectorOperation> Operations { get; } = new[] { new ConnectorOperation("fetch", "Fetch") };

            public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult(ConnectionTestResult.Ok());

            public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var response = await client.GetAsync(context.Endpoint, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?> { ["status"] = (long)response.StatusCode });
            }
        }
    }
}
