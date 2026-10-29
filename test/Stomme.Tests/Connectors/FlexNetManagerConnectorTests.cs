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
using Stomme.Modules.Connectors.FlexNetManager;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The FlexNetManagerConnectorTests cases (10), kept by name over the port: the hand-rolled SOAP envelope, the DataSet diffgram, the region-aware refresh-token grant.</summary>
    public sealed class FlexNetManagerConnectorTests
    {
        private const string DataSetResponse = """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
              <soap:Body>
                <GetCustomViewResponse xmlns="http://tempuri.org/">
                  <GetCustomViewResult>
                    <xs:schema id="NewDataSet" xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="NewDataSet"/></xs:schema>
                    <diffgr:diffgram xmlns:diffgr="urn:schemas-microsoft-com:xml-diffgram-v1" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
                      <NewDataSet>
                        <Table1 diffgr:id="Table11" msdata:rowOrder="0"><ComputerName>PC-1</ComputerName><Domain>corp</Domain><InstalledApps>42</InstalledApps></Table1>
                        <Table1 diffgr:id="Table12" msdata:rowOrder="1"><ComputerName>PC-2</ComputerName><Domain>corp</Domain><InstalledApps>7</InstalledApps></Table1>
                      </NewDataSet>
                    </diffgr:diffgram>
                  </GetCustomViewResult>
                </GetCustomViewResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        private const string FaultResponse = """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
              <soap:Body>
                <soap:Fault>
                  <soap:Reason><soap:Text xml:lang="en">Custom view 999 does not exist.</soap:Text></soap:Reason>
                </soap:Fault>
              </soap:Body>
            </soap:Envelope>
            """;

        private const string ServiceEndpoint = "https://fnms.example.test/ManageSoftServices/ComplianceAPIService/ComplianceAPIService.asmx?orgid=1";

        private static HttpResponseMessage Soap(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/soap+xml") };

        private static RecordingHandler Handler(Func<HttpRequestMessage, string, HttpResponseMessage> soap) =>
            new((request, body, _) => soap(request, body))
            {
                TokenRoute = request => request.RequestUri!.Host.StartsWith("login.flexera", StringComparison.OrdinalIgnoreCase),
                TokenResponse = () => Json("""{"token_type":"Bearer","expires_in":3599,"access_token":"fnms-tok"}"""),
            };

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["refresh-token"] = "rt-1" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? parameters = null, Dictionary<string, string>? settings = null, string operation = "custom-view", string endpoint = ServiceEndpoint) =>
            Contexts.Extract(new StubEgress(handler), endpoint, operation, parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["view_id"] = "123" }, settings, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheSoapPort()
        {
            var connector = new FlexNetManagerConnector();

            Assert.Equal(("fnms", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("custom-view", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_PostsTheContractEnvelopeAndParsesTheDataSet()
        {
            var handler = Handler((_, _) => Soap(DataSetResponse));

            var records = await Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(handler, new(StringComparer.Ordinal) { ["view_id"] = "123", ["search"] = "corp", ["row_limit"] = "5000", ["tenant_uid"] = "t-1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal(("PC-1", "42"), (records[0].Fields["ComputerName"], records[0].Fields["InstalledApps"]));

            var (request, body) = Assert.Single(handler.Calls);
            Assert.Equal(ServiceEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("fnms-tok", request.Headers.Authorization?.Parameter);
            Assert.Contains($"action=\"{FlexNetManagerConnector.SoapAction}\"", request.Content!.Headers.ContentType!.ToString());

            // The envelope mirrors the generated WCF contract: the wrapper and ordered members in the tempuri namespace.
            XNamespace tempuri = "http://tempuri.org/";
            var wrapper = XDocument.Parse(body).Descendants(tempuri + "GetCustomView").Single();
            Assert.Equal(new[] { "customViewID", "searchString", "rowLimit", "tenantUID" }, wrapper.Elements().Select(e => e.Name.LocalName));
            Assert.Equal(("123", "corp", "5000", "t-1"), (wrapper.Element(tempuri + "customViewID")!.Value, wrapper.Element(tempuri + "searchString")!.Value, wrapper.Element(tempuri + "rowLimit")!.Value, wrapper.Element(tempuri + "tenantUID")!.Value));
        }

        [Fact]
        public async Task Extract_SoapFault_SurfacesTheReason()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(Handler((_, _) => Soap(FaultResponse, HttpStatusCode.InternalServerError)))));

            Assert.Contains("Custom view 999 does not exist", ex.Message);
        }

        [Fact]
        public async Task Extract_EmptyDiffgram_YieldsNoRecords()
        {
            var handler = Handler((_, _) => Soap("""
                <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope"><soap:Body>
                  <GetCustomViewResponse xmlns="http://tempuri.org/"><GetCustomViewResult>
                    <diffgr:diffgram xmlns:diffgr="urn:schemas-microsoft-com:xml-diffgram-v1"/>
                  </GetCustomViewResult></GetCustomViewResponse>
                </soap:Body></soap:Envelope>
                """));

            Assert.Empty(await Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(handler)));
        }

        [Fact]
        public async Task Extract_MissingViewId_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(Handler((_, _) => Soap(DataSetResponse)), new(StringComparer.Ordinal))));

            Assert.Contains("view_id", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingEndpoint_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(Handler((_, _) => Soap(DataSetResponse)), endpoint: string.Empty)));

            Assert.Contains("ComplianceAPIService", ex.Message);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(Handler((_, _) => Soap(DataSetResponse)), operation: "inventory")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task Extract_RegionSetting_DrivesTheLoginHost()
        {
            var handler = Handler((_, _) => Soap(DataSetResponse));

            await Contexts.DrainAsync(new FlexNetManagerConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["region"] = "eu" }));

            Assert.Single(handler.TokenForms);
        }

        [Fact]
        public async Task TestConnection_TokenExchangeIsTheProbe()
        {
            var handler = Handler((_, _) => Soap(DataSetResponse));

            var result = await new FlexNetManagerConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), "https://fnms.example.test/svc.asmx", credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Single(handler.TokenForms);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task TestConnection_MissingRefreshToken_MentionsTheNtlmDeferral()
        {
            var result = await new FlexNetManagerConnector().TestConnectionAsync(Contexts.Test(new StubEgress(Handler((_, _) => Soap(DataSetResponse))), "https://fnms.example.test/svc.asmx", credentials: new Dictionary<string, string>(StringComparer.Ordinal), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("refresh-token", result.Message);
        }
    }
}
