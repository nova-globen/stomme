#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// Raw JSON-RPC over the streamable-HTTP transport - what actually goes over the wire at <c>/mcp/v1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw rather than the SDK client, for two reasons: it asserts the REFUSALS as well as the happy answers (an SDK
    /// client turns an error result into an exception and loses the status code entirely), and it does not need the
    /// client half. The SDK client round-trip is <c>McpProtocolHandshakeTests</c>, still to come.
    /// </para>
    /// <para>
    /// Shared by <see cref="McpEndpointDriveTests"/> and <see cref="McpSiteIsolationTests"/>: the second suite drives
    /// the SAME endpoint through a different alias, and two copies of the transport handling would be two places for
    /// "200 with an SSE body" to be understood differently.
    /// </para>
    /// </remarks>
    internal static class McpRpc
    {
        public const string Endpoint = "/mcp/v1";

        public static JsonObject Body(string method, JsonObject? parameters)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
            if (parameters is not null)
            {
                body["params"] = parameters;
            }

            return body;
        }

        /// <summary>A JSON-RPC call that must answer 200 and carry no <c>error</c> member.</summary>
        public static async Task<JsonNode?> CallAsync(
            HttpClient client, CookieContainer? cookies, string host, string method, JsonObject? parameters, ITestOutputHelper output, string label)
        {
            var response = await PostAsync(client, cookies, host, Body(method, parameters), output, label);
            Assert.Equal(HttpStatusCode.OK, response.Status);

            // Streamable HTTP answers either plain JSON or one SSE event carrying the JSON.
            var payload = response.Body;
            if (payload.Contains("data:", StringComparison.Ordinal))
            {
                payload = string.Join("", payload.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line.Substring(5).Trim()));
            }

            Assert.False(string.IsNullOrWhiteSpace(payload), $"[{label}] the endpoint answered 200 with no body");
            var node = JsonNode.Parse(payload);
            Assert.Null(node?["error"]);
            return node;
        }

        /// <summary>The raw POST, for the cases whose answer is NOT a 200.</summary>
        public static async Task<(HttpStatusCode Status, string Body)> PostAsync(
            HttpClient client, CookieContainer? cookies, string host, JsonObject body, ITestOutputHelper output, string label, string? path = null)
        {
            var uri = new Uri(new Uri("http://" + host), path ?? Endpoint);
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            var header = cookies?.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] POST {uri.AbsolutePath} {body["method"]} -> {(int)response.StatusCode} {GateDrive.Truncate(text)}");
            if (!response.IsSuccessStatusCode)
            {
                foreach (var error in GateDrive.DrainCapturedErrors())
                {
                    output.WriteLine($"[{label}] host log: {error}");
                }
            }

            return (response.StatusCode, text);
        }

        /// <summary>The tool names <c>tools/list</c> offered this caller, on this alias.</summary>
        public static async Task<System.Collections.Generic.List<string?>> ToolNamesAsync(
            HttpClient client, CookieContainer cookies, string host, ITestOutputHelper output, string label)
        {
            var tools = await CallAsync(client, cookies, host, "tools/list", null, output, label);
            return tools?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()).ToList() ?? new();
        }
    }
}
