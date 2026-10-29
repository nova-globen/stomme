using System;
using System.IO;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// An Interactive install's prerender is OVERRIDABLE. It used to be hardcoded
    /// <c>Prerender = (rendermode == Interactive)</c>, which makes an Interactive/WebAssembly site dead behind a
    /// reverse proxy whose container cannot resolve its own public host (the SSR prerender pass HTTP-calls the
    /// app's public URL → every page an empty 200). An explicit <c>InstallConfig.Prerender</c> (or appsettings
    /// <c>Prerender</c>) now wins; absent, the historical default stands. Provider-agnostic (a Site column), so
    /// SQLite is genuine cover.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class InstallPrerenderTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);
        private readonly ITestOutputHelper _output;

        public InstallPrerenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("false", 0)] // explicit override: Interactive install, prerender OFF
        [InlineData("true", 1)]  // explicit override: prerender ON
        [InlineData("", 1)]      // default preserved: Interactive => prerender ON
        public async Task Interactive_install_honours_the_prerender_override(string prerender, int expected)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "stomme-prerender-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(scratch, "wwwroot"));
            System.IO.File.Copy(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                Path.Combine(scratch, "appsettings.json"));
            System.IO.File.WriteAllText(Path.Combine(scratch, "appsettings.override.json"), "{}");
            var dbPath = Path.Combine(scratch, "tenant.db");

            // Route the app's appsettings read/write to the scratch dir (the install persists the connection
            // string via this override; without it the master-db provisioning cannot open the file).
            var previousOverride = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, scratch);

            // Defensive snapshot, kept although its root cause is fixed: ConfigManager's write target used to
            // be a STATIC field, so a prior in-process host that fell back to the shipped
            // BaseDirectory/appsettings.json could make this install's connection-string write land in that
            // SHARED file — polluting it for later tests (e.g. BootDoesNotMutateConfigTests, which copies the
            // shipped file). The target is per-instance now (ConfigManagerWriteTargetTests pins it); the
            // snapshot stays as belt-and-braces against any future writer of the shipped file.
            var shippedAppSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var shippedBytes = System.IO.File.Exists(shippedAppSettings) ? System.IO.File.ReadAllBytes(shippedAppSettings) : null;

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
                var installTask = Task.Run(() => databaseManager.InstallAsync(new InstallConfig
                {
                    DatabaseType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ConnectionString = $"Data Source={dbPath};",
                    Aliases = "localhost",
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = "host",
                    HostPassword = "P@ssw0rd!Prerender$2026",
                    HostEmail = "host@prerender.test",
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Interactive,
                    Runtime = "WebAssembly",
                    Prerender = prerender
                }));
                Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "Install timed out.");
                var install = await installTask;
                Assert.True(install.Success, $"Install failed: {install.Message}");

                // Let go of the pooled writers for the database the install just wrote, so the read-only open
                // below sees the file rather than a handle. This database only - ClearAllPools would reach into
                // whatever sibling class is mid-request.
                ScratchSqlite.Release($"Data Source={dbPath};");
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Prerender FROM Site WHERE SiteId = 1";
                var actual = Convert.ToInt32(command.ExecuteScalar());
                _output.WriteLine($"[prerender='{prerender}'] Site.Prerender = {actual} (expected {expected})");
                Assert.Equal(expected, actual);
            }
            finally
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await app.StopAsync(cts.Token); } catch { }
                await app.DisposeAsync();
                // Everything this run opened under the scratch root, by exact connection string: the installed
                // store named in the post-install settings files, the FeatureManagement sidecar, the write
                // connection string, and the read-only one opened above (a DIFFERENT pool - the key is the raw
                // string, measured in ScratchSqliteReleaseTests). Never the process-wide form.
                ScratchSqlite.ReleaseInstall(scratch);
                ScratchSqlite.Release($"Data Source={dbPath};");
                ScratchSqlite.Release($"Data Source={dbPath};Mode=ReadOnly");
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, previousOverride);
                // Restore the shipped appsettings if the static-field fallback caused a write to it.
                if (shippedBytes != null)
                {
                    try { System.IO.File.WriteAllBytes(shippedAppSettings, shippedBytes); } catch { }
                }
                try { Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
