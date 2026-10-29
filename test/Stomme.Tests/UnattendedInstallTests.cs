#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The unattended install a public container needs: the host password from a FILE (a container secret), and no
    /// browser wizard for a stranger on a host whose <c>Installation</c> section asks for an unattended install.
    /// </summary>
    /// <remarks>
    /// Each case boots the real <c>Program</c> over a scratch content root whose <c>appsettings.override.json</c> is the
    /// only configuration, as a container's mounted file would be.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class UnattendedInstallTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private const string FilePassword = "F1le!Secret$2026";
        private const string WeakPassword = "weakpassword";
        private const string InstallerMarker = "db-installer";

        private readonly ITestOutputHelper _output;

        public UnattendedInstallTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_host_password_is_read_from_HostPasswordFile_and_the_host_signs_in_with_it()
        {
            using var scratch = new Scratch();
            var passwordFile = scratch.WriteSecret("host-password", FilePassword + "\n"); // a secret file ends with a line break
            scratch.WriteOverride(withConnectionString: true, new JsonObject
            {
                ["DefaultAlias"] = "localhost",
                ["HostUsername"] = GateDrive.HostUsername,
                ["HostPasswordFile"] = passwordFile,
                ["HostEmail"] = GateDrive.HostEmail,
                ["HostName"] = "Host"
            });

            var app = await BuildAsync(scratch);
            try
            {
                var install = await InstallAsync(app);
                Assert.True(install.Success, $"the unattended install failed: {install.Message}");

                await app.StartAsync();
                using var client = app.GetTestClient();
                // LoginAsync asserts the sign-in succeeded; the password is the file's, without its line break.
                await GateDrive.LoginAsync(app, client, "localhost", GateDrive.HostUsername, FilePassword, _output);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [InlineData("missing", "names a file that does not exist")]
        [InlineData("empty", "names an empty file")]
        [InlineData("both", "are both set")]
        [InlineData("weak", "Installation:HostPasswordFile fails the password policy")]
        public async Task A_bad_HostPasswordFile_refuses_the_install_by_name(string fault, string expected)
        {
            using var scratch = new Scratch();
            var installation = new JsonObject
            {
                ["DefaultAlias"] = "localhost",
                ["HostEmail"] = GateDrive.HostEmail,
                ["HostPasswordFile"] = fault switch
                {
                    "missing" => Path.Combine(scratch.ContentRoot, "no-such-file"),
                    "empty" => scratch.WriteSecret("empty", "\n"),
                    "weak" => scratch.WriteSecret("host-password", WeakPassword + "\n"), // no digit, no symbol, no capital
                    _ => scratch.WriteSecret("host-password", FilePassword)
                }
            };
            if (fault == "both")
            {
                installation["HostPassword"] = "Inl1ne!Secret$2026";
            }
            scratch.WriteOverride(withConnectionString: true, installation);

            var app = await BuildAsync(scratch);
            try
            {
                var install = await InstallAsync(app);
                _output.WriteLine($"[{fault}] {install.Message}");
                Assert.False(install.Success);
                Assert.Contains("Installation:HostPassword", install.Message);
                Assert.Contains(expected, install.Message);
                Assert.DoesNotContain(FilePassword, install.Message);
                Assert.DoesNotContain("Inl1ne", install.Message);
                Assert.DoesNotContain(WeakPassword, install.Message);
                if (fault == "weak")
                {
                    // Nothing was installed: the next boot still refuses, rather than coming up with no host account.
                    Assert.False((await InstallAsync(app)).Success);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_unattended_configuration_refuses_the_wizard_page_and_its_api()
        {
            // The Installation section is there, the connection string is not: the unattended install cannot run, and before
            // the fix the browser wizard was offered to whoever reached the host first.
            using var scratch = new Scratch();
            scratch.WriteOverride(withConnectionString: false, new JsonObject
            {
                ["DefaultAlias"] = "localhost",
                ["HostEmail"] = GateDrive.HostEmail,
                ["HostPasswordFile"] = scratch.WriteSecret("host-password", FilePassword)
            });

            var (page, api) = await ProbeWizardAsync(scratch);
            Assert.DoesNotContain(InstallerMarker, page);
            Assert.Contains("Configured For An Unattended Installation", page);
            Assert.DoesNotContain(FilePassword, page);
            Assert.Equal("Installation Not Authorized", api);
        }

        [Fact]
        public async Task Without_an_unattended_configuration_the_wizard_is_offered_the_control()
        {
            using var scratch = new Scratch();
            scratch.WriteOverride(withConnectionString: false, installation: null);

            var (page, api) = await ProbeWizardAsync(scratch);
            // The page goes on to the installer component. Its markup cannot be asserted here: the installer's prerender
            // reads the database list from the host's own API over HTTP, which a TestServer has no listener for, so it
            // fails in this harness alone. What matters is that the refusal is not what renders.
            Assert.DoesNotContain("Configured For An Unattended Installation", page);
            // The call reached the installer, which rejects this deliberately empty configuration on its own terms.
            Assert.Equal("Invalid Installation Configuration", api);
        }

        private async Task<(string Page, string ApiMessage)> ProbeWizardAsync(Scratch scratch)
        {
            var app = await BuildAsync(scratch);
            try
            {
                await InstallAsync(app);
                await app.StartAsync();
                using var client = app.GetTestClient();

                var pageResponse = await client.GetAsync("/");
                var page = await pageResponse.Content.ReadAsStringAsync();
                _output.WriteLine($"GET / -> {(int)pageResponse.StatusCode}, {page.Length} chars, installer={page.Contains(InstallerMarker)}");

                // An empty configuration: what a stranger's first wizard call would carry before choosing anything.
                // With the antiforgery pair the installer page sends, so the call is judged by the controller, not refused before it.
                var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app);
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/installation")
                {
                    Content = JsonContent.Create(new InstallConfig { Aliases = "", ConnectionString = "" })
                };
                request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
                request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                _output.WriteLine($"POST /api/installation -> {(int)response.StatusCode} {GateDrive.Truncate(body)}");
                response.EnsureSuccessStatusCode();
                var installation = JsonSerializer.Deserialize<Installation>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return (page, installation.Message);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<WebApplication> BuildAsync(Scratch scratch) =>
            Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

        private static async Task<Installation> InstallAsync(WebApplication app)
        {
            var installTask = Task.Run(() => Program.InstallAsync(app));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"The installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
            }
            return await installTask;
        }

        /// <summary>A content root whose override file is the whole configuration, bound through the process-wide variable.</summary>
        private sealed class Scratch : IDisposable
        {
            private readonly string? _previousOverridePath;

            public Scratch()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-unattended-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));
                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public string WriteSecret(string name, string content)
            {
                var path = Path.Combine(ContentRoot, "secrets", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, content);
                return path;
            }

            public void WriteOverride(bool withConnectionString, JsonObject? installation)
            {
                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = withConnectionString ? $"Data Source={Path.Combine(ContentRoot, "unattended.db")};" : ""
                    }
                };
                if (installation != null)
                {
                    overrides["Installation"] = installation;
                }
                System.IO.File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);
                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort - SQLite may still hold the file
                }
            }
        }
    }
}
