#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.RedHatSatellite;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 4 - the predecessor platform's RedHatSatelliteConnectorTests (11) by name over the port.</summary>
    public sealed class RedHatSatelliteConnectorTests
    {
        private const string Server = "https://satellite.example.com";

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "admin", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "hosts") =>
            Contexts.Extract(new StubEgress(handler), Server, operation, parameters, settings, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesThePort()
        {
            var connector = new RedHatSatelliteConnector();

            Assert.Equal(("satellite", "Red Hat Satellite", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Contains(ConnectorCategory.NetworkInfrastructure, connector.Categories);
            Assert.Equal((Uri.UriSchemeHttps, Uri.UriSchemeHttps), (connector.WebsiteUrl!.Scheme, connector.ApiDocumentationUrl!.Scheme));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            foreach (var name in new[] { "hosts", "content-views", "tasks", "custom" })
            {
                Assert.Contains(connector.Operations, o => o.Name == name);
            }
        }

        [Fact]
        public async Task Extract_Hosts_PagesUsingSubtotalMetadata()
        {
            var handler = new RecordingHandler(request =>
            {
                var query = request.RequestUri!.Query;
                return Json(query.Contains("page=1&") ? """{"results":[{"id":1,"name":"h1"}],"total":2,"subtotal":2}"""
                    : query.Contains("page=2&") ? """{"results":[{"id":2,"name":"h2"}],"total":2,"subtotal":2}"""
                    : """{"results":[],"total":2,"subtotal":2}""");
            });

            var records = await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["per_page"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal((1L, "h2"), (records[0].Fields["id"], records[1].Fields["name"]));
            Assert.Equal(2, handler.Calls.Count);
            Assert.Contains("/api/v2/hosts?page=1&per_page=1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
            Assert.Contains("page=2&per_page=1", handler.Calls[1].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_KatelloResource_UsesTheKatelloBasePath()
        {
            var handler = new RecordingHandler(_ => Json("""{"results":[{"id":5}]}"""));

            await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, operation: "content-views"));

            Assert.Contains("/katello/api/content_views?", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_TasksResource_UsesTheForemanTasksBasePath()
        {
            var handler = new RecordingHandler(_ => Json("""{"results":[{"id":8}]}"""));

            await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, operation: "tasks"));

            Assert.Contains("/foreman_tasks/api/tasks?", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_Custom_UsesTheRawPath()
        {
            var handler = new RecordingHandler(_ => Json("""{"results":[{"id":3}]}"""));

            await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, operation: "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "/katello/api/products" }));

            Assert.StartsWith($"{Server}/katello/api/products?page=1", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_OrganizationAndSearch_FlowIntoTheQuery()
        {
            var handler = new RecordingHandler(_ => Json("""{"results":[]}"""));

            await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["organization_id"] = "3", ["search"] = "os = RedHat" }));

            var url = handler.Calls[0].Request.RequestUri!.AbsoluteUri;
            Assert.Contains("organization_id=3", url);
            Assert.Contains("search=os%20%3D%20RedHat", url);
        }

        [Fact]
        public async Task Extract_PerPage_IsClampedToOneThousand()
        {
            var handler = new RecordingHandler(_ => Json("""{"results":[]}"""));

            await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["per_page"] = "5000" }));

            Assert.Contains("per_page=1000", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_DataKeyFallback_IsRead()
        {
            var records = await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(new RecordingHandler(_ => Json("""{"data":[{"id":9}],"total":1}"""))));

            Assert.Equal(9L, Assert.Single(records).Fields["id"]);
        }

        [Fact]
        public async Task Extract_SingleObjectWithId_IsOneRecord()
        {
            var records = await Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(new RecordingHandler(_ => Json("""{"id":42,"name":"solo"}"""))));

            Assert.Equal("solo", Assert.Single(records).Fields["name"]);
        }

        [Fact]
        public async Task Extract_Unauthorized_SurfacesTheError()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RedHatSatelliteConnector(), Extract(new RecordingHandler(_ => Json("{}", HttpStatusCode.Unauthorized)))));

            Assert.Contains("401", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesStatus()
        {
            var handler = new RecordingHandler(_ => Json("""{"result":"ok"}"""));

            var result = await new RedHatSatelliteConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Server, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/api/v2/status", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        }
    }
}
