#nullable enable
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-203 - a deployment that lost its content folder gets the SEEDED logo back on the next boot, and nothing else is touched.
    /// </summary>
    /// <remarks>
    /// UAT's and production's containers lost every file when they were recreated after infra BL-134's mounts, while the
    /// database still listed them: the logo answered 404 on both sites (measured on the hosting server, 2026-10-02). Driven through the
    /// boot path on an installed SQLite host: the seeded file deleted comes back; a file present is never overwritten; a logo
    /// the operator uploaded under another name, whose file is gone, is not invented.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SeededLogoRestoreTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_boot_path_restores_a_missing_seeded_logo_and_leaves_a_present_file_and_an_upload_alone()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);

            // The shipped brand asset, where a deployed host's web root has it (a scratch web root starts empty).
            var images = Directory.CreateDirectory(Path.Combine(scratch.ContentRoot, "wwwroot", "images")).FullName;
            File.Copy(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "images", "logo-white.png"), Path.Combine(images, "logo-white.png"));

            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                var (logoId, path) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var site = await sp.GetRequiredService<ISiteRepository>().GetSiteAsync(alias.SiteId);
                    var file = await sp.GetRequiredService<IFileRepository>().GetFileAsync(site.LogoFileId!.Value);
                    return (file.FileId, await sp.GetRequiredService<IFileRepository>().GetFilePathAsync(file));
                });
                Assert.Equal(SeededLogoReconciler.SeededName, Path.GetFileName(path));
                var shipped = await File.ReadAllBytesAsync(path);
                output.WriteLine($"[logo] file {logoId} at {path}, {shipped.Length} bytes");

                // ---- the file is gone, its row is not: the next boot puts the shipped asset back ----
                File.Delete(path);
                Assert.True((await Program.InstallAsync(app)).Success);
                Assert.True(File.Exists(path), "the seeded logo was not restored");
                Assert.Equal(shipped, await File.ReadAllBytesAsync(path));

                // ---- a file present is the operator's to change: never overwritten ----
                await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
                Assert.True((await Program.InstallAsync(app)).Success);
                Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));

                // ---- a logo uploaded under another name, its file gone: not invented ----
                var uploaded = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var files = sp.GetRequiredService<IFileRepository>();
                    var seeded = await files.GetFileAsync(logoId);
                    var upload = await files.AddFileAsync(new Stomme.Models.File { FolderId = seeded.FolderId, Name = "brand.png", Extension = "png", Size = 10 });
                    var sites = sp.GetRequiredService<ISiteRepository>();
                    var site = await sites.GetSiteAsync(alias.SiteId);
                    site.LogoFileId = upload.FileId;
                    await sites.UpdateSiteAsync(site);
                    return await files.GetFilePathAsync(upload);
                });
                Assert.True((await Program.InstallAsync(app)).Success);
                Assert.False(File.Exists(uploaded), "an upload's file was invented");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test output.");
        }
    }
}
