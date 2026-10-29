#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.FlexeraOne;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The FlexeraOneConnectorTests cases (10), kept by name over the port: the bare refresh-token grant, bounded cycle-guarded paging, the error property outranking a missing values array.</summary>
    public sealed class FlexeraOneConnectorTests
    {
        private const string PageOne = """
            {"values":[{"name":"App A","version":"1.0","installations":12,"publisher":{"name":"Contoso"}}],
             "nextPage":"https://api.flexera.com/fnms/v1/orgs/org-1/reports/42/execute?skipToken=abc"}
            """;

        private const string PageTwo = """{"values":[{"name":"App B","version":"2.0","installations":3,"publisher":{"name":"Fabrikam"}}]}""";

        private static RecordingHandler Handler(Func<HttpRequestMessage, int, HttpResponseMessage> api) =>
            new(api)
            {
                TokenRoute = request => request.RequestUri!.Host.StartsWith("login.flexera", StringComparison.OrdinalIgnoreCase),
                TokenResponse = () => Json("""{"token_type":"Bearer","expires_in":3599,"access_token":"flex-tok"}"""),
            };

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["refresh-token"] = "rt-1" };

        private static Dictionary<string, string> Settings(string org = "org-1") => new(StringComparer.Ordinal) { ["org_id"] = org };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "reports") =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["report_numbers"] = "42" }, settings ?? Settings(), Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheFlexeraPort()
        {
            var connector = new FlexeraOneConnector();

            Assert.Equal(("flexeraone", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("reports", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_FollowsNextLinksAndFlattens()
        {
            var handler = Handler((request, _) => Json(request.RequestUri!.Query.Contains("skipToken=abc") ? PageTwo : PageOne));

            var records = await Contexts.DrainAsync(new FlexeraOneConnector(), Extract(handler));

            Assert.Equal(2, records.Count);
            Assert.Equal("App A", records[0].Fields["name"]);
            Assert.Equal(12L, records[0].Fields["installations"]);
            Assert.Equal("Contoso", records[0].Fields["publisher_name"]);

            var api = handler.Requests.ToList();
            Assert.Equal(2, api.Count);
            Assert.Equal("https://api.flexera.com/fnms/v1/orgs/org-1/reports/42/execute", api[0].RequestUri!.AbsoluteUri);
            Assert.Contains("skipToken=abc", api[1].RequestUri!.AbsoluteUri);
            Assert.All(api, r => Assert.Equal("flex-tok", r.Headers.Authorization?.Parameter));

            // The bare refresh-token grant went to the region's login host.
            var form = Assert.Single(handler.TokenForms);
            Assert.Contains("grant_type=refresh_token", form);
            Assert.Contains("refresh_token=rt-1", form);
        }

        [Fact]
        public async Task Extract_MultipleReports_ExecuteEach()
        {
            var handler = Handler((_, _) => Json(PageTwo));

            var records = await Contexts.DrainAsync(new FlexeraOneConnector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["report_numbers"] = "42; 77" }));

            Assert.Equal(2, records.Count);
            Assert.Contains(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/reports/42/execute"));
            Assert.Contains(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/reports/77/execute"));
        }

        [Fact]
        public async Task Extract_CyclicNextLink_ThrowsInsteadOfLooping()
        {
            // The legacy do/while would follow a self-referencing next link forever.
            var handler = Handler((_, _) => Json("""{"values":[{"name":"App A"}],"nextPage":"https://api.flexera.com/fnms/v1/orgs/org-1/reports/42/execute"}"""));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexeraOneConnector(), Extract(handler)));

            Assert.Contains("did not terminate", ex.Message);
        }

        [Fact]
        public async Task Extract_ErrorProperty_OutranksMissingValues()
        {
            // The legacy bridge reported this shape as "values missing" instead of the real API error.
            var handler = Handler((_, _) => Json("""{"error":{"code":403,"message":"no access to org"}}"""));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexeraOneConnector(), Extract(handler)));

            Assert.Contains("no access to org", ex.Message);
        }

        [Fact]
        public async Task Extract_RegionSetting_DrivesLoginAndApiHosts()
        {
            var handler = Handler((_, _) => Json(PageTwo));

            await Contexts.DrainAsync(new FlexeraOneConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["org_id"] = "org-1", ["region"] = "eu" }));

            Assert.StartsWith("https://api.flexera.eu/", handler.Requests.First().RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_MissingOrgId_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexeraOneConnector(), Extract(Handler((_, _) => Json(PageTwo)), settings: new(StringComparer.Ordinal))));

            Assert.Contains("org_id", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingReportNumbers_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FlexeraOneConnector(), Extract(Handler((_, _) => Json(PageTwo)), parameters: new(StringComparer.Ordinal))));

            Assert.Contains("report_numbers", ex.Message);
        }

        [Fact]
        public async Task TestConnection_TokenExchangeIsTheProbe()
        {
            var handler = Handler((_, _) => Json(PageTwo));

            var result = await new FlexeraOneConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, Settings(), Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Single(handler.TokenForms);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task TestConnection_MissingRefreshToken_Fails()
        {
            var result = await new FlexeraOneConnector().TestConnectionAsync(Contexts.Test(new StubEgress(Handler((_, _) => Json(PageTwo))), string.Empty, Settings(), new Dictionary<string, string>(StringComparer.Ordinal), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("refresh-token", result.Message);
        }
    }
}
