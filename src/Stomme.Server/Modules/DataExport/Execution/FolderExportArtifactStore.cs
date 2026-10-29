#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.DataExport.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using File = Stomme.Models.File;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// The site's private <c>Exports/</c> folder: where it is, how it is made, and whether a file is still in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Modelled on the framework's own <c>Users/</c> folder</b> - <see cref="FolderTypes.Private"/>, so it
    /// resolves under ContentRoot and is never served as a static file, with Browse, View and Edit granted to
    /// Administrators ALONE. The framework's download endpoints authorise by folder View and never by requester, so that row is the whole of what keeps one user's export from another through
    /// <c>/api/File/download/{id}</c>; the module's own download then narrows Administrators down to the requester
    /// or an <c>export:manage</c> holder.
    /// </para>
    /// <para>
    /// <b>The folder's rows are not enough on their own.</b> Edit on a folder lets an editor MOVE a
    /// file into a public one. So nothing in this module trusts a File id by itself: <see cref="ContainsAsync"/>
    /// re-asserts that the file still sits in THIS site's Exports folder, and both the download and the purge ask
    /// it first.
    /// </para>
    /// </remarks>
    public sealed class ExportsFolder
    {
        public const string FolderName = "Exports";

        public const string FolderPath = "Exports/";

        private readonly IFolderRepository _folders;
        private readonly DataExportOptions _options;

        public ExportsFolder(IFolderRepository folders, DataExportOptions options)
        {
            _folders = folders ?? throw new ArgumentNullException(nameof(folders));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The folder, or null when this site has none yet.</summary>
        public Task<Folder> FindAsync(int siteId) => _folders.GetFolderAsync(siteId, FolderPath);

        /// <summary>Creates the folder if it is not there. Idempotent; never touches one that exists.</summary>
        /// <returns>The folder, and whether this call created it.</returns>
        public async Task<(Folder Folder, bool Created)> EnsureAsync(int siteId)
        {
            var existing = await FindAsync(siteId);
            if (existing is not null)
            {
                // Left exactly as found: an operator may have tightened it, and re-applying the defaults would
                // silently undo that (the AdminSiteTemplate reconciliation lesson).
                return (existing, false);
            }

            var root = await _folders.GetFolderAsync(siteId, string.Empty)
                ?? throw new InvalidOperationException($"Site {siteId} has no root folder; its Exports folder cannot be created.");

            var created = await _folders.AddFolderAsync(new Folder
            {
                SiteId = siteId,
                ParentId = root.FolderId,
                Name = FolderName,
                Type = FolderTypes.Private,
                Path = FolderPath,
                Order = 5,
                ImageSizes = string.Empty,
                Capacity = Math.Max(0, _options.FolderCapacityMb),
                IsSystem = true,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true),
                },
            });

            return (created, true);
        }

        /// <summary>Whether <paramref name="file"/> is, right now, in this site's Exports folder.</summary>
        public async Task<bool> ContainsAsync(int siteId, File? file)
        {
            if (file is null)
            {
                return false;
            }

            var folder = await FindAsync(siteId);
            return folder is not null && file.FolderId == folder.FolderId;
        }
    }

    /// <summary>
    /// Artifacts as framework File rows in the site's private <c>Exports/</c> folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No second blob port: the bytes live where every other site file lives and are described by an ordinary
    /// <see cref="File"/> row, so the Files admin, the folder capacity and site deletion all see them.
    /// </para>
    /// <para>
    /// <b>Each ATTEMPT writes its own temp file</b> (<c>{job}.{attempt}.tmp</c>). A render whose
    /// lease was taken over may still be writing when the new attempt starts; sharing one path would have the two
    /// interleave bytes into the artifact. The File row is added only at commit, so a temp file is never visible
    /// as a site file, and a crash leaves at worst an orphan the purge can recognise by its extension.
    /// </para>
    /// <para>
    /// <b>Tenancy is the caller's job, and it matters here more than anywhere.</b> The physical path is built from
    /// the RESOLVED TENANT (<c>FolderRepository.GetFolderPathAsync</c>), so this store must run inside the scope
    /// the hosted job prepared - <c>BeginScope</c> + a fresh DI scope + <c>SetAliasAsync</c>. A
    /// mis-set scope would write one tenant's export under another tenant's directory.
    /// </para>
    /// </remarks>
    public sealed class FolderExportArtifactStore : IExportArtifactStore
    {
        internal const string TempExtension = ".tmp";

        private readonly ExportsFolder _exports;
        private readonly IFolderRepository _folders;
        private readonly IFileRepository _files;

        public FolderExportArtifactStore(ExportsFolder exports, IFolderRepository folders, IFileRepository files)
        {
            _exports = exports ?? throw new ArgumentNullException(nameof(exports));
            _folders = folders ?? throw new ArgumentNullException(nameof(folders));
            _files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public async Task<Stream> OpenAttemptAsync(ExportJobAggregate job, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            var (folder, _) = await _exports.EnsureAsync(job.SiteId);
            var directory = await _folders.GetFolderPathAsync(folder);
            Directory.CreateDirectory(directory);

            // CreateNew, not Create: an attempt's temp file existing already means two writers share an attempt
            // number, which must be loud rather than an overwrite.
            return new FileStream(
                TempPath(directory, job), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public async Task<int> CommitAttemptAsync(ExportJobAggregate job, long sizeBytes, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (sizeBytes < 0 || sizeBytes > int.MaxValue)
            {
                // File.Size is an int. The cap already clamps to it; this is the explicit refusal behind the cap.
                throw new ExportArtifactTooLargeException(int.MaxValue);
            }

            var folder = await _exports.FindAsync(job.SiteId)
                ?? throw new InvalidOperationException($"The Exports folder of site {job.SiteId} disappeared while export {job.Id} was rendering.");
            var directory = await _folders.GetFolderPathAsync(folder);

            var name = job.Id.ToString("N") + job.FileExtension();
            System.IO.File.Move(TempPath(directory, job), Path.Combine(directory, name), overwrite: true);

            var row = await _files.AddFileAsync(new File
            {
                FolderId = folder.FolderId,
                Name = name,
                Extension = job.FileExtension().TrimStart('.'),
                Size = (int)sizeBytes,
                ImageHeight = 0,
                ImageWidth = 0,
                Description = "Data export",
            });

            return row.FileId;
        }

        public async Task DiscardAttemptAsync(ExportJobAggregate job, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            var folder = await _exports.FindAsync(job.SiteId);
            if (folder is null)
            {
                return;
            }

            var path = TempPath(await _folders.GetFolderPathAsync(folder), job);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        private static string TempPath(string directory, ExportJobAggregate job) =>
            Path.Combine(directory, job.Id.ToString("N") + "." + job.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) + TempExtension);
    }

    /// <summary>
    /// Creates each site's <c>Exports/</c> folder on the boot/upgrade path.
    /// </summary>
    /// <remarks>
    /// Run by the framework's <c>SiteContentReconciliationRunner</c>, which enters a tenant scope per tenant and
    /// contains failures per site - never from a hosted service's <c>StartAsync</c>, which has whichever tenant the
    /// ambient scope happened to hold. Additive: a site that already has the folder is left alone. While
    /// <c>DataExport:Enabled</c> is off it does nothing, so a stock install gains no folder.
    /// </remarks>
    public sealed class DataExportSiteReconciler : ISiteContentReconciler
    {
        private readonly DataExportOptions _options;

        public DataExportSiteReconciler(DataExportOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string Name => "Data export folder";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!_options.Enabled)
            {
                return;
            }

            await context.Services.GetRequiredService<ExportsFolder>().EnsureAsync(context.Site.SiteId);
        }
    }
}
