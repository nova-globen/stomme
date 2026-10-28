#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Tests.Fixtures.McpTestModule;
using Stomme.Tests.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-250 - what a request made by ANOTHER page in the signed-in user's browser can do. The sign-in cookie is SameSite=Lax,
    /// so a different site never sends it - but a page on a sibling host of the same site does (another tenant's subdomain
    /// under the same domain), and a browser lets such a page send, without a CORS preflight, a POST whose content type is
    /// <c>text/plain</c>, <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c> and whose only other headers
    /// are the CORS-safelisted ones (an <c>Accept</c> included). Anything else needs a preflight, which this host's CORS
    /// policy refuses. Driven: the MCP endpoint must not run a tool for such a request, and the API's antiforgery check must
    /// not be skipped because a request carries an <c>Authorization</c> header while its cookie is what signs it in.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CrossSiteRequestDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_mcp_endpoint_runs_no_tool_for_a_request_another_page_could_send_with_the_users_cookie()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var call = McpRpc.Body("tools/call", new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject { ["text"] = "from another page" } });

                // The control: the same call as a real client sends it runs the tool.
                var before = McpTestContributor.Invocations;
                await McpRpc.CallAsync(client, cookies, Host, "tools/call", (JsonObject)call["params"]!.DeepClone(), output, "a real client's call");
                Assert.Equal(before + 1, McpTestContributor.Invocations);

                foreach (var contentType in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x" })
                {
                    var ran = McpTestContributor.Invocations;
                    var answer = await PostAsync(client, cookies, McpRpc.Endpoint, call.ToJsonString(), contentType, extraHeader: null, output, "simple request, " + contentType);
                    Assert.NotEqual(HttpStatusCode.OK, answer.Status);
                    Assert.Equal(ran, McpTestContributor.Invocations);
                    Assert.DoesNotContain("from another page", answer.Body);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_authorization_header_does_not_excuse_a_cookie_signed_request_from_the_antiforgery_check()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                const string body = "{\"name\":\"Unwanted board\",\"columns\":12,\"rowHeightPx\":40}";

                var withoutToken = await PostAsync(client, cookies, "/api/Dashboards", body, "application/json", extraHeader: null, output, "no antiforgery token");
                Assert.Equal(HttpStatusCode.BadRequest, withoutToken.Status);

                var withBogusBearer = await PostAsync(client, cookies, "/api/Dashboards", body, "application/json", ("Authorization", "Bearer not-a-token"), output, "no token, a bogus bearer");
                Assert.Equal(HttpStatusCode.BadRequest, withBogusBearer.Status);
                Assert.DoesNotContain("Unwanted board", withBogusBearer.Body);

                // A bearer with NO cookie is what the skip is for, and a bogus one signs nobody in: refused, not created.
                using var bare = GateDrive.NewClient(app, Host);
                var bearerOnly = await PostAsync(bare, cookies: null, "/api/Dashboards", body, "application/json", ("Authorization", "Bearer not-a-token"), output, "a bogus bearer alone");
                Assert.NotEqual(HttpStatusCode.OK, bearerOnly.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, System.Net.CookieContainer? cookies, string path, string body, string contentType,
            (string Name, string Value)? extraHeader, ITestOutputHelper output, string label)
        {
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8) };
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            var cookieHeader = cookies?.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            if (extraHeader is { } header)
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] POST {path} ({contentType}) -> {(int)response.StatusCode} {GateDrive.Truncate(text)}");
            return (response.StatusCode, text);
        }
    }
}
