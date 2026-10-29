#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Entuity;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The EntuityConnectorTests cases (11), kept by name over the port.</summary>
    public sealed class EntuityConnectorTests
    {
        private const string Server = "https://entuity.example.com";

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "entuity.user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "inventory") =>
            Contexts.Extract(new StubEgress(handler), Server, operation, parameters, settings, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesThePort()
        {
            var connector = new EntuityConnector();

            Assert.Equal(("entuity", AuthenticationType.Basic), (connector.ConnectorType, connector.AuthType));
            Assert.Contains(ConnectorCategory.NetworkInfrastructure, connector.Categories);
            Assert.Equal(Uri.UriSchemeHttps, connector.WebsiteUrl!.Scheme);
            Assert.Null(connector.ApiDocumentationUrl);
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            foreach (var name in new[] { "inventory", "view-objects", "custom" })
            {
                Assert.Contains(connector.Operations, o => o.Name == name);
            }
        }

        [Fact]
        public async Task Extract_Inventory_ProbesTheKeyedArrayOverApiV2()
        {
            var handler = new RecordingHandler(_ => Json("""{"inventory":[{"id":1,"name":"router-1"}]}"""));

            var records = await Contexts.DrainAsync(new EntuityConnector(), Extract(handler));

            var record = Assert.Single(records);
            Assert.Equal(("router-1", 1L), (record.Fields["name"], record.Fields["id"]));
            Assert.Contains("/api/v2/inventory", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", handler.Calls[0].Request.Headers.Authorization?.Scheme);
        }

        [Fact]
        public async Task Extract_BareArrayResponse_IsRead()
        {
            var handler = new RecordingHandler(_ => Json("""[{"id":2},{"id":3}]"""));

            var records = await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, operation: "servers"));

            Assert.Equal(2, records.Count);
            Assert.Contains("/api/v2/servers", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_ApiV2Off_UsesLegacyApiBase()
        {
            var handler = new RecordingHandler(_ => Json("[]"));

            await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["use_api_v2"] = "false" }));

            var url = handler.Calls[0].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("/api/inventory", url);
            Assert.DoesNotContain("/api/v2/", url);
        }

        [Fact]
        public async Task Extract_ViewObjects_RequiresViewId()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new EntuityConnector(), Extract(new RecordingHandler(_ => Json("[]")), operation: "view-objects", parameters: new(StringComparer.Ordinal))));

            Assert.Contains("'view_id' parameter", ex.Message);
        }

        [Fact]
        public async Task Extract_ViewObjects_BuildsTheViewScopedPath()
        {
            var handler = new RecordingHandler(_ => Json("""{"objects":[{"id":7}]}"""));

            var records = await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, operation: "view-objects", parameters: new(StringComparer.Ordinal) { ["view_id"] = "v9" }));

            Assert.Single(records);
            Assert.Contains("/api/v2/views/v9/objects", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_Incidents_AppliesStateFilterButNotForAll()
        {
            var handler = new RecordingHandler(_ => Json("[]"));

            await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, operation: "incidents", parameters: new(StringComparer.Ordinal) { ["state"] = "Open" }));
            Assert.Contains("state=open", handler.Calls[0].Request.RequestUri!.AbsoluteUri);

            await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, operation: "incidents", parameters: new(StringComparer.Ordinal) { ["state"] = "all" }));
            Assert.DoesNotContain("state=", handler.Calls[1].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_Custom_UsesTheRawPathWithoutApiBase()
        {
            var handler = new RecordingHandler(_ => Json("""{"id":5,"kind":"port"}"""));

            var records = await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, operation: "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "/api/v2/objects/9/ports" }));

            Assert.Single(records);
            Assert.Equal($"{Server}/api/v2/objects/9/ports", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_ServerId_FlowsIntoTheQuery()
        {
            var handler = new RecordingHandler(_ => Json("[]"));

            await Contexts.DrainAsync(new EntuityConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["server_id"] = "srv-1" }));

            Assert.Contains("serverId=srv-1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_Unauthorized_SurfacesTheError()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new EntuityConnector(), Extract(new RecordingHandler(_ => Json("{}", HttpStatusCode.Unauthorized)))));

            Assert.Contains("401", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesInfo()
        {
            var handler = new RecordingHandler(_ => Json("""{"version":"23.1"}"""));

            var result = await new EntuityConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Server, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/api/v2/info", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }
    }
}
