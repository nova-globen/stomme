#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Taegis;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The Taegis connector cases (12), carried over by name over the port: the scope-less grant, the derived token URL, the verbatim queries, the flattening.</summary>
    public sealed class TaegisConnectorTests
    {
        private const string AlertsPageFull = """
            {"data":{"allAlerts":{"alerts":[
               {"id":"a1","tenant_id":"t1","metadata":{"title":"Sev alert","severity":"high","confidence":0.9},"status":"OPEN","suppressed":false,"entities":[{"type":"host","value":"pc-1"}]},
               {"id":"a2","tenant_id":"t1","metadata":{"title":"Other","severity":"low","confidence":0.2},"status":"CLOSED","suppressed":true,"entities":[]}
             ],"totalCount":3,"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}
            """;

        private const string AlertsPageShort = """
            {"data":{"allAlerts":{"alerts":[{"id":"a3","tenant_id":"t1","metadata":{"title":"Last","severity":"medium","confidence":0.5},"status":"OPEN","suppressed":false,"entities":[]}],"totalCount":3,"pageInfo":{"hasNextPage":false,"endCursor":null}}}}
            """;

        private const string AssetsPage = """{"data":{"allAssets":{"assets":[{"id":"as1","hostname":"srv-1","osFamily":"Windows","tags":["prod","eu"],"lastSeenAt":"2026-07-01T00:00:00Z"}],"totalCount":1}}}""";

        private const string InvestigationsPage = """{"data":{"allInvestigations":{"investigations":[{"id":"i1","priority":2,"status":"Open","alerts_count":5}],"totalCount":1,"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""";

        private static RecordingHandler Handler(Func<HttpRequestMessage, string, HttpResponseMessage> graphQl) =>
            new((request, body, _) => graphQl(request, body)) { TokenRoute = request => request.RequestUri!.AbsolutePath.EndsWith("/auth/api/v2/auth/token", StringComparison.Ordinal) };

        private static Dictionary<string, string> Credentials(bool withTokenUrl = false)
        {
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal) { ["client-id"] = "client", ["client-secret"] = "secret" };
            if (withTokenUrl)
            {
                credentials["token-url"] = "https://api.delta.taegis.secureworks.com/auth/api/v2/auth/token";
            }

            return credentials;
        }

        private static ConnectorExtractContext Extract(RecordingHandler handler, string operation, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, Dictionary<string, string>? credentials = null, string endpoint = "") =>
            Contexts.Extract(new StubEgress(handler), endpoint, operation, parameters, settings, credentials ?? Credentials(), timeoutSeconds: 30);

        private static JsonElement Variables(string body) => JsonDocument.Parse(body).RootElement.GetProperty("variables");

        [Fact]
        public void Declaration_MatchesTheGraphQlPort()
        {
            var connector = new TaegisConnector();

            Assert.Equal(("taegis", AuthenticationType.OAuth2ClientCredentials), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "alerts", "events", "investigations", "endpoint-assets", "custom" }, connector.Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_Alerts_PagesAndFlattensLegacyStyle()
        {
            var handler = Handler((_, body) => Json(body.Contains("\"offset\":0", StringComparison.Ordinal) ? AlertsPageFull : AlertsPageShort));

            var records = await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "alerts", settings: new(StringComparer.Ordinal) { ["api_limit"] = "2" }));

            Assert.Equal(3, records.Count);
            // A nested object becomes parent_child columns; an array of objects stays raw JSON; a scalar bool is typed.
            Assert.Equal("Sev alert", records[0].Fields["metadata_title"]);
            Assert.Equal("high", records[0].Fields["metadata_severity"]);
            Assert.Equal(false, records[0].Fields["suppressed"]);
            Assert.Contains("host", (string?)records[0].Fields["entities"]);

            Assert.Equal(2, handler.Calls.Count);
            var first = Variables(handler.Calls[0].Body).GetProperty("in");
            Assert.Equal((2, 0), (first.GetProperty("limit").GetInt32(), first.GetProperty("offset").GetInt32()));
            Assert.Equal(2, Variables(handler.Calls[1].Body).GetProperty("in").GetProperty("offset").GetInt32());

            // The scope-less grant: the token form carries no scope parameter.
            Assert.All(handler.TokenForms, form => Assert.DoesNotContain("scope=", form));
            Assert.All(handler.Requests, r => Assert.Equal("tok-1", r.Headers.Authorization?.Parameter));
        }

        [Fact]
        public async Task Extract_Alerts_MapsFiltersAndTenantIntoTheInput()
        {
            var handler = Handler((_, _) => Json(AlertsPageShort));

            await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "alerts",
                settings: new(StringComparer.Ordinal) { ["tenant_id"] = "t-42" },
                parameters: new(StringComparer.Ordinal) { ["start_time"] = "2026-07-01T00:00:00Z", ["severity"] = "critical, high", ["status"] = "open" }));

            var input = Variables(handler.Calls[0].Body).GetProperty("in");
            Assert.Equal("2026-07-01T00:00:00Z", input.GetProperty("startTime").GetString());
            Assert.Equal(new[] { "critical", "high" }, input.GetProperty("severity").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("t-42", input.GetProperty("tenantId").GetString());
            Assert.False(input.TryGetProperty("endTime", out _));
        }

        [Fact]
        public async Task Extract_Investigations_UsesPagePerPageVariables()
        {
            var handler = Handler((_, _) => Json(InvestigationsPage));

            var records = await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "investigations"));

            Assert.Single(records);
            var body = JsonDocument.Parse(handler.Calls[0].Body).RootElement;
            Assert.Equal((1, 1000), (body.GetProperty("variables").GetProperty("page").GetInt32(), body.GetProperty("variables").GetProperty("perPage").GetInt32()));
            Assert.Contains("allInvestigations", body.GetProperty("query").GetString());
        }

        [Fact]
        public async Task Extract_EndpointAssets_UsesOffsetLimitAndJoinsScalarArrays()
        {
            var handler = Handler((_, _) => Json(AssetsPage));

            var records = await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "endpoint-assets"));

            var asset = Assert.Single(records);
            Assert.Equal(("srv-1", "prod, eu"), (asset.Fields["hostname"], asset.Fields["tags"]));
            Assert.Equal(0, Variables(handler.Calls[0].Body).GetProperty("offset").GetInt32());
        }

        [Fact]
        public async Task Extract_Custom_RunsVerbatimSinglePage()
        {
            var handler = Handler((_, _) => Json("""{"data":{"myThings":{"things":[{"id":"x1"},{"id":"x2"}]}}}"""));

            var records = await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "custom", parameters: new(StringComparer.Ordinal)
            {
                ["query"] = "query myThings($a: Int) { myThings(a: $a) { things { id } } }",
                ["variables"] = """{"a": 7}""",
            }));

            Assert.Equal(2, records.Count);
            var body = JsonDocument.Parse(Assert.Single(handler.Calls).Body).RootElement;
            Assert.Equal(7, body.GetProperty("variables").GetProperty("a").GetInt32());
            Assert.StartsWith("query myThings", body.GetProperty("query").GetString());
        }

        [Fact]
        public async Task Extract_GraphQlErrors_ThrowWithTheFirstMessage()
        {
            var handler = Handler((_, _) => Json("""{"data":null,"errors":[{"message":"access denied for tenant"}]}"""));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "alerts")));

            Assert.Contains("access denied for tenant", ex.Message);
        }

        [Fact]
        public async Task Extract_DerivesTokenUrlFromEndpointWhenAbsent()
        {
            var handler = Handler((_, _) => Json(AlertsPageShort));

            await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "alerts", credentials: Credentials(withTokenUrl: false), endpoint: "https://api.echo.taegis.secureworks.com"));

            Assert.Single(handler.TokenForms); // the token call reached the derived endpoint-based URL (recorded only when the path matched the route)
            Assert.All(handler.Requests, r => Assert.StartsWith("https://api.echo.taegis.secureworks.com/graphql", r.RequestUri!.AbsoluteUri));
        }

        [Fact]
        public async Task Extract_ExplicitTokenUrlWins()
        {
            var handler = Handler((_, _) => Json(AlertsPageShort));

            await Contexts.DrainAsync(new TaegisConnector(), Extract(handler, "alerts", credentials: Credentials(withTokenUrl: true)));

            Assert.Single(handler.TokenForms);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TaegisConnector(), Extract(Handler((_, _) => Json(AlertsPageShort)), "playbooks")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesAssetsAndSucceeds()
        {
            var handler = Handler((_, _) => Json(AssetsPage));

            var result = await new TaegisConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("allAssets", Assert.Single(handler.Calls).Body);
        }

        [Fact]
        public async Task TestConnection_SurfacesGraphQlErrors()
        {
            var handler = Handler((_, _) => Json("""{"errors":[{"message":"invalid client"}]}"""));

            var result = await new TaegisConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("invalid client", result.Message);
        }
    }
}
