#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Qualys;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 4 - the predecessor platform's QualysConnectorTests (8) by name over the port.</summary>
    public sealed class QualysConnectorTests
    {
        private const string PageOne = """
            {"ServiceResponse":{"responseCode":"SUCCESS","count":1,"hasMoreRecords":"true",
             "data":[{"HostAsset":{"id":1001,"name":"srv-1","type":"HOST","os":"Windows Server 2022","created":"2024-01-01T00:00:00Z","lastVulnScan":"2026-07-01T00:00:00Z",
                      "agentInfo":{"lastCheckedIn":"2026-07-08T12:00:00Z","status":"Active"},"address":"10.0.0.4","fqdn":"srv-1.corp","trackingMethod":"AGENT"}}]}}
            """;

        private const string PageTwo = """
            {"ServiceResponse":{"responseCode":"SUCCESS","count":1,"hasMoreRecords":false,"data":[{"HostAsset":{"id":1002,"name":"srv-2","os":"Ubuntu","address":"10.0.0.5"}}]}}
            """;

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "host-assets", Dictionary<string, string>? credentials = null) =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, settings, credentials ?? Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheQpsPort()
        {
            var connector = new QualysConnector();

            Assert.Equal(("qualys", AuthenticationType.Basic), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("host-assets", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_PagesByOffsetAndMapsTheLegacyProjection()
        {
            var handler = new RecordingHandler((_, call) => Json(call == 0 ? PageOne : PageTwo));

            var records = await Contexts.DrainAsync(new QualysConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["api_limit"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal(1001L, records[0].Fields["id"]);
            Assert.Equal("srv-1", records[0].Fields["name"]);
            Assert.Equal("2026-07-08T12:00:00Z", records[0].Fields["lastCheckedIn"]);
            Assert.Equal("AGENT", records[0].Fields["trackingMethod"]);
            Assert.Null(records[1].Fields["lastVulnScan"]);

            Assert.Equal(2, handler.Calls.Count);
            var first = XElement.Parse(handler.Calls[0].Body);
            Assert.Equal(("1", "1"), (first.Element("preferences")!.Element("startFromOffset")!.Value, first.Element("preferences")!.Element("limitResults")!.Value));
            Assert.Equal("2", XElement.Parse(handler.Calls[1].Body).Element("preferences")!.Element("startFromOffset")!.Value);
            Assert.All(handler.Requests, r =>
            {
                Assert.Equal(("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pw"))), (r.Headers.Authorization?.Scheme, r.Headers.Authorization?.Parameter));
                Assert.Equal(QualysConnector.RequestedWith, Contexts.Header(r, "X-Requested-With"));
            });
        }

        [Fact]
        public async Task Extract_SingleCriteria_LandsInTheXmlFilters()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            await Contexts.DrainAsync(new QualysConnector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["filter_field"] = "lastVulnScan", ["filter_operator"] = "GREATER", ["filter_value"] = "2026-01-01" }));

            var criteria = XElement.Parse(handler.Calls[0].Body).Element("filters")!.Element("Criteria")!;
            Assert.Equal(("lastVulnScan", "GREATER", "2026-01-01"), (criteria.Attribute("field")!.Value, criteria.Attribute("operator")!.Value, criteria.Value));
        }

        [Fact]
        public async Task Extract_PartialCriteria_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new QualysConnector(), Extract(new RecordingHandler(_ => Json(PageTwo)), parameters: new(StringComparer.Ordinal) { ["filter_field"] = "os" })));

            Assert.Contains("all three", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingPassword_FailsLoudlyWithoutCalling()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new QualysConnector(), Extract(handler, credentials: new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "user" })));

            Assert.Contains("password", ex.Message);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new QualysConnector(), Extract(new RecordingHandler(_ => Json(PageTwo)), operation: "reports")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesOneAssetSearch()
        {
            var handler = new RecordingHandler(_ => Json(PageTwo));

            var result = await new QualysConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            var (request, body) = Assert.Single(handler.Calls);
            Assert.Equal("https://qualysapi.qualys.com/qps/rest/2.0/search/am/hostasset", request.RequestUri!.AbsoluteUri);
            Assert.Equal("1", XElement.Parse(body).Element("preferences")!.Element("limitResults")!.Value);
        }

        [Fact]
        public async Task TestConnection_Unauthorized_ReportsTheCredentials()
        {
            var result = await new QualysConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new RecordingHandler(_ => Json("""{"message":"denied"}""", HttpStatusCode.Unauthorized))), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("401", result.Message);
        }
    }
}
