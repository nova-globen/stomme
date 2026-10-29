#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Rapid7;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The Rapid7 connector cases (9), carried over by name, run over the port.</summary>
    public sealed class Rapid7ConnectorTests
    {
        private const string Console = "https://console.example.test";

        private const string PageZero = """
            {"resources":[{"id":101,"hostName":"pc-1","os":"Windows 11","addresses":[{"ip":"10.0.0.4","mac":"aa:bb"}],"riskScore":1234.5,"vulnerabilities":{"critical":1,"total":7},"links":[{"href":"https://console/api/3/assets/101","rel":"self"}]}],
             "page":{"number":0,"size":1,"totalPages":2,"totalResources":2},"links":[{"href":"x","rel":"self"}]}
            """;

        private const string PageOne = """
            {"resources":[{"id":102,"hostName":"srv-9","os":"Ubuntu","addresses":[],"riskScore":10.0,"vulnerabilities":{"critical":0,"total":1},"links":[]}],"page":{"number":1,"size":1,"totalPages":2,"totalResources":2}}
            """;

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, string operation = "assets", Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string endpoint = Console) =>
            Contexts.Extract(new StubEgress(handler), endpoint, operation, parameters, settings, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheInsightVmPort()
        {
            var connector = new Rapid7Connector();

            Assert.Equal(("rapid7", AuthenticationType.Basic), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "assets", "sites", "scans", "vulnerabilities", "custom" }, connector.Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_PagesByTotalPagesAndDropsLinks()
        {
            var handler = new RecordingHandler(request => Json(request.RequestUri!.Query.Contains("page=0") ? PageZero : PageOne));

            var records = await Contexts.DrainAsync(new Rapid7Connector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["api_limit"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal(101L, records[0].Fields["id"]);
            Assert.Equal(1234.5, records[0].Fields["riskScore"]);
            Assert.Equal(1L, records[0].Fields["vulnerabilities_critical"]);
            Assert.False(records[0].Fields.ContainsKey("links"));
            Assert.Contains("10.0.0.4", (string?)records[0].Fields["addresses"]);

            Assert.Equal(2, handler.Calls.Count);
            Assert.Equal($"{Console}/api/3/assets?page=0&size=1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
            Assert.Equal(("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pw"))), (handler.Calls[0].Request.Headers.Authorization?.Scheme, handler.Calls[0].Request.Headers.Authorization?.Parameter));
        }

        [Fact]
        public async Task Extract_SiteScope_RewritesThePath()
        {
            var handler = new RecordingHandler(_ => Json(PageOne));

            await Contexts.DrainAsync(new Rapid7Connector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["site_id"] = "7", ["sort"] = "id,asc" }));

            var url = handler.Calls[0].Request.RequestUri!.AbsoluteUri;
            Assert.StartsWith($"{Console}/api/3/sites/7/assets?", url);
            Assert.Contains("sort=id%2Casc", url);
        }

        [Fact]
        public async Task Extract_ConflictingScopes_Throw()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new Rapid7Connector(), Extract(new RecordingHandler(_ => Json(PageOne)), parameters: new(StringComparer.Ordinal) { ["site_id"] = "7", ["scan_id"] = "9" })));

            Assert.Contains("at most ONE", ex.Message);
        }

        [Fact]
        public async Task Extract_CustomPath_MustBeVersioned()
        {
            var handler = new RecordingHandler(_ => Json(PageOne));

            await Contexts.DrainAsync(new Rapid7Connector(), Extract(handler, "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "/api/3/tags" }));
            Assert.StartsWith($"{Console}/api/3/tags?", handler.Calls[0].Request.RequestUri!.AbsoluteUri);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new Rapid7Connector(), Extract(handler, "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "/api/2/tags" })));
            Assert.Contains("/api/3/", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingEndpoint_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new Rapid7Connector(), Extract(new RecordingHandler(_ => Json(PageOne)), endpoint: string.Empty)));

            Assert.Contains("console base URL", ex.Message);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new Rapid7Connector(), Extract(new RecordingHandler(_ => Json(PageOne)), "reports")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesOneAsset()
        {
            var handler = new RecordingHandler(_ => Json(PageOne));

            var result = await new Rapid7Connector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Console, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/api/3/assets?page=0&size=1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_Unauthorized_MentionsTwoFactorCaveat()
        {
            var result = await new Rapid7Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new RecordingHandler(_ => Json("""{"message":"denied"}""", HttpStatusCode.Unauthorized))), Console, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("401", result.Message);
            Assert.Contains("2FA", result.Message);
        }
    }
}
