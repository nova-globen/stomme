#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// The predecessor platform's ConnectorExtractionServiceTests (7) by name on the port's
    /// <see cref="IConnectorExtractionService"/>: the connection, the extractor, the operation, the credentials and the
    /// records; a failure is a category, never an exception. CHANGED: the two managed-file-sink cases
    /// (<c>Extract_WithARegisteredFileSink_SetsItOnTheContext</c>, <c>Extract_WithoutARegisteredFileSink_LeavesTheContextSinkNull</c>)
    /// are the RECORD CAP's - the sink is not in the contract (its file-manager half is BL-95's), the cap is D110's.
    /// </summary>
    public sealed class ConnectorExtractionServiceTests
    {
        [Fact]
        public async Task Extract_ResolvesConnection_SelectsOperation_AndStreamsRecords()
        {
            var connector = new ProbeExtractor("fake-extract", count: 2);
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list", new Dictionary<string, string> { ["k"] = "v" });

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Records.Count);
            Assert.Equal("list", result.Records[0].Fields["op"]);
            Assert.Equal(("list", "v", "endpoint://x"), (connector.LastContext?.OperationName, connector.LastContext?.Parameters["k"], connector.LastContext?.Endpoint));
            Assert.False(result.Truncated);
            Assert.Equal("Extracted 2 record(s).", result.Message);
        }

        [Fact]
        public async Task Extract_WithNoOperationSelected_UsesTheFirstDeclaredOperation()
        {
            var connector = new ProbeExtractor("fake-extract", count: 1);
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, operation: null);

            Assert.True(result.Success);
            Assert.Equal("list", connector.LastContext?.OperationName);
        }

        [Fact]
        public async Task Extract_StopsAtTheRecordCap_AndSaysSo()
        {
            var connector = new ProbeExtractor("fake-extract", count: 5);
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list", maxRecords: 3);

            Assert.True(result.Success);
            Assert.Equal(3, result.Records.Count);
            Assert.True(result.Truncated);
            Assert.Contains("cap of 3", result.Message);
            Assert.Equal(4, connector.Yielded); // one read past the cap tells the rest exists; the fifth was never asked for
        }

        [Fact]
        public async Task Extract_UnderTheCap_IsNotTruncated()
        {
            var connector = new ProbeExtractor("fake-extract", count: 2);
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list", maxRecords: 3);

            Assert.Equal((true, 2, false), (result.Success, result.Records.Count, result.Truncated));
            Assert.Equal(2, connector.Yielded);
        }

        [Fact]
        public async Task Extract_UnknownConnection_Fails()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeExtractor("fake-extract", count: 1));

            var result = await harness.ExtractAsync(Guid.NewGuid(), "list");

            Assert.Equal((false, ConnectorOutcome.ConnectionNotFound), (result.Success, result.Outcome));
            Assert.Empty(result.Records);
        }

        [Fact]
        public async Task Extract_ConnectorWithoutExtractCapability_Fails()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeConnector("fake-plain") { AuthType = AuthenticationType.None });
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-plain", "endpoint://x"));

            var result = await harness.ExtractAsync(id, null);

            Assert.Equal((false, ConnectorOutcome.ExtractionUnsupported), (result.Success, result.Outcome));
            Assert.Equal(ConnectorOutcomes.Message(ConnectorOutcome.ExtractionUnsupported), result.Message);
        }

        [Fact]
        public async Task Extract_UndefinedOperation_Fails()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeExtractor("fake-extract", count: 1));
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "nope");

            Assert.Equal((false, ConnectorOutcome.OperationUndefined), (result.Success, result.Outcome));
        }

        [Fact]
        public async Task Extract_CarriesTheHostsEgress_OnTheContext()
        {
            var connector = new ProbeExtractor("fake-extract", count: 1);
            await using var harness = await ConnectivityApplicationHarness.StartAsync(connector);
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x", TimeoutSeconds: 12));

            await harness.ExtractAsync(id, "list");

            Assert.Same(harness.Egress, connector.LastContext?.Egress);
            Assert.Equal(TimeSpan.FromSeconds(12), connector.LastContext?.Timeout);
        }

        [Fact]
        public async Task Extract_AConnectorFault_IsACategory_AndTheLogCarriesTheTypeNotTheText()
        {
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeExtractor("fake-extract", count: 1, fault: new InvalidOperationException("password=hunter2 refused")));
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list");

            Assert.Equal((false, ConnectorOutcome.Faulted), (result.Success, result.Outcome));
            Assert.DoesNotContain("hunter2", result.Message);
            Assert.Contains(harness.Logs, line => line.Contains("System.InvalidOperationException", StringComparison.Ordinal) && line.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(harness.Logs, line => line.Contains("hunter2", StringComparison.Ordinal));
        }

        [Fact]
        public async Task An_untrusted_host_key_while_extracting_is_its_own_category()
        {
            // BL-229 (D110's correction of 2026-10-17): the extraction port classifies it as the test runner does.
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeExtractor("sftpish", count: 1, fault: new ConnectorHostKeyUntrustedException("it offered ssh-ed25519 SHA256:OFFERED")));
            var id = await harness.SendAsync(new CreateConnectionCommand("Kassa", "sftpish", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list");

            Assert.Equal((false, ConnectorOutcome.HostKeyUntrusted, ConnectorOutcomes.Message(ConnectorOutcome.HostKeyUntrusted)), (result.Success, result.Outcome, result.Message));
            Assert.DoesNotContain(harness.Logs, line => line.Contains("OFFERED", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_connectors_own_timeout_while_extracting_is_TimedOut_not_an_exception()
        {
            // BL-239's validation (measured against a slow server): an HttpClient's timeout elapses as a TaskCanceledException
            // the caller never asked for, and it escaped the port - a pipeline step faulted with the raw exception's text.
            var elapsed = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing.", new TimeoutException());
            await using var harness = await ConnectivityApplicationHarness.StartAsync(new ProbeExtractor("fake-extract", count: 1, fault: elapsed));
            var id = await harness.SendAsync(new CreateConnectionCommand("Sys", "fake-extract", "endpoint://x"));

            var result = await harness.ExtractAsync(id, "list");

            Assert.Equal((false, ConnectorOutcome.TimedOut, ConnectorOutcomes.Message(ConnectorOutcome.TimedOut)), (result.Success, result.Outcome, result.Message));
        }
    }
}
