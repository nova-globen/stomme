#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A WebAssembly site that is not prerendered shows a loading status until its page renders, never a blank page.
    /// </summary>
    /// <remarks>
    /// Measured: the home page's body is one WebAssembly root component and nothing else - prerendering
    /// is off, so a visitor saw NOTHING until the client had downloaded and started: 238 files,
    /// 57.4 MB decoded (17.3 MB over the wire on that load), 4.7 seconds on a warm cache, "5-10 seconds of blank"
    /// on a cold one. The page now carries a status, hidden by app.css once the root component has rendered its wrapper. Not
    /// by the JS initializer's afterWebAssemblyStarted: Blazor calls it when the client's Main first yields (its boot code,
    /// measured in blazor.web.js 10.0.12), before Stomme's Main has streamed the module assemblies and long before a
    /// render. A Server site, whose first byte already carries the page, carries none, and nor does a prerendered WebAssembly
    /// site - checked on a real host, because under this harness's TestServer a prerendered WebAssembly home page answers an
    /// empty 500 that nothing logs (the same settings under <c>dotnet run</c> render it, 200 and 100 KB).
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class WebAssemblyBootStatusTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Theory]
        [InlineData("WebAssembly", "false", true)]
        [InlineData("Server", "false", false)]
        public async Task Only_a_webassembly_site_that_is_not_prerendered_shows_the_loading_status(string runtime, string prerender, bool expected)
        {
            var settings = new JsonObject { ["RenderMode"] = "Interactive", ["Runtime"] = runtime, ["Prerender"] = prerender };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var home = await GateDrive.GetAsync(client, null, Host, "/", output, $"a {runtime} home page, prerender {prerender}");
                if (home.Status != HttpStatusCode.OK)
                {
                    foreach (var error in GateDrive.DrainCapturedErrors())
                    {
                        output.WriteLine($"host log: {error}");
                    }

                    foreach (var error in await GateDrive.LastErrorsAsync(app, Host))
                    {
                        output.WriteLine($"site log: {error}");
                    }
                }

                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("blazor.web.js", home.Body);

                var status = Regex.Match(home.Body, "<div id=\"db-boot\"[^>]*>");
                Assert.Equal(expected, status.Success);
                if (expected)
                {
                    Assert.Contains("role=\"status\"", status.Value);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [InlineData("src", "Stomme.Server", "wwwroot", "css", "app.css")]
        [InlineData("src", "Stomme.Maui", "wwwroot", "css", "app.css")]
        public void The_status_is_hidden_once_the_root_component_has_rendered(params string[] path)
        {
            var root = RepositoryRoot();
            var css = File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
            var rule = Regex.Match(css, @"(?m)^body:has\(\.(?<app>[\w-]+)\)\s+#db-boot\s*\{(?<body>[^}]*)\}");
            Assert.True(rule.Success, "app.css carries no `body:has(.<app>) #db-boot` rule: the loading status would stay above the page.");
            Assert.Matches(@"display:\s*none", rule.Groups["body"].Value);

            var routes = File.ReadAllText(Path.Combine(root, "src", "Stomme.Client", "UI", "Routes.razor"));
            Assert.Contains($"class=\"{rule.Groups["app"].Value}\"", routes);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
