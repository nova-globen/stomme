#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Repository;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Puts a site's SEEDED logo file back when its row is there and its file is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect, measured on the hosting server (2026-10-02).</b> UAT's and production's containers wrote their files into their own
    /// writable layer until infra BL-134 mounted the content folders on 2026-09-29; the next deploy recreated each container,
    /// so every file the sites held was gone while the database still listed it - the logo answered 404 on both sites. A
    /// deployment that loses its files keeps losing the logo the same way, and the logo is the one file the framework itself
    /// put there: <c>DefaultSiteTemplate</c> copies <c>images/logo-white.png</c> into the site's root folder at install.
    /// </para>
    /// <para>
    /// <b>Narrow by construction.</b> It acts only on the file the site's logo names, only when that file is the seeded one
    /// (<c>logo-white.png</c> in the site's root folder), and only when the file is missing - it copies the shipped asset
    /// back and changes no row. An upload an operator chose as the logo under any other name cannot be restored and is left
    /// a 404 (the owner re-uploads it); a present file, whatever it holds, is never overwritten. Runs on every boot through
    /// the shared runner, per site of every tenant; a second boot finds the file and does nothing.
    /// </para>
    /// </remarks>
    public sealed class SeededLogoReconciler : ISiteContentReconciler
    {
        /// <summary>The seeded logo's name in the site's root folder, and the web root asset it is copied from.</summary>
        public const string SeededName = "logo-white.png";

        public const string AssetPath = "images/logo-white.png";

        public string Name => "Seeded logo restore";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Site.LogoFileId is not int fileId)
            {
                return;
            }

            var files = context.Services.GetRequiredService<IFileRepository>();
            var file = await files.GetFileAsync(fileId);
            if (file is null
                || !string.Equals(file.Name, SeededName, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(file.Folder?.Path))
            {
                return; // not the seeded file: an operator's upload is theirs
            }

            var path = await files.GetFilePathAsync(file);
            if (string.IsNullOrEmpty(path) || System.IO.File.Exists(path))
            {
                return;
            }

            var asset = context.Services.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider?.GetFileInfo(AssetPath);
            if (asset is not { Exists: true, IsDirectory: false })
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = asset.CreateReadStream())
            await using (var target = System.IO.File.Create(path))
            {
                await source.CopyToAsync(target, ct);
            }

            context.Services.GetRequiredService<ILogger<SeededLogoReconciler>>().LogWarning(
                "Restored the seeded logo of site {SiteId}: its file row was there and its file was not - a deployment that " +
                "lost its content folder.", context.Site.SiteId);
        }
    }
}
