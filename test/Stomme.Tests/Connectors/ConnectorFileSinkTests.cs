#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Repository;
using Stomme.Tests.Datasets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-96 increment 2 - the managed file sink on an installed SQLite host: a connector's file lands in a site folder,
    /// through the ingestion guard, as a File row (and updates the row when the name exists); an extension outside the
    /// site's uploadable set, a missing folder and a file over the cap are refused with the guard's reason and nothing
    /// is written; the extraction service hands the sink to the connector.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ConnectorFileSinkTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task A_file_lands_in_the_site_folder_as_a_row_and_refusals_write_nothing()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), UploadDrive.Host, output, colocateModuleAggregates: false,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Files"] = new System.Text.Json.Nodes.JsonObject { ["MaxUploadBytes"] = 4096 } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var folder = await UploadDrive.NewFolderAsync(app);
                var folderPath = folder.Path.Trim('/');

                var (firstId, secondId, size) = await GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
                {
                    var sink = sp.GetRequiredService<IConnectorFileSink>();
                    Assert.Equal(4096, sink.MaxBytes);

                    var first = await sink.StoreAsync("report.csv", folderPath, new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n")), 8);
                    var second = await sink.StoreAsync("report.csv", folderPath, new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n3,4\n")), 12); // the same name updates the row
                    var row = await sp.GetRequiredService<IFileRepository>().GetFileAsync(second);
                    return (first, second, row.Size);
                });
                Assert.Equal(firstId, secondId);
                Assert.Equal(12, size);

                await GateDrive.InTenantAsync(app, UploadDrive.Host, async (sp, _) =>
                {
                    var sink = sp.GetRequiredService<IConnectorFileSink>();
                    var badExtension = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.StoreAsync("tool.exe", folderPath, new MemoryStream(new byte[4]), 4));
                    Assert.Contains("Extension", badExtension.Message);
                    var missingFolder = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.StoreAsync("x.csv", "no-such-folder", new MemoryStream(new byte[4]), 4));
                    Assert.Contains("does not exist", missingFolder.Message);
                    var overCap = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.StoreAsync("big.csv", folderPath, new MemoryStream(new byte[8192]), 8192));
                    Assert.Contains("Size", overCap.Message);
                    var rootRefused = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.StoreAsync("x.csv", "", new MemoryStream(new byte[1]), 1));
                    Assert.Contains("target folder is required", rootRefused.Message);
                    var names = (await sp.GetRequiredService<IFileRepository>().GetFilesAsync(folder.FolderId)).Select(f => f.Name).ToList();
                    Assert.Equal(new[] { "report.csv" }, names);
                    return 0;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
