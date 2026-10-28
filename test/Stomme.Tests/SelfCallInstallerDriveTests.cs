#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-234 DRIVEN - the install wizard of a host whose public name it cannot reach (a container with no egress, as
    /// a game app's host was on 2026-10-13: here a name that never resolves, <c>.invalid</c>) renders, because its server
    /// render's call to its own API goes to its own listener. The REAL host on Kestrel, uninstalled, from the shipped
    /// appsettings, asked over its loopback listener with the public host and scheme an edge would forward: with
    /// <c>Installation:InternalUrl</c> set to <c>public</c> in the override file - the address before the fix - the wizard
    /// answers 500 and the API clients cannot reach themselves (the defect, reproduced); with nothing set it renders the
    /// database choices, and the server's API clients - the named one and the scoped one, outside any request as in a
    /// circuit - reach their own API at the public address.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SelfCallInstallerDriveTests(ITestOutputHelper output) : IDisposable
    {
        private const string PublicHost = "play.stomme.invalid";

        private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "stomme-selfcall-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
            try
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private async Task<(WebApplication App, Uri Listener)> StartAsync(string? internalUrl)
        {
            Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
            var shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            File.Copy(shipped, Path.Combine(_contentRoot, "appsettings.json"), overwrite: true);
            var settings = JsonNode.Parse(File.ReadAllText(shipped))!.AsObject();
            if (internalUrl is not null)
            {
                settings["Installation"]!["InternalUrl"] = internalUrl; // in the override FILE, where a deployed install keeps it
            }

            File.WriteAllText(Path.Combine(_contentRoot, "appsettings.override.json"), settings.ToJsonString());
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _contentRoot);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
            var install = await Program.InstallAsync(app);
            Assert.True(!install.Success && string.IsNullOrEmpty(install.Message), $"Expected an uninstalled host, the installer said: {install.Message}");
            await app.StartAsync();
            return (app, new Uri(app.Urls.Single()));
        }

        // The wizard, as an edge forwards it: to the host's listener, for the public host over HTTPS.
        private static async Task<(HttpStatusCode Status, string Body)> WizardAsync(Uri listener)
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(listener, "/"));
            request.Headers.Host = PublicHost;
            request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_wizard_renders_when_its_public_name_cannot_be_reached_and_failed_before()
        {
            // Before the fix - the public address - the wizard's render and the API clients cannot reach themselves.
            var (before, beforeListener) = await StartAsync("public");
            try
            {
                var (status, _) = await WizardAsync(beforeListener);
                output.WriteLine($"[public] wizard {(int)status}");
                Assert.Equal(HttpStatusCode.InternalServerError, status);
                using var scope = before.Services.CreateScope();
                await Assert.ThrowsAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("stomme").GetStringAsync($"https://{PublicHost}/api/Database"));
            }
            finally
            {
                await before.StopAsync();
                await before.DisposeAsync();
            }

            // The fix: to the host's own listener.
            var (after, afterListener) = await StartAsync(null);
            try
            {
                var (status, body) = await WizardAsync(afterListener);
                output.WriteLine($"[listener {afterListener}] wizard {(int)status}");
                Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body[..Math.Min(body.Length, 2000)]}");
                foreach (var database in new[] { "SQLite", "PostgreSQL", "SQL Server", "RavenDB" })
                {
                    Assert.Contains($"<option value=\"{database}\"", body, StringComparison.Ordinal); // the choices the server render asked its own API for
                }

                // The server's API clients outside any request - as behind an Interactive Server circuit - reach their own API.
                using var scope = after.Services.CreateScope();
                var named = await scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("stomme").GetStringAsync($"https://{PublicHost}/api/Database");
                var scoped = await scope.ServiceProvider.GetRequiredService<HttpClient>().GetStringAsync($"https://{PublicHost}/api/Database");
                foreach (var answer in new[] { named, scoped })
                {
                    Assert.Contains("SQLite", JsonNode.Parse(answer)!.AsArray().Select(database => database!["name"]?.GetValue<string>()));
                }
            }
            finally
            {
                await after.StopAsync();
                await after.DisposeAsync();
            }
        }
    }
}
