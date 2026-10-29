#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Backup.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Stomme.Persistence.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// Archives are <c>.stbak</c> since 10.75.0 (<c>.dcbak</c> carried the old product name), and an archive taken
    /// before keeps working - each snapshot row keeps the name it was stored under. Pinned on an installed host: a real
    /// snapshot is renamed on disk and in its row to the old extension, as a 10.74.x install left it, and is then listed,
    /// opened for download, staged for a restore (its pre-flight reads the whole archive) and deleted with its sidecar.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BackupLegacyArchiveNameTests(ITestOutputHelper output)
    {
        private const string Passphrase = "the legacy archive passphrase";

        [Fact]
        public async Task A_new_archive_is_stbak_and_one_stored_as_dcbak_is_still_listed_downloaded_restored_and_deleted()
        {
            var settings = new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            try
            {
                var folder = Path.Combine(app.Services.GetRequiredService<IConfigManager>().GetDataDirectory(), LocalFileBackupArchiveStore.FolderName);
                var restored = await LegacySnapshotAsync(app, folder);
                var deleted = await LegacySnapshotAsync(app, folder);
                output.WriteLine("files: " + string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));

                foreach (var id in new[] { restored, deleted })
                {
                    var listed = Assert.Single(await Engine(app).ListSnapshotsAsync(), row => row.Id == id);
                    Assert.Equal($"{id:N}{StorageLocation.LegacyArchiveExtension}", listed.StorageLocation.Value);
                    Assert.Equal(BackupSnapshotStatus.Available, listed.Status);
                }

                await using (var archive = await Engine(app).OpenArchiveAsync(restored))
                {
                    Assert.NotNull(archive);
                    Assert.True(archive!.ReadByte() >= 0, "the .dcbak archive opened empty");
                }

                var report = await Engine(app).StageRestoreAsync(restored, Passphrase, null);
                output.WriteLine("pre-flight: " + string.Join(" | ", report.Problems));
                Assert.True(report.Ok, "the .dcbak archive did not pass its restore pre-flight: " + string.Join(" | ", report.Problems));
                File.Delete(Engine(app).RequestFile); // the staged restore is not the test's subject; leave nothing to apply

                await Engine(app).DeleteSnapshotAsync(deleted);
                Assert.Empty(Directory.GetFiles(folder, $"{deleted:N}*"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // A real snapshot, then renamed as a 10.74.x install left it: the archive, its checksum sidecar and the row all .dcbak.
        private static async Task<Guid> LegacySnapshotAsync(WebApplication app, string folder)
        {
            var id = await Engine(app).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
            var currentName = $"{id:N}{StorageLocation.ArchiveExtension}";
            Assert.True(File.Exists(Path.Combine(folder, currentName)), "a new archive is not written as .stbak");

            var legacyName = $"{id:N}{StorageLocation.LegacyArchiveExtension}";
            foreach (var file in Directory.GetFiles(folder, currentName + "*"))
            {
                File.Move(file, Path.Combine(folder, legacyName + Path.GetFileName(file)[currentName.Length..]));
            }

            await CatalogueAsync(app, async repository =>
            {
                var row = await repository.GetByIdAsync(id) ?? throw new InvalidOperationException("no row");
                typeof(BackupSnapshotAggregate).GetProperty(nameof(BackupSnapshotAggregate.StorageLocation))!.SetValue(row, StorageLocation.Create(legacyName));
                await repository.UpdateAsync(row);
            });
            return id;
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static async Task CatalogueAsync(WebApplication app, Func<IBackupSnapshotRepository, Task> action)
        {
            using var scope = app.Services.CreateScope();
            using var system = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().BeginScope(null);
            await action(scope.ServiceProvider.GetRequiredService<IBackupSnapshotRepository>());
        }
    }
}
