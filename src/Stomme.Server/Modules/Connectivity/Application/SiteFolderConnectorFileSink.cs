#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>
    /// The managed file sink over the framework's file manager (BL-96 increment 2): the folder is the request's site's,
    /// by path; the bytes go through <see cref="IFileIngestionGuard.AcceptChunkAsync"/> as a one-part upload - the
    /// extension allowlist, the size cap, the scanner and the folder capacity all apply, on a staging copy, exactly as
    /// the Files admin's own upload - and the accepted file is registered as a File row (updated when the name exists).
    /// A refusal is an <see cref="InvalidOperationException"/> carrying the guard's reason: the connector's caller gets
    /// a category, never a host path.
    /// </summary>
    /// <remarks>
    /// <b>One row write per fresh scope.</b> A connector stores several files in ONE request scope (an FTP download of a
    /// folder), and the framework's file repository tracks what it writes (D19): a second store of the same name in the
    /// same scope attached a second instance of the row and EF refused it - measured by the sink's drive. Each row write
    /// therefore runs in a scope of its own, routed to the same alias, which is the per-write-store pattern in scope form.
    /// </remarks>
    public sealed class SiteFolderConnectorFileSink(IFolderRepository folders, IFileIngestionGuard guard, SiteState siteState, IServiceScopeFactory scopes) : IConnectorFileSink
    {
        public long MaxBytes => guard.Options.MaxUploadBytes;

        public async Task<int> StoreAsync(string fileName, string folderPath, Stream content, long length, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            ArgumentNullException.ThrowIfNull(content);
            var alias = siteState.Alias ?? throw new InvalidOperationException("Managed file storage belongs to a site; none is resolved.");

            var path = (folderPath ?? string.Empty).Trim().Trim('/', '\\').Replace('\\', '/');
            if (path.Length == 0)
            {
                throw new InvalidOperationException("A target folder is required; the site's root is not a place for connector output.");
            }

            var folder = await folders.GetFolderAsync(alias.SiteId, path + "/")
                ?? throw new InvalidOperationException($"The folder '{path}' does not exist on this site; create it in the Files admin first.");
            var directory = await folders.GetFolderPathAsync(folder);
            Directory.CreateDirectory(directory);

            var result = await guard.AcceptChunkAsync(new FileIngestionTarget(alias, folder, directory), fileName, 1, 1, content, length, cancellationToken);
            if (!result.IsAccepted)
            {
                throw new InvalidOperationException($"The file '{fileName}' was refused ({result.Reason}): {result.Message}");
            }

            var info = new FileInfo(Path.Combine(directory, fileName));
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
            var row = await files.GetFileAsync(folder.FolderId, fileName);
            var isNew = row is null;
            row ??= new Models.File { FolderId = folder.FolderId, Name = fileName };
            row.Extension = info.Extension.TrimStart('.').ToLowerInvariant();
            row.Size = (int)Math.Min(info.Length, int.MaxValue);
            row.ImageHeight = 0;
            row.ImageWidth = 0;
            row = isNew ? await files.AddFileAsync(row) : await files.UpdateFileAsync(row);
            return row.FileId;
        }
    }
}
