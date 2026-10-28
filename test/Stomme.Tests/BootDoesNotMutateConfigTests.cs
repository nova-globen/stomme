using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Server;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Booting the app must never write to appsettings.json. Configuration is only persisted when an install
    /// is actually performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DEFECT THIS PINS (D32). <c>Program.Main</c> -> <c>Program.Install</c> -> <c>DatabaseManager.Install(null)</c>
    /// calls <c>ValidateConfiguration()</c> as its FIRST statement — 88 lines before the install-intent guard
    /// that checks there is a connection string to install against. The shipped appsettings.json has
    /// <c>DefaultDBType: ""</c>, so that unguarded branch persisted the default on EVERY boot of a fresh
    /// checkout, whether or not anything was installed. Observed: it rewrote a developer's working tree and
    /// stripped the file's trailing newline (ConfigManager's JsonSerializer.Serialize emits none), turning a
    /// plain `dotnet run` into a dirty `git status`.
    /// </para>
    /// <para>
    /// WHY THIS IS THE DISCRIMINATING CHECK. The bug is not in rendering and not in the installer UI — it is
    /// in BOOT, on the no-install-intent path, which is precisely the path that does nothing observable.
    /// Nothing else in the suite covers it: InstalledSiteRenderTests always performs a REAL install, and a
    /// real install is *supposed* to write. Only a boot with no install intent can see this, so only this
    /// test can.
    /// </para>
    /// <para>
    /// HOW THE WRITE IS OBSERVED. Every ConfigManager write is routed to the file named by
    /// STOMME_APPSETTINGS_PATH, so pointing it at scratch both protects the repository's appsettings.json
    /// and gives the assertion a single file to watch: if boot writes ANY setting, these bytes change. The
    /// scratch copy is the real shipped appsettings.json — uninstalled, exactly as checked out — because
    /// testing a hand-rolled config would test a fiction. It shares
    /// <see cref="InstalledSiteRenderTests"/>'s non-parallel collection deliberately: ConfigManager resolves
    /// that environment variable into STATIC fields, so two hosts composing at once would race over it.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BootDoesNotMutateConfigTests : IDisposable
    {
        private readonly string _contentRoot;

        // Null when the variable was not set, which is the normal case — Dispose restores exactly that.
        private readonly string? _previousOverridePath;

        public BootDoesNotMutateConfigTests()
        {
            _contentRoot = Path.Combine(Path.GetTempPath(), "stomme-boot-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));

            var shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

            // The base layer Program.BuildAppAsync requires (AddJsonFile with optional:false).
            File.Copy(shipped, Path.Combine(_contentRoot, "appsettings.json"));

            // The write target. Program.BuildAppAsync reads it LAST, and ConfigManager writes it — so it is both
            // the effective configuration and the file under observation.
            File.Copy(shipped, OverrideFile);

            _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _contentRoot);
        }

        private string OverrideFile => Path.Combine(_contentRoot, "appsettings.override.json");

        [Fact]
        public async Task Booting_an_uninstalled_host_does_not_write_configuration()
        {
            var before = File.ReadAllBytes(OverrideFile);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                // Exactly what Program.Main does, in the same place: after the host is built, before it
                // serves. With no connection string there is nothing to install — Main serves the wizard.
                var install = await Program.InstallAsync(app);

                Assert.False(install.Success);
                // An empty Message is what Main treats as "not installed yet, serve the installer" rather
                // than "installation failed". Asserting it keeps this a clean no-install-intent boot and not
                // an install that merely errored before it got as far as writing.
                Assert.True(string.IsNullOrEmpty(install.Message),
                    $"Expected a clean uninstalled boot, but the installer reported: {install.Message}");
            }
            finally
            {
                (app as IDisposable)?.Dispose();
            }

            // Byte-for-byte: this catches the stripped trailing newline as surely as the persisted value.
            Assert.Equal(before, File.ReadAllBytes(OverrideFile));
        }

        /// <summary>
        /// BL-20 (a): a config whose <c>AvailableDatabases</c> carries the pre-6.1.5 <c>, Stomme.Database.</c>
        /// namespace must NOT be rewritten by boot — the catalog is resolved on READ (the D32 pattern
        /// <c>GetDatabaseType</c> already uses), so the wizard still sees the migrated entries.
        /// </summary>
        [Fact]
        public async Task Booting_with_a_legacy_AvailableDatabases_config_does_not_rewrite_it_and_still_serves_the_catalog()
        {
            // age one entry back to the pre-6.1.5 assembly namespace — the shape ValidateConfiguration rewrote
            var overrideJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(OverrideFile))!.AsObject();
            var legacyEntry = overrideJson["AvailableDatabases"]!.AsArray()[0]!.AsObject();
            legacyEntry["DBType"] = legacyEntry["DBType"]!.GetValue<string>()
                .Replace(", Stomme.Server", ", Stomme.Database.SqlServer");
            File.WriteAllText(OverrideFile, overrideJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var before = File.ReadAllBytes(OverrideFile);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var install = await Program.InstallAsync(app);
                Assert.False(install.Success);
                Assert.True(string.IsNullOrEmpty(install.Message),
                    $"Expected a clean uninstalled boot, but the installer reported: {install.Message}");

                await app.StartAsync();
                var databases = await GetCatalogAsync(app);

                // read-time resolution: every entry serves the migrated assembly, RavenDB included (D56)
                Assert.All(databases, d => Assert.EndsWith(", Stomme.Server", d.DBType));
                Assert.Contains(databases, d => d.Name == "RavenDB");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }

            Assert.Equal(before, File.ReadAllBytes(OverrideFile));
        }

        /// <summary>
        /// BL-20 (a), missing-section case: a config with NO <c>AvailableDatabases</c> at all boots without a
        /// write, and the catalog read serves the stock six defaults.
        /// </summary>
        [Fact]
        public async Task Booting_with_no_AvailableDatabases_section_serves_defaults_on_read_without_writing()
        {
            // the section must vanish from BOTH layers, or the base file still supplies it to the merged config
            foreach (var file in new[] { Path.Combine(_contentRoot, "appsettings.json"), OverrideFile })
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                json.Remove("AvailableDatabases");
                File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            var before = File.ReadAllBytes(OverrideFile);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var install = await Program.InstallAsync(app);
                Assert.False(install.Success);

                await app.StartAsync();
                var databases = await GetCatalogAsync(app);

                Assert.Equal(6, databases.Count);
                Assert.Contains(databases, d => d.Name == "RavenDB");
                Assert.All(databases, d => Assert.EndsWith(", Stomme.Server", d.DBType));
            }
            finally
            {
                await SafeShutdownAsync(app);
            }

            Assert.Equal(before, File.ReadAllBytes(OverrideFile));
        }

        /// <summary>
        /// BL-20 (b): <c>GetInstallationId()</c> is a PURE read — no id minted, no version stamped, no bytes
        /// written. <c>EnsureInstallationId()</c> is the explicit write, idempotent, and is what the
        /// install/upgrade path calls (<c>DatabaseManager.UpdateInstallation</c>).
        /// </summary>
        [Fact]
        public async Task GetInstallationId_is_a_pure_read_and_EnsureInstallationId_is_the_explicit_write()
        {
            var before = File.ReadAllBytes(OverrideFile);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", _contentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var config = app.Services.GetRequiredService<Stomme.Infrastructure.IConfigManager>();

                Assert.Equal(string.Empty, config.GetInstallationId());
                Assert.Equal(before, File.ReadAllBytes(OverrideFile));

                var ensured = config.EnsureInstallationId();
                Assert.False(string.IsNullOrEmpty(ensured), "EnsureInstallationId must mint and persist an id");
                Assert.Equal(ensured, config.GetInstallationId());
                Assert.Equal(ensured, config.EnsureInstallationId());
                Assert.NotEqual(before, File.ReadAllBytes(OverrideFile));
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private static async Task<List<Stomme.Models.Database>> GetCatalogAsync(Microsoft.AspNetCore.Builder.WebApplication app)
        {
            var server = app.GetTestServer();
            using var client = new System.Net.Http.HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            var response = await client.GetAsync("/api/Database");
            response.EnsureSuccessStatusCode();
            var databases = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<List<Stomme.Models.Database>>(client, "/api/Database");
            Assert.NotNull(databases);
            return databases!;
        }

        private static async Task SafeShutdownAsync(Microsoft.AspNetCore.Builder.WebApplication app)
        {
            try { await app.StopAsync(new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

            // Release this install's SQLite pools first, or the delete below fails on a file the host still
            // holds - which is what the catch was quietly absorbing (BL-104).
            ScratchSqlite.ReleaseInstall(_contentRoot);

            try
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
            catch (IOException)
            {
                // A file the host still holds open; the OS reclaims the temp folder.
            }
        }
    }
}
