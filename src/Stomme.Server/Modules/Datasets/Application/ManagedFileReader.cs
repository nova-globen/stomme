#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>A managed file opened for reading: its name, its size, and a read-only stream the caller disposes.</summary>
    public sealed record ManagedFile(string Name, long Size, Stream Content) : IDisposable
    {
        public void Dispose() => Content.Dispose();
    }

    /// <summary>
    /// Opens a file the framework's file manager holds, for a caller acting as the CURRENT user:
    /// the file must belong to the request's site (another site's id is simply not found), its folder must grant the
    /// current user View (the same check the file controller's download makes, from the same permission list, with no
    /// HttpContext - a headless caller has no user and is refused), and it must be within the server's upload cap
    /// (<c>Files:MaxUploadBytes</c>). The bytes are opened as a stream from the file's path; nothing is buffered here.
    /// </summary>
    public interface IManagedFileReader
    {
        Task<ManagedFile> OpenAsync(int fileId, CancellationToken ct = default);
    }

    public sealed class ManagedFileReader(IFileRepository files, IUserPermissions permissions, ICurrentUser currentUser, IFileIngestionGuard guard, SiteState siteState) : IManagedFileReader
    {
        public async Task<ManagedFile> OpenAsync(int fileId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var file = await files.GetFileAsync(fileId);
            if (file?.Folder is null || file.Folder.SiteId != siteState.Alias?.SiteId)
            {
                throw new FileNotFoundException($"File '{fileId}' does not exist.");
            }

            if (!currentUser.IsAuthenticated || !await permissions.IsAuthorizedAsync(Principal(currentUser), PermissionNames.View, file.Folder.PermissionList))
            {
                throw new UnauthorizedAccessException($"File '{fileId}' is in a folder the current user may not view.");
            }

            var cap = guard.Options.MaxUploadBytes;
            if (file.Size > cap)
            {
                throw new InvalidOperationException($"File '{file.Name}' is {file.Size} bytes, over the {cap}-byte upload cap; it is not read.");
            }

            var path = await files.GetFilePathAsync(file);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File '{file.Name}' has no bytes on disk.");
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
            if (stream.Length > cap)
            {
                stream.Dispose();
                throw new InvalidOperationException($"File '{file.Name}' is {file.Size} bytes on disk, over the {cap}-byte upload cap; it is not read.");
            }

            return new ManagedFile(file.Name, stream.Length, stream);
        }

        /// <summary>The current user as the principal the folder's permission list is evaluated for: the id, the name and every role.</summary>
        private static ClaimsPrincipal Principal(ICurrentUser user)
        {
            var claims = new List<Claim>();
            if (user.UserId is { } id)
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
            }

            if (!string.IsNullOrEmpty(user.UserName))
            {
                claims.Add(new Claim(ClaimTypes.Name, user.UserName));
            }

            foreach (var role in user.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Stomme"));
        }
    }

    /// <summary>The port the upload command consumes: an uploaded file materialised into the sample rows a dataset is inferred from, with the FULL row count.</summary>
    public interface IUploadedDatasetSource
    {
        Task<UploadedDatasetContent> ReadAsync(int fileId, CancellationToken ct = default);
    }

    public sealed record UploadedDatasetContent(string SuggestedName, string SampleJson, long RowCount, UploadFormat Format);

    /// <summary>The port's implementation over the managed-file reader and the hardened parser: the file's stem is the suggested name.</summary>
    public sealed class ManagedFileUploadedDatasetSource(IManagedFileReader reader) : IUploadedDatasetSource
    {
        public async Task<UploadedDatasetContent> ReadAsync(int fileId, CancellationToken ct = default)
        {
            using var file = await reader.OpenAsync(fileId, ct);
            var sample = UploadedDatasetParser.Parse(file.Content, file.Name);
            var stem = Path.GetFileNameWithoutExtension(file.Name);
            return new UploadedDatasetContent(string.IsNullOrWhiteSpace(stem) ? file.Name : stem, sample.SampleJson, sample.RowCount, sample.Format);
        }
    }
}
