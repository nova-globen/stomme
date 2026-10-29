#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Connectivity
{
    /// <summary>
    /// The files of the place an operation runs in, handed to a connector by the HOST (the file sink extended, after n8n's
    /// binary data on its file nodes - S3, Google Drive, Dropbox, OneDrive): in a pipeline run, the run's site's pipeline folder,
    /// the one the file steps read and write. A connector reads a file it is to send (an upload) and writes one it received (a
    /// download) by a path RELATIVE to that folder, held to the folder's rules - never a rooted path, never outside the folder,
    /// at most <see cref="MaxBytes"/> - so it never names a host path. Absent outside a pipeline run; an operation that needs it
    /// says so.
    /// </summary>
    public interface IConnectorFiles
    {
        /// <summary>The largest file read or written, in bytes; a connector refuses a larger one before fetching or sending it.</summary>
        long MaxBytes { get; }

        /// <summary>Opens the file at <paramref name="path"/> for reading; refused (an <see cref="InvalidOperationException"/> saying why) when the path is not one of the folder's, the file is missing or it is over <see cref="MaxBytes"/>.</summary>
        Task<ConnectorFileContent> OpenReadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes <paramref name="content"/> to the file at <paramref name="path"/> as it streams in - beside it first, moved over it
        /// only when complete, so a failed or oversized write never leaves half a file under the name; the bytes written. Refused
        /// (an <see cref="InvalidOperationException"/>, nothing kept) when the path is not one of the folder's or the content
        /// passes <see cref="MaxBytes"/>.
        /// </summary>
        Task<long> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);
    }

    /// <summary>A file opened through <see cref="IConnectorFiles"/>: its path as given, its name, its length and its bytes, to dispose when sent.</summary>
    public sealed class ConnectorFileContent(string path, long length, Stream content) : IAsyncDisposable
    {
        /// <summary>The path as the operation named it, relative to the folder.</summary>
        public string Path { get; } = path;

        /// <summary>The file's own name, its last segment.</summary>
        public string Name => System.IO.Path.GetFileName(Path.Replace('\\', '/').TrimEnd('/'));

        public long Length { get; } = length;

        public Stream Content { get; } = content;

        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }
}
