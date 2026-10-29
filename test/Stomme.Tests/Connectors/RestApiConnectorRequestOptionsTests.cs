#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.RestApi;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The REST connector request-option cases (13), carried over by name: connection-level headers overlaid
    /// by per-operation headers, the body content type, malformed header lines named. CHANGED (five cases): the
    /// handler-factory and the proxy / certificate-bypass options are gone - <c>HandlerFactory_ReceivesTheParsedNetworkingOptions</c>,
    /// <c>HandlerFactory_ReceivesDefaultOptions_WhenNoNetworkingSettingsAreSet</c>, <c>CreateDefaultHandler_*</c> (2) and
    /// <c>TestConnection_InvalidProxyUrl_FailsWithAnActionableMessage</c> become the port's rule: the client is the
    /// egress's, created once per call with the context's timeout, and a <c>proxy_url</c> setting is an unknown key that
    /// changes nothing.
    /// </summary>
    public sealed class RestApiConnectorRequestOptionsTests
    {
        private const string Endpoint = "https://api.example.com/";

        [Fact]
        public async Task TestConnection_SendsTheConnectionLevelHeaders()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Endpoint, new Dictionary<string, string> { ["headers"] = "X-Api-Key: k1\n\nX-Trace: t-42" }));

            Assert.True(result.Success, result.Message);
            Assert.Equal(("k1", "t-42"), (Contexts.Header(handler.Request, "X-Api-Key"), Contexts.Header(handler.Request, "X-Trace")));
        }

        [Fact]
        public async Task Extract_SendsTheConnectionLevelHeaders()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), Endpoint, "read", settings: new Dictionary<string, string> { ["headers"] = "X-Api-Key: k1" }));
            Assert.Equal("k1", Contexts.Header(handler.Request, "X-Api-Key"));
        }

        [Fact]
        public async Task Extract_OperationHeaders_MergeOverTheConnectionHeaders_CaseInsensitively()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var settings = new Dictionary<string, string> { ["headers"] = "X-Api-Key: from-connection\nX-Keep: kept" };
            var parameters = new Dictionary<string, string> { ["headers"] = "x-api-key: from-operation\nX-Extra: added" };
            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), Endpoint, "read", parameters, settings));

            Assert.Equal(("from-operation", "kept", "added"), (Contexts.Header(handler.Request, "X-Api-Key"), Contexts.Header(handler.Request, "X-Keep"), Contexts.Header(handler.Request, "X-Extra")));
        }

        [Fact]
        public async Task Extract_ContentTypeHeader_IsRoutedToTheContentHeaders()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var parameters = new Dictionary<string, string> { ["method"] = "POST", ["body"] = "<q/>", ["headers"] = "Content-Type: text/xml" };
            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), Endpoint, "read", parameters));

            Assert.Equal("text/xml", handler.BodyContentType);
            Assert.Null(Contexts.Header(handler.Request, "Content-Type"));
        }

        [Fact]
        public async Task TestConnection_MalformedHeaderLine_FailsNamingTheLine()
        {
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.OK)), Endpoint, new Dictionary<string, string> { ["headers"] = "X-Good: 1\nNoColonHere" }));

            Assert.False(result.Success);
            Assert.Contains("NoColonHere", result.Message, StringComparison.Ordinal);
            Assert.Contains("Name: value", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Extract_MalformedHeaderLine_ThrowsNamingTheLine()
        {
            var parameters = new Dictionary<string, string> { ["headers"] = ": value-without-a-name" };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK)), Endpoint, "read", parameters)));
            Assert.Contains(": value-without-a-name", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Extract_ContentTypeParameter_SetsTheBodyContentType()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var parameters = new Dictionary<string, string> { ["method"] = "POST", ["body"] = "a=1&b=2", ["content_type"] = "application/x-www-form-urlencoded" };
            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), Endpoint, "read", parameters));

            Assert.Equal(("application/x-www-form-urlencoded", "a=1&b=2"), (handler.BodyContentType, handler.Body));
        }

        [Fact]
        public async Task Extract_WithoutContentTypeParameter_TheBodyStaysApplicationJson()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(new StubEgress(handler), Endpoint, "read", new Dictionary<string, string> { ["method"] = "POST", ["body"] = "{\"filter\":\"all\"}" }));
            Assert.StartsWith("application/json", handler.BodyContentType);
        }

        // ---- CHANGED: the five networking-option cases become the port's rule --------------------------------------------

        [Fact]
        public async Task The_client_is_the_egress_created_once_per_call_with_the_contexts_timeout()
        {
            var egress = new StubEgress(new StubHandler(HttpStatusCode.OK, "[]"));
            await new RestApiConnector().TestConnectionAsync(Contexts.Test(egress, Endpoint, timeoutSeconds: 7));
            Assert.Equal((1, TimeSpan.FromSeconds(7)), (egress.Clients, egress.LastTimeout));

            await Contexts.DrainAsync(new RestApiConnector(), Contexts.Extract(egress, Endpoint, "read", timeoutSeconds: 9));
            Assert.Equal((2, TimeSpan.FromSeconds(9)), (egress.Clients, egress.LastTimeout));
        }

        [Fact]
        public async Task A_proxy_or_certificate_setting_is_an_unknown_key_that_changes_nothing()
        {
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var settings = new Dictionary<string, string> { ["proxy_url"] = "http://proxy.corp:8080/", ["danger_accept_any_certificate"] = "true" };
            var result = await new RestApiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Endpoint, settings));

            Assert.True(result.Success, result.Message);
            Assert.DoesNotContain(new RestApiConnector().ConfigSchema.Fields, field => field.Name is "proxy_url" or "danger_accept_any_certificate");
            Assert.Equal(Endpoint, handler.Request?.RequestUri?.ToString()); // the request went where the endpoint says, through the egress's client
        }
    }
}
