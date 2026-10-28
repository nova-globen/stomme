#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Cylance;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 3 - the predecessor platform's CylanceConnectorTests (8) by name over the port: the JWT assertion minted and verified, one exchange serving both pages, the credential fields the ApiKeyPair scheme's.</summary>
    public sealed class CylanceConnectorTests
    {
        private const string DevicesPageOne = """
            {"page_number":1,"page_size":1,"total_pages":2,"total_number_of_items":2,
             "page_items":[{"id":"d1","name":"PC-1","state":"Online","policy":{"id":"p1","name":"Default"},"ip_addresses":["10.0.0.4"],"mac_addresses":["aa:bb"]}]}
            """;

        private const string DevicesPageTwo = """
            {"page_number":2,"page_size":1,"total_pages":2,"total_number_of_items":2,
             "page_items":[{"id":"d2","name":"PC-2","state":"Offline","policy":null,"ip_addresses":[],"mac_addresses":[]}]}
            """;

        private static RecordingHandler Handler(Func<HttpRequestMessage, int, HttpResponseMessage> page) =>
            new(page) { TokenRoute = request => request.RequestUri!.AbsolutePath.EndsWith("/auth/v2/token", StringComparison.Ordinal), TokenResponse = () => Json("""{"access_token":"cyl-tok"}""") };

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["access-key"] = "app-id", ["secret-key"] = "app-secret" };

        private static Dictionary<string, string> Settings(string? tenant = "tenant-1") =>
            tenant is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant_id"] = tenant };

        private static ConnectorExtractContext Extract(RecordingHandler handler, string operation = "devices", Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null) =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, settings ?? Settings(), Credentials(), timeoutSeconds: 30);

        private static string Base64UrlDecodePayload(string jwt)
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            return Encoding.UTF8.GetString(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
        }

        [Fact]
        public void Declaration_MatchesTheCylancePort()
        {
            var connector = new CylanceConnector();

            Assert.Equal(("cylance", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "devices", "threats", "zones", "custom" }, connector.Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_MintsAValidJwtAssertionAndPages()
        {
            var handler = Handler((_, call) => Json(call == 0 ? DevicesPageOne : DevicesPageTwo));

            var records = await Contexts.DrainAsync(new CylanceConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["tenant_id"] = "tenant-1", ["api_limit"] = "1" }));

            Assert.Equal(2, records.Count);
            Assert.Equal("PC-1", records[0].Fields["name"]);
            Assert.Equal("Default", records[0].Fields["policy_name"]);
            Assert.Equal("10.0.0.4", records[0].Fields["ip_addresses"]);

            // ONE token exchange serves both pages (the legacy bridge re-used an uncached token instead).
            var tokenBody = Assert.Single(handler.TokenForms);
            var jwt = JsonDocument.Parse(tokenBody).RootElement.GetProperty("auth_token").GetString()!;
            var parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);

            var claims = JsonDocument.Parse(Base64UrlDecodePayload(jwt)).RootElement;
            Assert.Equal("app-id", claims.GetProperty("sub").GetString());
            Assert.Equal("tenant-1", claims.GetProperty("tid").GetString());
            Assert.Equal("http://cylance.com", claims.GetProperty("iss").GetString());
            Assert.True(claims.GetProperty("exp").GetInt64() > claims.GetProperty("iat").GetInt64());

            // The HS256 signature verifies against the application secret.
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes("app-secret"), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
            Assert.Equal(Convert.ToBase64String(expected).TrimEnd('=').Replace('+', '-').Replace('/', '_'), parts[2]);

            var api = handler.Requests.ToList();
            Assert.Equal(2, api.Count);
            Assert.Contains("page=1&page_size=1", api[0].RequestUri!.AbsoluteUri);
            Assert.Contains("page=2&page_size=1", api[1].RequestUri!.AbsoluteUri);
            Assert.All(api, r => Assert.Equal("cyl-tok", r.Headers.Authorization?.Parameter));
        }

        [Fact]
        public async Task Extract_MissingTenantId_FailsLoudlyWithoutCalling()
        {
            var handler = Handler((_, _) => Json(DevicesPageTwo));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CylanceConnector(), Extract(handler, settings: Settings(tenant: null))));

            Assert.Contains("tenant_id", ex.Message);
            Assert.Empty(handler.TokenForms);
        }

        [Fact]
        public async Task Extract_CustomPath_PagesThatResource()
        {
            var handler = Handler((_, _) => Json(DevicesPageTwo));

            await Contexts.DrainAsync(new CylanceConnector(), Extract(handler, "custom", parameters: new(StringComparer.Ordinal) { ["path"] = "/users/v2" }));

            Assert.StartsWith("https://protectapi.cylance.com/users/v2?", handler.Requests.First().RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CylanceConnector(), Extract(Handler((_, _) => Json(DevicesPageTwo)), "detections")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task Extract_OversizedPageSize_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CylanceConnector(), Extract(Handler((_, _) => Json(DevicesPageTwo)), parameters: new(StringComparer.Ordinal) { ["limit"] = "500" })));

            Assert.Contains("between 1 and 200", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesOneDevice()
        {
            var handler = Handler((_, _) => Json(DevicesPageTwo));

            var result = await new CylanceConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, Settings(), Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/devices/v2?page=1&page_size=1", handler.Requests.First().RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_TokenExchangeFailure_SurfacesTheStatus()
        {
            var handler = new RecordingHandler(_ => Json("""{"message":"invalid credentials"}""", HttpStatusCode.Unauthorized));

            var result = await new CylanceConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, Settings(), Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("401", result.Message);
        }
    }
}
