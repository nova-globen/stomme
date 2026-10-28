#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Tenable;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 5 - the predecessor platform's TenableConnectorTests (8) by name over the port: the export flow through the egress's client, the poll delay a seam. CHANGED: the key pair fields are the scheme's, access-key and secret-key.</summary>
    public sealed class TenableConnectorTests
    {
        private const string Finished = """{"status":"FINISHED","chunks_available":[1,2]}""";

        private static RecordingHandler Handler(Func<HttpRequestMessage, int, HttpResponseMessage> status)
        {
            var statusCalls = 0;
            return new RecordingHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/assets/export" && request.Method == HttpMethod.Post)
                {
                    return Json("""{"export_uuid":"u1"}""");
                }

                if (path.EndsWith("/status", StringComparison.Ordinal))
                {
                    return status(request, statusCalls++);
                }

                if (path.Contains("/chunks/", StringComparison.Ordinal))
                {
                    return Json(path[(path.LastIndexOf('/') + 1)..] == "1"
                        ? """[{"id":"as-1","hostnames":["pc-1","pc-1.corp"],"has_agent":true,"last_seen":"2026-07-01T00:00:00Z","network_interfaces":[{"name":"eth0","mac_address":"aa:bb"}]}]"""
                        : """[{"id":"as-2","hostnames":[],"has_agent":false,"last_seen":null,"network_interfaces":[]}]""");
                }

                return path == "/scans" ? Json("""{"scans":[]}""") : Json("""{"error":"unexpected route"}""", HttpStatusCode.NotFound);
            });
        }

        private static Dictionary<string, string> Credentials(string? secret = "sk") => new(StringComparer.Ordinal) { ["access-key"] = "ak", ["secret-key"] = secret ?? string.Empty };

        private static (TenableConnector Connector, List<TimeSpan> Delays) Connector()
        {
            var delays = new List<TimeSpan>();
            return (new TenableConnector((d, _) => { delays.Add(d); return Task.CompletedTask; }), delays);
        }

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, Dictionary<string, string>? credentials = null, string operation = "assets") =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, settings, credentials ?? Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheExportPort()
        {
            var (connector, _) = Connector();

            Assert.Equal(("tenable", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("assets", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_RunsTheExportFlowAndStreamsChunks()
        {
            var handler = Handler((_, call) => Json(call == 0 ? """{"status":"QUEUED"}""" : Finished));
            var (connector, delays) = Connector();

            var records = await Contexts.DrainAsync(connector, Extract(handler, settings: new(StringComparer.Ordinal) { ["chunk_size"] = "500" }));

            Assert.Equal(2, records.Count);
            Assert.Equal("as-1", records[0].Fields["id"]);
            Assert.Equal("pc-1, pc-1.corp", records[0].Fields["hostnames"]);
            Assert.Equal(true, records[0].Fields["has_agent"]);
            Assert.Contains("eth0", (string?)records[0].Fields["network_interfaces"]);
            Assert.Equal(string.Empty, records[1].Fields["hostnames"]);
            Assert.Single(delays);

            var export = handler.Calls.Single(c => c.Request.Method == HttpMethod.Post);
            Assert.Equal(500, JsonDocument.Parse(export.Body).RootElement.GetProperty("chunk_size").GetInt32());
            Assert.All(handler.Requests, r => Assert.Equal("accessKey=ak; secretKey=sk;", Contexts.Header(r, "X-ApiKeys")));
        }

        [Fact]
        public async Task Extract_StuckExport_TimesOutWithBoundedPolling()
        {
            var (connector, delays) = Connector();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, Extract(Handler((_, _) => Json("""{"status":"QUEUED"}""")), parameters: new(StringComparer.Ordinal) { ["max_wait"] = "00:00:15" })));

            Assert.Contains("did not finish within", ex.Message);
            Assert.Contains("QUEUED", ex.Message);
            Assert.Equal(2, delays.Count);
        }

        [Fact]
        public async Task Extract_ExportError_Throws()
        {
            var (connector, _) = Connector();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, Extract(Handler((_, _) => Json("""{"status":"ERROR"}""")))));

            Assert.Contains("ended in status ERROR", ex.Message);
        }

        [Fact]
        public async Task Extract_MissingSecretKey_FailsLoudlyWithoutCalling()
        {
            var handler = Handler((_, _) => Json(Finished));
            var (connector, _) = Connector();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, Extract(handler, credentials: Credentials(secret: null))));

            Assert.Contains("secret-key", ex.Message);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var (connector, _) = Connector();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, Extract(Handler((_, _) => Json(Finished)), operation: "vulnerabilities")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesScansAndSucceeds()
        {
            var handler = Handler((_, _) => Json(Finished));
            var (connector, _) = Connector();

            var result = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Equal("https://cloud.tenable.com/scans?limit=1", Assert.Single(handler.Calls).Request.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_Unauthorized_MentionsTheKeyPair()
        {
            var result = await new TenableConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new RecordingHandler(_ => Json("""{"error":"invalid keys"}""", HttpStatusCode.Unauthorized))), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("401", result.Message);
        }
    }
}
