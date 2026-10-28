#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.PostgreSql;
using Stomme.Modules.Connectors.RestApi;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 1 - the predecessor platform's RestApiConnectorExtractTests (8) by name: the REST connector's read operation streams canonical records; the two connectors declare their capabilities; the record JSON helper (now in the Shared contract).</summary>
    public sealed class RestApiConnectorExtractTests
    {
        [Fact]
        public async Task Extract_TopLevelJsonArray_YieldsCanonicalRecords()
        {
            var records = await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, "[{\"id\":1,\"name\":\"alpha\"},{\"id\":2,\"name\":\"beta\"}]")), "https://api.example.com/devices", "read"));

            Assert.Equal(2, records.Count);
            Assert.Equal((1L, "alpha", "beta"), (records[0].Fields["id"], records[0].Fields["name"], records[1].Fields["name"]));
        }

        [Fact]
        public async Task Extract_ObjectWithValuesNode_YieldsCanonicalRecords()
        {
            var record = Assert.Single(await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, "{\"total\":1,\"values\":[{\"host\":\"h1\",\"up\":true}]}")), "https://api.example.com/inventory", "read")));
            Assert.Equal(("h1", true), (record.Fields["host"], record.Fields["up"]));
        }

        [Fact]
        public async Task Extract_NamedRecordsPath_SelectsTheArrayProperty()
        {
            var record = Assert.Single(await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, "{\"result\":{\"items\":1},\"rows\":[{\"x\":9}]}")), "https://api.example.com/q", "read", new Dictionary<string, string> { ["records_path"] = "rows" })));
            Assert.Equal(9L, record.Fields["x"]);
        }

        [Fact]
        public async Task Extract_PostWithBodyAndBearerToken_SendsThemAndReadsRows()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[{\"ok\":1}]");
            var parameters = new Dictionary<string, string> { ["method"] = "POST", ["body"] = "{\"filter\":\"all\"}" };
            var records = await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), "https://api.example.com/query", "read", parameters, credentials: new Dictionary<string, string> { ["token"] = "tok123" }));

            Assert.Single(records);
            Assert.Equal((HttpMethod.Post, "Bearer", "tok123", "{\"filter\":\"all\"}"), (handler.Request?.Method, handler.Request?.Headers.Authorization?.Scheme, handler.Request?.Headers.Authorization?.Parameter, handler.Body));
        }

        [Fact]
        public async Task Extract_NonSuccessStatus_Throws() =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.InternalServerError, "boom")), "https://api.example.com/x", "read")));

        [Fact]
        public async Task Send_PostsTheBodyWithTheBearerToken_AndAnswersOneRecordWithTheStatusAndTheBody()
        {
            // BL-95: a request whose answer is not rows - here an object - as ONE record; POST when no method is named.
            var handler = new StubHandler(HttpStatusCode.Created, "{\"id\":5}");
            var parameters = new Dictionary<string, string> { ["path"] = "/v1/events", ["body"] = "{\"event\":\"deployed\"}" };
            var record = Assert.Single(await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), "https://api.example.com", "send", parameters, credentials: new Dictionary<string, string> { ["token"] = "tok123" })));

            Assert.Equal((201L, "{\"id\":5}"), (record.Fields["status"], record.Fields["body"]));
            Assert.Equal((HttpMethod.Post, "https://api.example.com/v1/events", "Bearer", "tok123", "{\"event\":\"deployed\"}"), (handler.Request?.Method, handler.Request?.RequestUri?.ToString(), handler.Request?.Headers.Authorization?.Scheme, handler.Request?.Headers.Authorization?.Parameter, handler.Body));
        }

        [Fact]
        public async Task Send_NonSuccessStatus_Throws() =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.Conflict, "{}")), "https://api.example.com/x", "send")));

        [Fact]
        public void Rest_DeclaresExtractCapability_AndAReadOperation()
        {
            IConnector connector = new RestApiConnector();
            Assert.True(connector is IConnectorExtractor);
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.TestConnection));
            // BL-95 (2026-10-03) added "send" beside the predecessor platform's single "read", which stays first: an extraction naming no operation reads.
            var operations = ((IConnectorExtractor)connector).Operations;
            Assert.Equal(new[] { "read", "send" }, operations.Select(operation => operation.Name));
            Assert.All(operations, operation => Assert.Contains(operation.Parameters.Fields, f => f.Name == "method" && f.Type == ConnectorConfigFieldType.Select));
        }

        [Fact]
        public void Postgres_DeclaresExtractCapability_AndAQueryOperation()
        {
            IConnector connector = new PostgreSqlConnector();
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(ConnectorEndpointKind.ConnectionString, connector.EndpointKind);
            var operation = Assert.Single(((IConnectorExtractor)connector).Operations);
            Assert.Equal("query", operation.Name);
            Assert.Contains(operation.Parameters.Fields, f => f.Name == "query" && f.Required);
        }

        [Fact]
        public void ConnectorRecordJson_SerializesRecords_WithNativeJsonValueTypes()
        {
            var records = new List<ConnectorRecord> { new(new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "alpha", ["up"] = true, ["ratio"] = 1.5, ["note"] = null }) };
            Assert.Equal("[{\"id\":1,\"name\":\"alpha\",\"up\":true,\"ratio\":1.5,\"note\":null}]", ConnectorRecordJson.Serialize(records));
        }
    }
}
