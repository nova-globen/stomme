#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A request to a switched-off feature's API, or to no API at all, answers 404 whatever its method.
    /// </summary>
    /// <remarks>
    /// A config-gated controller declines its routes at route selection (an <c>ActionMethodSelectorAttribute</c>), so the
    /// router answers 404 instead of the authorization policy's 403 (the DataExport posture). For a GET that held. A POST fell
    /// through to the Blazor page endpoint, which refuses a JSON body: 400, "The request has an incorrect Content-type" -
    /// found while using the pipeline editor on a WebAssembly site with Collaboration off, its panel hiding on the
    /// refusal and the browser's console carrying the 400. An unmatched <c>api/</c> request now ends at an endpoint of its own,
    /// a bare 404, never the site's page.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SwitchedOffApiNotFoundTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly (HttpMethod Method, string Path)[] Requests =
        {
            (HttpMethod.Post, "/api/Collaboration/sessions"),
            (HttpMethod.Post, "/api/DataExport"),
            (HttpMethod.Post, "/api/Portability/mine"),
            (HttpMethod.Post, "/api/Help/preview"),
            (HttpMethod.Post, "/api/AiAssistant/conversations"),
            (HttpMethod.Post, "/api/Backup"),
            (HttpMethod.Post, "/api/Mcp/servers"),
            (HttpMethod.Put, "/api/AiAssistant/settings"),
            (HttpMethod.Post, "/api/NoSuchController"),
            (HttpMethod.Post, "/api/NoSuchController/any/depth"),
        };

        private static readonly string[] Reads =
        {
            "/api/Collaboration/sessions",
            "/api/DataExport/mine",
            "/api/Mcp/published",
            "/api/NoSuchController",
        };

        [Fact]
        public async Task A_switched_off_api_answers_404_to_every_method()
        {
            var settings = new JsonObject
            {
                ["Collaboration"] = new JsonObject { ["Enabled"] = false },
                ["DataExport"] = new JsonObject { ["Enabled"] = false },
                ["Portability"] = new JsonObject { ["Enabled"] = false },
                ["Help"] = new JsonObject { ["Enabled"] = false },
                ["AiAssistant"] = new JsonObject { ["Enabled"] = false },
                ["Backup"] = new JsonObject { ["Enabled"] = false },
                ["Mcp"] = new JsonObject { ["Enabled"] = false, ["Server"] = new JsonObject { ["Enabled"] = false } },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                foreach (var (method, path) in Requests)
                {
                    var response = await GateDrive.SendJsonAsync(app, client, null, Host, null, method, path, new { Name = "x" }, output, $"{method} {path}");
                    Assert.True(response.Status == HttpStatusCode.NotFound, $"{method} {path} answered {(int)response.Status}, not 404: {response.Body}");
                }

                foreach (var path in Reads)
                {
                    var response = await GateDrive.GetAsync(client, null, Host, path, output, $"GET {path}");
                    Assert.True(response.Status == HttpStatusCode.NotFound, $"GET {path} answered {(int)response.Status}, not 404");
                    Assert.DoesNotContain("text/html", response.ContentType ?? "");
                }

                // The page endpoint is untouched: a page still renders.
                var home = await GateDrive.GetAsync(client, null, Host, "/", output, "the home page");
                Assert.Equal(HttpStatusCode.OK, home.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A POST carrying a valid antiforgery token to a path nothing serves - the dashboards
        /// hub with the real-time transport off, a made-up path - answered 500 from the page endpoint. It is a 404.
        /// </summary>
        [Fact]
        public async Task A_post_to_a_path_nothing_serves_answers_404_never_500()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                foreach (var path in new[] { "/hubs/v1/dashboards/negotiate?negotiateVersion=1", "/nosuch/path/x", "/admin/nosuchpage" })
                {
                    var response = await GateDrive.SendJsonAsync(app, client, null, Host, null, HttpMethod.Post, path, null, output, $"POST {path}");
                    Assert.True(response.Status == HttpStatusCode.NotFound, $"POST {path} answered {(int)response.Status}, not 404: {response.Body}");
                }

                // A FORM posted to a page with no such form is the page endpoint's to refuse - with its own status, not the
                // 404 page re-run as the POST (which answered 500).
                var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app);
                var formPost = new HttpRequestMessage(HttpMethod.Post, new Uri("http://" + Host + "/nosuch/form"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = requestToken, ["field"] = "x" }),
                };
                formPost.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
                var formResponse = await client.SendAsync(formPost);
                output.WriteLine($"form POST /nosuch/form -> {(int)formResponse.StatusCode}");
                Assert.True((int)formResponse.StatusCode is >= 400 and < 500, $"a form POST to a page without that form answered {(int)formResponse.StatusCode}");

                // As observed on 10.7.0: the same requests WITHOUT an antiforgery token - what a bot or a stray
                // client sends. Every POST above carries a valid token, so this suite could not see that the fallback's
                // HasFormContentType threw on the invalid token feature: 500, and an Error in the event log, for each.
                foreach (var (path, contentType) in new[]
                {
                    ("/nosuch/path/y", "application/json"),
                    ("/nosuch/path/y", "text/plain"),
                    ("/nosuch/path/y", null),
                    ("/hubs/v1/dashboards/negotiate?negotiateVersion=1", "text/plain"),
                })
                {
                    var bare = new HttpRequestMessage(HttpMethod.Post, new Uri("http://" + Host + path));
                    if (contentType is not null)
                    {
                        bare.Content = new StringContent("{}", System.Text.Encoding.UTF8, contentType);
                    }

                    var answer = await client.SendAsync(bare);
                    output.WriteLine($"token-less POST {path} [{contentType ?? "no body"}] -> {(int)answer.StatusCode}");
                    Assert.True(answer.StatusCode == HttpStatusCode.NotFound, $"a token-less POST {path} [{contentType ?? "no body"}] answered {(int)answer.StatusCode}, not 404");
                }

                // A token-less FORM goes on to the page endpoint, which refuses it itself - a 400, as 10.6.0 answered.
                var bareForm = await client.PostAsync(new Uri("http://" + Host + "/nosuch/form"),
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["field"] = "x" }));
                output.WriteLine($"token-less form POST /nosuch/form -> {(int)bareForm.StatusCode}");
                Assert.True((int)bareForm.StatusCode is >= 400 and < 500, $"a token-less form POST answered {(int)bareForm.StatusCode}");

                var home = await GateDrive.GetAsync(client, null, Host, "/", output, "the home page");
                Assert.Equal(HttpStatusCode.OK, home.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
