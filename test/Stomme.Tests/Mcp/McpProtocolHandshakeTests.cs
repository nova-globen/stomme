#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Tests.Fixtures.McpTestModule;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// A REAL MCP client, from the SDK, talking to Stomme's endpoint (D101's handshake gate).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists beside <see cref="McpEndpointDriveTests"/>, which already drives the same verbs.</b> That
    /// suite sends raw JSON-RPC, which is what lets it assert the REFUSALS - an SDK client turns an error result into
    /// an exception and loses the status code entirely. What raw JSON-RPC does NOT prove is that a real client can
    /// complete the protocol: <c>initialize</c>, the version negotiation, the capability exchange, and the shapes the
    /// SDK deserialises into. A hand-written request can be accidentally right where a client is not.
    /// </para>
    /// <para>
    /// The negotiated protocol version is pinned by a RAW initialize beside the client round-trip, because the version
    /// this install answers with is a compatibility fact about the deployment - not an implementation detail - and it
    /// should change deliberately when the SDK is upgraded, not quietly.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpProtocolHandshakeTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpProtocolHandshakeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_real_SDK_client_initializes_lists_and_round_trips_a_tool_and_a_resource()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                using var http = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, http, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // The endpoint admits the Identity cookie and nothing else in Wave 1, so the client carries it as a
                // header - the SDK builds its own requests and never sees a CookieContainer.
                var endpoint = new Uri(new Uri("http://" + Host), McpRpc.Endpoint);
                var options = new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = "Stomme",
                    AdditionalHeaders = new Dictionary<string, string> { ["Cookie"] = cookies.GetCookieHeader(endpoint) },
                };

                var transport = new HttpClientTransport(options, http, loggerFactory: null, ownsHttpClient: false);
                await using var client = await McpClient.CreateAsync(transport);

                // ---- initialize: the server says who it is ---------------------------------------------------------
                _output.WriteLine($"server: {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
                Assert.Equal("Stomme", client.ServerInfo?.Name);

                // ---- list, through the client's own deserialisation ------------------------------------------------
                var tools = await client.ListToolsAsync();
                var names = tools.Select(tool => tool.Name).ToList();
                _output.WriteLine($"tools: {string.Join(", ", names)}");
                Assert.Contains(McpTestContributor.Echo, names);
                Assert.DoesNotContain(McpTestContributor.Never, names);      // a Deny tool is not advertised

                // Every tool the client sees carries a schema it could actually build a call from.
                Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.JsonSchema.ToString())));

                // ---- call, and the handler really runs -------------------------------------------------------------
                var ran = McpTestContributor.Invocations;
                var result = await client.CallToolAsync(McpTestContributor.Echo,
                    new Dictionary<string, object?> { ["text"] = "through the SDK" });
                Assert.NotEqual(true, result.IsError);
                Assert.Contains("through the SDK", string.Join("", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text)));
                Assert.Equal(ran + 1, McpTestContributor.Invocations);

                // ---- resources, and a TEMPLATE read through its arguments -------------------------------------------
                var resources = await client.ListResourcesAsync();
                Assert.Contains(McpTestContributor.Greeting, resources.Select(resource => resource.Uri));

                var templates = await client.ListResourceTemplatesAsync();
                Assert.Contains(McpTestContributor.ItemTemplate, templates.Select(template => template.UriTemplate));

                var read = await client.ReadResourceAsync("mcptest://item/42");
                Assert.Equal("item 42", string.Join("", read.Contents.OfType<ModelContextProtocol.Protocol.TextResourceContents>().Select(content => content.Text)));

                // ---- and the negotiated protocol version, pinned deliberately ---------------------------------------
                var raw = await McpRpc.CallAsync(http, cookies, Host, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "gate", ["version"] = "1.0.0" },
                }, _output, "raw initialize");

                var negotiated = raw?["result"]?["protocolVersion"]?.GetValue<string>();
                _output.WriteLine($"negotiated protocol version: {negotiated}");
                Assert.False(string.IsNullOrWhiteSpace(negotiated));
                Assert.Equal("2025-06-18", negotiated);

                // ---- AND THE NEGATIVE THAT MAKES ALL OF THE ABOVE MEAN SOMETHING ------------------------------------
                // The same client, the same endpoint, WITHOUT the cookie: it cannot complete the handshake at all.
                // Without this the whole suite would pass just as well against an endpoint that admitted everybody.
                // A FRESH baseline: the resource read above increments the same counter, so "ran + 1" would be wrong
                // here by exactly the read - which is what it was, first run.
                var beforeAnonymous = McpTestContributor.Invocations;
                var anonymousTransport = new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = endpoint, Name = "anonymous" }, http, loggerFactory: null, ownsHttpClient: false);

                var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await using var anonymous = await McpClient.CreateAsync(anonymousTransport);
                    await anonymous.ListToolsAsync();
                });
                _output.WriteLine($"anonymous client: {refused.GetType().Name}: {GateDrive.Truncate(refused.Message)}");
                Assert.Equal(beforeAnonymous, McpTestContributor.Invocations);    // nothing of this contributor's ran for it

                // WHAT THE ENDPOINT ACTUALLY ANSWERS AN ANONYMOUS CALLER, PER VERB - measured, because the three
                // differ and only one of them is the decision D101 and the owner settled:
                //
                //   POST   401  the JSON-RPC path, and the status this protocol's authorization flow keys on. It is
                //               the carve-out from this host's usual bare 403, by endpoint METADATA.
                //   GET    404  a STATELESS transport maps no SSE stream, so the verb has no route at all and the SSR
                //   DELETE 404  catch-all answers - the same for session termination. Neither is an auth decision,
                //               and neither should be read as one. (This is also why an SDK client's failure surfaces
                //               as 404 rather than 401: its opening handshake tries the GET first.)
                var answers = new Dictionary<string, int>();
                foreach (var method in new[] { System.Net.Http.HttpMethod.Get, System.Net.Http.HttpMethod.Post, System.Net.Http.HttpMethod.Delete })
                {
                    using var bare = GateDrive.NewClient(app, Host);
                    var probe = new System.Net.Http.HttpRequestMessage(method, endpoint);
                    probe.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                    if (method == System.Net.Http.HttpMethod.Post)
                    {
                        probe.Content = new System.Net.Http.StringContent(
                            McpRpc.Body("tools/list", null).ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    }

                    var answer = await bare.SendAsync(probe);
                    answers[method.Method] = (int)answer.StatusCode;
                    Assert.DoesNotContain(McpTestContributor.Echo, await answer.Content.ReadAsStringAsync());
                }

                _output.WriteLine($"anonymous per verb: {string.Join(", ", answers.Select(entry => entry.Key + "=" + entry.Value))}");
                Assert.Equal(401, answers["POST"]);
                Assert.Equal(404, answers["GET"]);
                Assert.Equal(404, answers["DELETE"]);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
