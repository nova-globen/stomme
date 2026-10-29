#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.CiscoDnaCenter;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The CiscoDnaCenterConnectorTests cases (8), kept by name over the port.</summary>
    public sealed class CiscoDnaCenterConnectorTests
    {
        private const string Appliance = "https://dnac.example.com";
        private const string Token = """{"Token":"tok-123"}""";
        private const string PageOne = """{"response":[{"id":"d1","hostname":"sw1","uptime":12345}]}""";
        private const string PageTwo = """{"response":[{"id":"d2","hostname":"sw2","uptime":222}]}""";
        private const string EmptyPage = """{"response":[]}""";
        private const string SingleObject = """{"response":{"score":98}}""";

        private static bool IsAuthRequest(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/dna/system/api/v1/auth/token", StringComparison.Ordinal);

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "dnac.user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "devices") =>
            Contexts.Extract(new StubEgress(handler), Appliance, operation, parameters, settings, Credentials(), timeoutSeconds: 30);

        private static RecordingHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> data) => new(request => IsAuthRequest(request) ? Json(Token) : data(request));

        [Fact]
        public void Declaration_MatchesTheIntentApiPort()
        {
            var connector = new CiscoDnaCenterConnector();

            Assert.Equal(("ciscodnac", "Cisco Catalyst Center (formerly Cisco DNA Center)", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Contains(ConnectorCategory.NetworkInfrastructure, connector.Categories);
            Assert.Equal((Uri.UriSchemeHttps, Uri.UriSchemeHttps), (connector.WebsiteUrl!.Scheme, connector.ApiDocumentationUrl!.Scheme));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "devices", "custom" }, connector.Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_ExchangesTokenThenPagesByOneBasedOffset()
        {
            var handler = Handler(request =>
            {
                var query = request.RequestUri!.Query;
                return Json(query.Contains("offset=1&") ? PageOne : query.Contains("offset=2&") ? PageTwo : EmptyPage);
            });

            var records = await Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["api_limit"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal(("sw1", 12345L), (records[0].Fields["hostname"], records[0].Fields["uptime"]));

            var requests = handler.Requests.ToList();
            Assert.True(IsAuthRequest(requests[0]));
            Assert.Equal((HttpMethod.Post, "Basic"), (requests[0].Method, requests[0].Headers.Authorization?.Scheme));
            Assert.Equal(4, requests.Count);
            Assert.Equal("tok-123", requests[1].Headers.GetValues("X-Auth-Token").Single());
            Assert.Contains("/dna/intent/api/v1/network-device?offset=1&limit=1", requests[1].RequestUri!.AbsoluteUri);
            Assert.Contains("offset=2&limit=1", requests[2].RequestUri!.AbsoluteUri);
            Assert.Contains("offset=3&limit=1", requests[3].RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_CustomUnpaged_ReadsOneObjectWithoutOffsetParams()
        {
            var handler = Handler(_ => Json(SingleObject));

            var records = await Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(handler, operation: "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "client-health", ["paged"] = "false" }));

            Assert.Equal(98L, Assert.Single(records).Fields["score"]);
            Assert.Equal(2, handler.Calls.Count); // the token and one data GET
            var url = handler.Calls[1].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("/dna/intent/api/v1/client-health", url);
            Assert.DoesNotContain("offset=", url);
        }

        [Fact]
        public async Task Extract_CustomPathAlreadyCarryingThePrefix_IsNotDoubled()
        {
            var handler = Handler(_ => Json(EmptyPage));

            await Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(handler, operation: "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "dna/intent/api/v1/site-health", ["paged"] = "false" }));

            var url = handler.Calls[1].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("/dna/intent/api/v1/site-health", url);
            Assert.DoesNotContain("/dna/intent/api/v1/dna/intent", url);
        }

        [Fact]
        public async Task Extract_FieldsAndFilter_AreAppendedToTheQuery()
        {
            var handler = Handler(_ => Json(EmptyPage));

            await Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["fields"] = "hostname,managementIpAddress", ["filter"] = "family=Switches" }));

            var url = handler.Calls[1].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("fields=hostname%2CmanagementIpAddress", url);
            Assert.Contains("family=Switches", url);
        }

        [Fact]
        public async Task Extract_TokenRejected_SurfacesTheError()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(new RecordingHandler(_ => Json("{}", HttpStatusCode.Unauthorized)))));

            Assert.Contains("401", ex.Message);
        }

        [Fact]
        public async Task Extract_CustomWithoutPath_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CiscoDnaCenterConnector(), Extract(Handler(_ => Json(EmptyPage)), operation: "custom", parameters: new(StringComparer.Ordinal))));

            Assert.Contains("'path' parameter", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ObtainsATokenAndReportsSuccess()
        {
            var handler = Handler(_ => Json(EmptyPage));

            var result = await new CiscoDnaCenterConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Appliance, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.True(IsAuthRequest(Assert.Single(handler.Calls).Request));
        }
    }
}
