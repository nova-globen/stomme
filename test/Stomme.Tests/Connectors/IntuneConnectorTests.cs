#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Intune;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 3 - the predecessor platform's IntuneConnectorTests (8) by name over the port: the token and every Graph page ride the egress's client; the credential fields are the scheme's.</summary>
    public sealed class IntuneConnectorTests
    {
        // A recorded Graph managedDevices page (a typed subset of the ~45-column projection).
        private const string PageOneJson = """
            {"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#deviceManagement/managedDevices",
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=999&$skiptoken=abc",
             "value":[
               {"id":"d1","deviceName":"PC-1","operatingSystem":"Windows","complianceState":"compliant","totalStorageSpaceInBytes":512000000000,"isEncrypted":true,"lastSyncDateTime":"2026-07-01T10:00:00Z"},
               {"id":"d2","deviceName":"MAC-1","operatingSystem":"macOS","complianceState":"noncompliant","totalStorageSpaceInBytes":256000000000,"isEncrypted":false,"lastSyncDateTime":null}
             ]}
            """;

        private const string PageTwoJson = """
            {"value":[{"id":"d3","deviceName":"PHONE-1","operatingSystem":"iOS","complianceState":"compliant","totalStorageSpaceInBytes":128000000000,"isEncrypted":true,"lastSyncDateTime":"2026-07-02T08:00:00Z"}]}
            """;

        private static RecordingHandler GraphHandler(Func<HttpRequestMessage, int, HttpResponseMessage> graph) =>
            new(graph) { TokenRoute = request => request.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) };

        private static Dictionary<string, string> Credentials(string? secret = "secret") => new(StringComparer.Ordinal)
        {
            ["token-url"] = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token",
            ["client-id"] = "client",
            ["client-secret"] = secret ?? string.Empty,
        };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, Dictionary<string, string>? credentials = null, string operation = "managed-devices") =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, settings, credentials ?? Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheGraphPort()
        {
            var connector = new IntuneConnector();

            Assert.Equal(("intune", AuthenticationType.OAuth2ClientCredentials), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("managed-devices", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_FollowsNextLinkAndTypesFields()
        {
            var handler = GraphHandler((_, call) => Json(call == 0 ? PageOneJson : PageTwoJson));

            var records = await Contexts.DrainAsync(new IntuneConnector(), Extract(handler));

            Assert.Equal(3, records.Count);
            Assert.Equal("PC-1", records[0].Fields["deviceName"]);
            Assert.Equal(512000000000L, records[0].Fields["totalStorageSpaceInBytes"]);
            Assert.Equal(true, records[0].Fields["isEncrypted"]);
            Assert.Null(records[1].Fields["lastSyncDateTime"]);

            var graph = handler.Requests.ToList();
            Assert.Equal(2, graph.Count);
            Assert.Equal("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=999", graph[0].RequestUri!.AbsoluteUri);
            Assert.Contains("$skiptoken=abc", graph[1].RequestUri!.AbsoluteUri);
            Assert.All(graph, r => Assert.Equal(("Bearer", "tok-1"), (r.Headers.Authorization?.Scheme, r.Headers.Authorization?.Parameter)));
        }

        // Graph's next link followed only while it stays on the collection and names another page: one to another host,
        // another scheme, another collection, an item of it, the page itself or no address at all refused, nothing sent there.
        [Theory]
        [InlineData("https://elsewhere.example.test/v1.0/deviceManagement/managedDevices?$skiptoken=abc")]
        [InlineData("http://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=abc")]
        [InlineData("https://graph.microsoft.com/v1.0/users?$skiptoken=abc")]
        [InlineData("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices/d1?$skiptoken=abc")]
        [InlineData("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=999")]
        [InlineData("managedDevices?$skiptoken=abc")]
        public async Task Extract_NextLinkOffTheCollection_IsRefusedWithNothingSentThere(string link)
        {
            var handler = GraphHandler((_, _) => Json(PageOneJson.Replace("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=999&$skiptoken=abc", link, StringComparison.Ordinal)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntuneConnector(), Extract(handler)));

            Assert.Equal("Microsoft Graph named a next page outside the collection - it is not followed.", ex.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Extract_NextLinkInAnotherCase_IsTheSameCollection()
        {
            var handler = GraphHandler((_, call) => Json(call == 0 ? PageOneJson.Replace("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=999&$skiptoken=abc", "https://GRAPH.microsoft.com/v1.0/devicemanagement/manageddevices?$top=999&$skiptoken=abc", StringComparison.Ordinal) : PageTwoJson));

            Assert.Equal(3, (await Contexts.DrainAsync(new IntuneConnector(), Extract(handler))).Count);
        }

        [Fact]
        public async Task Extract_BuildsQueryFromSettingsAndParameters()
        {
            var handler = GraphHandler((_, _) => Json(PageTwoJson));

            await Contexts.DrainAsync(new IntuneConnector(), Extract(handler,
                settings: new(StringComparer.Ordinal) { ["api_limit"] = "500" },
                parameters: new(StringComparer.Ordinal) { ["filter"] = "operatingSystem eq 'Windows'", ["select"] = "id,deviceName" }));

            var url = Assert.Single(handler.Requests).RequestUri!.AbsoluteUri;
            Assert.Contains("$top=500", url);
            Assert.Contains("$filter=operatingSystem%20eq%20%27Windows%27", url);
            Assert.Contains("$select=id%2CdeviceName", url);
        }

        [Fact]
        public async Task Extract_TopParameterOverridesConfiguredLimit()
        {
            var handler = GraphHandler((_, _) => Json(PageTwoJson));

            await Contexts.DrainAsync(new IntuneConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["api_limit"] = "500" }, parameters: new(StringComparer.Ordinal) { ["top"] = "25" }));

            Assert.Contains("$top=25", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_MissingClientSecret_FailsLoudlyWithoutCallingGraph()
        {
            // The legacy MsIntune bridge logged an empty secret and carried on; the port refuses to run. CHANGED: the field is the scheme's, client-secret.
            var handler = GraphHandler((_, _) => Json(PageOneJson));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntuneConnector(), Extract(handler, credentials: Credentials(secret: null))));

            Assert.Contains("client-secret", ex.Message);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new IntuneConnector(), Extract(GraphHandler((_, _) => Json(PageOneJson)), operation: "devices")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesOneDeviceAndSucceeds()
        {
            var handler = GraphHandler((_, _) => Json("""{"value":[]}"""));

            var result = await new IntuneConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("$top=1", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_Forbidden_MentionsApplicationPermission()
        {
            var handler = GraphHandler((_, _) => Json("""{"error":{}}""", HttpStatusCode.Forbidden));

            var result = await new IntuneConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("DeviceManagementManagedDevices.Read.All", result.Message);
        }

        [Fact]
        public async Task TestConnection_MissingCredentials_ReturnsFailure()
        {
            var handler = GraphHandler((_, _) => Json(PageOneJson));

            var result = await new IntuneConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: new Dictionary<string, string>(StringComparer.Ordinal), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("token-url", result.Message);
        }
    }
}
