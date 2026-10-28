#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-172 - a data snapshot before a framework upgrade. The upgrade asks every <see cref="IPreUpgradeSnapshot"/> BEFORE it
    /// touches anything and stops on a refusal; the backup module's answers: off unless <c>Backup:Enabled</c>, a
    /// <c>pre-upgrade</c> snapshot under the scheduled passphrase when on, and a refusal when that passphrase is missing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BackupPreUpgradeSnapshotTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Passphrase = "pre-upgrade passphrase";

        [Fact]
        public async Task A_refused_snapshot_stops_the_upgrade_before_the_package_is_touched()
        {
            using var root = new TempRoot();
            var package = root.WriteFrameworkPackage();

            Assert.Equal("planted refusal", await Manager(root, new Answer("planted refusal")).UpgradeFramework(backup: true));
            Assert.True(File.Exists(package), "a refused upgrade consumed the framework package");
        }

        // The instrument, checked the other way: with the snapshot's consent the same upgrade DOES go on - it installs (and so
        // deletes) the package, then fails to download the upgrade from an address nothing listens on. Without this, a gate
        // that was never reached would pass the test above.
        [Fact]
        public async Task A_consenting_snapshot_lets_the_same_upgrade_go_on()
        {
            using var root = new TempRoot();
            var package = root.WriteFrameworkPackage();

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Manager(root, new Answer(null)).UpgradeFramework(backup: true));

            Assert.False(File.Exists(package));
        }

        [Fact]
        public async Task With_backup_off_the_snapshot_consents_without_touching_anything()
        {
            // No services at all: an answer that reached for the engine would throw.
            var snapshot = new BackupPreUpgradeSnapshot(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new BackupOptions(), NullLogger<BackupPreUpgradeSnapshot>.Instance);

            Assert.Null(await snapshot.TakeAsync(CancellationToken.None));
        }

        [Fact]
        public async Task With_backup_on_a_pre_upgrade_snapshot_is_taken_under_the_scheduled_passphrase()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true, ["Schedule"] = new JsonObject { ["Passphrase"] = Passphrase } } });
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            try
            {
                Assert.Null(await PreUpgrade(app).TakeAsync(CancellationToken.None));

                var row = Assert.Single(await Engine(app).ListSnapshotsAsync());
                Assert.Equal((RetentionTag.PreUpgrade, BackupSnapshotStatus.Available), (row.RetentionTag, row.Status));
                Assert.True((await Engine(app).PreflightAsync(row.Id, Passphrase)).Ok, "the pre-upgrade snapshot does not open under the scheduled passphrase");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_backup_on_and_no_scheduled_passphrase_the_upgrade_is_refused()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                var refusal = await PreUpgrade(app).TakeAsync(CancellationToken.None);

                Assert.Contains("Schedule:Passphrase", refusal, StringComparison.Ordinal);
                Assert.Empty(await Engine(app).ListSnapshotsAsync());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static IPreUpgradeSnapshot PreUpgrade(WebApplication app) =>
            Assert.Single(app.Services.GetServices<IPreUpgradeSnapshot>().OfType<BackupPreUpgradeSnapshot>());

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static InstallationManager Manager(TempRoot root, IPreUpgradeSnapshot snapshot) =>
            new(new Lifetime(), new Environment(root.Path), NullLogger<InstallationManager>.Instance, new[] { snapshot });

        private sealed class Answer(string? refusal) : IPreUpgradeSnapshot
        {
            public Task<string?> TakeAsync(CancellationToken cancellationToken) => Task.FromResult(refusal);
        }

        private sealed class TempRoot : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-preupgrade-" + Guid.NewGuid().ToString("N"));

            public string WriteFrameworkPackage()
            {
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "wwwroot"));
                return BackupPreUpgradeSnapshotTests.WriteFrameworkPackage(Path);
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }

        /// <summary>
        /// A framework package the version check accepts, holding nothing but its nuspec (nothing to extract), whose upgrade
        /// download points at an address nothing listens on. Also used by <c>UpgradeEndpointTests</c> (BL-174).
        /// </summary>
        internal static string WriteFrameworkPackage(string contentRoot)
        {
            var folder = System.IO.Path.Combine(contentRoot, Constants.PackagesFolder);
            Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, $"{Constants.PackageId}.{Constants.Version}.nupkg");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry($"{Constants.PackageId}.nuspec").Open()))
            {
                writer.Write($"<package><metadata><id>{Constants.PackageId}</id><version>{Constants.Version}</version>" +
                    "<projectUrl>http://127.0.0.1:9/Stomme.Upgrade.zip</projectUrl></metadata></package>");
            }

            return file;
        }

        private sealed class Environment(string root) : IWebHostEnvironment
        {
            public string ApplicationName { get; set; } = "Stomme.Tests";

            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

            public string ContentRootPath { get; set; } = root;

            public string EnvironmentName { get; set; } = "Development";

            public string WebRootPath { get; set; } = System.IO.Path.Combine(root, "wwwroot");

            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        }

        private sealed class Lifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;

            public CancellationToken ApplicationStopping => CancellationToken.None;

            public CancellationToken ApplicationStopped => CancellationToken.None;

            public void StopApplication()
            {
            }
        }
    }
}
