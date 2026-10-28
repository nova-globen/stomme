#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.SuseManager;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>BL-96 increment 4 - the predecessor platform's SuseManagerConnectorTests (9) by name over the port: the XML-RPC session through the egress's client.</summary>
    public sealed class SuseManagerConnectorTests
    {
        private const string Server = "https://suma.example.com";

        private const string LoginResponse = """<?xml version="1.0"?><methodResponse><params><param><value><string>sess-1</string></value></param></params></methodResponse>""";
        private const string LogoutResponse = """<?xml version="1.0"?><methodResponse><params><param><value><int>1</int></value></param></params></methodResponse>""";

        private const string ListOneSystem = """
            <?xml version="1.0"?><methodResponse><params><param><value><array><data>
              <value><struct>
                <member><name>id</name><value><int>1001</int></value></member>
                <member><name>name</name><value><string>host-a</string></value></member>
                <member><name>last_checkin</name><value><dateTime.iso8601>20260101T00:00:00</dateTime.iso8601></value></member>
              </struct></value>
            </data></array></value></param></params></methodResponse>
            """;

        private const string ListTwoSystemsOneInvalid = """
            <?xml version="1.0"?><methodResponse><params><param><value><array><data>
              <value><struct><member><name>id</name><value><int>1001</int></value></member><member><name>name</name><value><string>host-a</string></value></member></struct></value>
              <value><struct><member><name>id</name><value><int>0</int></value></member><member><name>name</name><value><string>never-registered</string></value></member></struct></value>
            </data></array></value></param></params></methodResponse>
            """;

        private const string DetailResponse = """
            <?xml version="1.0"?><methodResponse><params><param><value><array><data>
              <value><struct>
                <member><name>id</name><value><int>1001</int></value></member>
                <member><name>machine_id</name><value>abc</value></member>
                <member><name>virtualized</name><value><boolean>1</boolean></value></member>
                <member><name>network</name><value><struct><member><name>ip</name><value><string>10.0.0.5</string></value></member></struct></value></member>
                <member><name>entitlements</name><value><array><data><value><string>salt</string></value><value><string>monitoring</string></value></data></array></value></member>
              </struct></value>
            </data></array></value></param></params></methodResponse>
            """;

        private const string FaultResponse = """
            <?xml version="1.0"?><methodResponse><fault><value><struct>
              <member><name>faultCode</name><value><int>-1</int></value></member>
              <member><name>faultString</name><value><string>Either the login or password is incorrect.</string></value></member>
            </struct></value></fault></methodResponse>
            """;

        private static HttpResponseMessage Xml(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(payload, Encoding.UTF8, "text/xml") };

        private static RecordingHandler Router(string listResponse) => new((_, body, _) =>
            body.Contains("listActiveSystemsDetails", StringComparison.Ordinal) ? Xml(DetailResponse)
            : body.Contains("auth.login", StringComparison.Ordinal) ? Xml(LoginResponse)
            : body.Contains("auth.logout", StringComparison.Ordinal) ? Xml(LogoutResponse)
            : Xml(listResponse));

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal) { ["username"] = "suma.user", ["password"] = "pw" };

        private static ConnectorExtractContext Extract(RecordingHandler handler, string endpoint = Server, Dictionary<string, string>? settings = null, string operation = "systems") =>
            Contexts.Extract(new StubEgress(handler), endpoint, operation, null, settings, Credentials(), timeoutSeconds: 30);

        private static List<string> Bodies(RecordingHandler handler) => handler.Calls.Select(call => call.Body).ToList();

        [Fact]
        public void Declaration_MatchesThePort()
        {
            var connector = new SuseManagerConnector();

            Assert.Equal(("susemanager", AuthenticationType.Basic), (connector.ConnectorType, connector.AuthType));
            Assert.Contains(ConnectorCategory.NetworkInfrastructure, connector.Categories);
            Assert.Equal((Uri.UriSchemeHttps, Uri.UriSchemeHttps), (connector.WebsiteUrl!.Scheme, connector.ApiDocumentationUrl!.Scheme));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "systems", "active-systems" }, connector.Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_LogsInListsMergesDetailsAndLogsOut()
        {
            var handler = Router(ListOneSystem);

            var records = await Contexts.DrainAsync(new SuseManagerConnector(), Extract(handler));

            var record = Assert.Single(records);
            Assert.Equal(1001L, record.Fields["id"]);
            Assert.Equal("host-a", record.Fields["name"]);
            Assert.Equal(true, record.Fields["virtualized"]); // boolean 1 becomes true
            Assert.Equal("10.0.0.5", record.Fields["network_ip"]); // a nested struct flattened
            Assert.Equal("salt, monitoring", record.Fields["entitlements"]); // an array comma-joined
            Assert.Equal("abc", record.Fields["machine_id"]); // a bare-string value
            Assert.DoesNotContain("calc_os", record.Fields.Keys); // the legacy synthetic column is gone

            var bodies = Bodies(handler);
            Assert.Equal(4, bodies.Count);
            Assert.Contains("auth.login", bodies[0]);
            Assert.Contains("system.listSystems", bodies[1]);
            Assert.Contains("system.listActiveSystemsDetails", bodies[2]);
            Assert.Contains("auth.logout", bodies[3]);
            Assert.Contains("sess-1", bodies[3]); // the logout carries the session key
        }

        [Fact]
        public async Task Extract_ActiveSystemsOperation_CallsListActiveSystems()
        {
            var handler = Router(ListOneSystem);

            await Contexts.DrainAsync(new SuseManagerConnector(), Extract(handler, operation: "active-systems"));

            Assert.Contains("system.listActiveSystems", Bodies(handler)[1]);
        }

        [Fact]
        public async Task Extract_FetchDetailsOff_SkipsTheDetailCall()
        {
            var handler = Router(ListOneSystem);

            var records = await Contexts.DrainAsync(new SuseManagerConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["fetch_details"] = "false" }));

            Assert.Single(records);
            Assert.DoesNotContain(Bodies(handler), b => b.Contains("listActiveSystemsDetails"));
            Assert.Equal(3, handler.Calls.Count);
        }

        [Fact]
        public async Task Extract_SkipsSystemsWithoutAPositiveId()
        {
            var records = await Contexts.DrainAsync(new SuseManagerConnector(), Extract(Router(ListTwoSystemsOneInvalid), settings: new(StringComparer.Ordinal) { ["fetch_details"] = "false" }));

            Assert.Equal(1001L, Assert.Single(records).Fields["id"]);
        }

        [Fact]
        public async Task Extract_Fault_SurfacesTheFaultString()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SuseManagerConnector(), Extract(new RecordingHandler(_ => Xml(FaultResponse)))));

            Assert.Contains("login or password is incorrect", ex.Message);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var handler = Router(ListOneSystem);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SuseManagerConnector(), Extract(handler, operation: "bogus")));

            Assert.Contains("Unknown operation", ex.Message);
            Assert.Empty(handler.Calls); // the operation is rejected before any call
        }

        [Fact]
        public async Task TestConnection_LogsInThenOut()
        {
            var handler = Router(ListOneSystem);

            var result = await new SuseManagerConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), Server, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            var bodies = Bodies(handler);
            Assert.Contains("auth.login", bodies[0]);
            Assert.Contains("auth.logout", bodies[^1]);
        }

        [Fact]
        public async Task Extract_PastedRpcApiEndpoint_IsNotDoubled()
        {
            var handler = Router(ListOneSystem);

            var records = await Contexts.DrainAsync(new SuseManagerConnector(), Extract(handler, endpoint: $"{Server}/rpc/api", settings: new(StringComparer.Ordinal) { ["fetch_details"] = "false" }));

            Assert.Single(records);
            Assert.All(handler.Requests, r => Assert.Equal($"{Server}/rpc/api", r.RequestUri!.AbsoluteUri));
        }
    }
}
