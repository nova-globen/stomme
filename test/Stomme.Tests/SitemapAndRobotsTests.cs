#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// robots.txt and the sitemap (BL-265, measured by the website on 10.69.0): a configured <c>Sitemap:</c> line is not
    /// doubled, a folder site's <c>/folder/sitemap.xml</c> lists that site's pages and not the root's, and no sitemap lists
    /// the search page.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SitemapAndRobotsTests
    {
        private readonly ITestOutputHelper _output;

        public SitemapAndRobotsTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(null, "User-agent: *\n\nSitemap: https://example.com/sitemap.xml")]
        [InlineData("User-agent: *\nDisallow: /admin", "User-agent: *\nDisallow: /admin\n\nSitemap: https://example.com/sitemap.xml")]
        [InlineData("User-agent: *\nSitemap: https://example.com/other.xml", "User-agent: *\nSitemap: https://example.com/other.xml")]
        [InlineData("User-agent: *\nsitemap: https://example.com/other.xml", "User-agent: *\nsitemap: https://example.com/other.xml")]
        public void Robots_adds_a_sitemap_line_only_when_none_is_configured(string? configured, string expected) =>
            Assert.Equal(expected, TenantMiddleware.RobotsText(configured, "https", "example.com"));

        [Fact]
        public async Task A_folder_sites_sitemap_lists_its_own_pages_and_none_lists_the_search_page()
        {
            using var scratch = AuthorityScratch.Sqlite();
            using var env = new FolderSiteCookieTests.EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Delato",
                ["Installation__Sites__0__Aliases"] = "localhost/delato"
            });
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = GateDrive.NewClient(app, "localhost");

                // The root first: a sitemap cached for the root must not be what the folder site answers.
                var root = await LocationsAsync(client, "http://localhost/sitemap.xml");
                var folder = await LocationsAsync(client, "http://localhost/delato/sitemap.xml");

                Assert.NotEmpty(root);
                Assert.NotEmpty(folder);
                Assert.All(root, url => Assert.DoesNotContain("/delato", url));
                Assert.All(folder, url => Assert.StartsWith("http://localhost/delato", url));
                Assert.DoesNotContain(root.Concat(folder), url => url.TrimEnd('/').EndsWith("/search", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<List<string>> LocationsAsync(System.Net.Http.HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            var xml = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
            var locations = Regex.Matches(xml, "<loc>([^<]+)</loc>").Select(match => match.Groups[1].Value).ToList();
            _output.WriteLine($"{url}: {string.Join(", ", locations)}");
            return locations;
        }
    }
}
