#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>One entry of a file server's folder: a <c>file</c>, a <c>folder</c> or a <c>link</c>, by its path from the server's root.</summary>
    internal sealed record RemoteEntry(string Name, string Path, string Type, long? Size, DateTimeOffset? Modified)
    {
        public const string File = "file";

        public const string Folder = "folder";

        public const string Link = "link";
    }

    /// <summary>
    /// A signed-in session on a file server - FTP's or SFTP's - in the few words the file operations need (n8n's FTP
    /// node over either protocol). A folder's entries are its direct children, never its own <c>.</c> and <c>..</c>; a missing
    /// path is null from <see cref="StatAsync"/>; an upload makes the folders on its way and replaces a file of its name.
    /// </summary>
    internal interface IRemoteFileSession : IAsyncDisposable
    {
        Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken);

        Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken);

        Task DownloadAsync(string path, Stream output, CancellationToken cancellationToken);

        Task UploadAsync(string path, Stream content, CancellationToken cancellationToken);

        Task CreateFolderAsync(string path, CancellationToken cancellationToken);

        Task DeleteFileAsync(string path, CancellationToken cancellationToken);

        Task DeleteFolderAsync(string path, CancellationToken cancellationToken);

        Task RenameAsync(string from, string to, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The file operations the FTP and SFTP connectors share (n8n's FTP node made whole): a folder listed - its folders
    /// too, its subfolders' entries too - files downloaded into a File Manager folder by a pattern, one file downloaded into the
    /// pipeline run's folder and one uploaded from it (or text), a file or a folder deleted, an entry renamed or moved. Every
    /// parameter is checked before the server is dialled; a remote path holding a control character is refused, since FTP
    /// sends it on a command line. A link is listed, never followed, so a walk cannot loop; a folder's content is deleted only
    /// when asked, and the server's root never.
    /// </summary>
    internal static class RemoteFiles
    {
        public const string ListOperation = "list";

        public const string DownloadOperation = "download";

        public const string DownloadFileOperation = "download_file";

        public const string UploadOperation = "upload";

        public const string DeleteOperation = "delete";

        public const string RenameOperation = "rename";

        public const int DefaultMax = 10_000;

        /// <summary>How many entries a recursive walk reads at most, matched or not - a tree past it is narrowed by its path.</summary>
        public const int WalkLimit = 100_000;

        private static readonly ConnectorConfigField PathField = new("path", "Remote path", ConnectorConfigFieldType.Text, Required: false, Description: "The remote folder whose direct children are matched.") { Default = "/" };

        private static readonly ConnectorConfigField PatternField = new("pattern", "File pattern", ConnectorConfigFieldType.Text, Required: false, Description: "Wildcard (*/?) matched against each remote file name.") { Default = "*" };

        public static IReadOnlyList<ConnectorOperation> Operations(string server) => new[]
        {
            new ConnectorOperation(ListOperation, "List files", $"Lists the {server}'s entries matching the wildcard - one record per entry (name, path, type, size, modified); its folders and their content when asked; n8n's List.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    PathField,
                    PatternField,
                    new ConnectorConfigField("recursive", "Recursive", ConnectorConfigFieldType.Boolean, Required: false, Description: "The entries of its folders too, and theirs - a link is listed, never followed.") { Default = "false" },
                    new ConnectorConfigField("folders", "Folders too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Its folders as records of their own, beside its files and links.") { Default = "false" },
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(DownloadOperation, "Download files", $"Downloads each matching file of the {server}'s folder into the managed File Manager folder - one record per stored file.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    PathField,
                    PatternField,
                    new ConnectorConfigField("target_folder", "Target folder", ConnectorConfigFieldType.Text, Required: true, Description: "The managed File Manager folder the downloaded files are stored in (a folder of this site, by path)."),
                }),
            },
            new ConnectorOperation(DownloadFileOperation, "Download a file", $"One file of the {server} written into the run's pipeline folder, for the file steps to read - n8n's Download.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Remote file", ConnectorConfigFieldType.Text, Required: true, Description: "Its path on the server - /kvitton/1042.csv."),
                    new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: true, Description: "Where in the site's pipeline folder it is written - downloads/1042.csv."),
                }),
            },
            new ConnectorOperation(UploadOperation, "Upload a file", $"A file of the run's pipeline folder, or text, stored on the {server} - its folders made on the way, a file of its name replaced; n8n's Upload.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Remote file", ConnectorConfigFieldType.Text, Required: true, Description: "Its path on the server - /kvitton/1042.csv."),
                    new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: false, Description: "The file of the site's pipeline folder sent - exports/1042.csv. Empty: the text."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Text, Required: false, Description: "Text sent as the file's content (UTF-8), where no file is given."),
                }),
            },
            new ConnectorOperation(DeleteOperation, "Delete", $"A file, or a folder, deleted from the {server} - a folder's content only when asked; n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Remote path", ConnectorConfigFieldType.Text, Required: true, Description: "Its path on the server."),
                    new ConnectorConfigField("folder", "A folder", ConnectorConfigFieldType.Boolean, Required: false, Description: "The path is a folder.") { Default = "false" },
                    new ConnectorConfigField("recursive", "Its content too", ConnectorConfigFieldType.Boolean, Required: false, Description: "A folder's files and folders deleted with it - without it, a folder that is not empty is refused.") { Default = "false" },
                }),
            },
            new ConnectorOperation(RenameOperation, "Rename or move", $"An entry of the {server} given another path - n8n's Rename.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("from", "Path", ConnectorConfigFieldType.Text, Required: true, Description: "Its path now."),
                    new ConnectorConfigField("to", "New path", ConnectorConfigFieldType.Text, Required: true, Description: "Its path after."),
                    new ConnectorConfigField("create_folders", "Make its folders", ConnectorConfigFieldType.Boolean, Required: false, Description: "The new path's folders made where missing.") { Default = "false" },
                }),
            },
        };

        /// <summary>
        /// Runs <paramref name="context"/>'s operation on a session <paramref name="open"/> makes - every parameter checked first,
        /// so a misconfigured step fails before any connection.
        /// </summary>
        public static async IAsyncEnumerable<ConnectorRecord> RunAsync(ConnectorExtractContext context, Func<CancellationToken, Task<IRemoteFileSession>> open, string server, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var p = context.Parameters;
            var operation = (context.OperationName ?? string.Empty).Trim().ToLowerInvariant();
            if (operation is not (ListOperation or DownloadOperation or DownloadFileOperation or UploadOperation or DeleteOperation or RenameOperation))
            {
                throw new InvalidOperationException($"'{context.OperationName}' is not an operation of the {server} connector - list, download, download_file, upload, delete or rename.");
            }

            switch (operation)
            {
                case ListOperation:
                    {
                        var folder = RemotePath(Values.Get(p, "path") ?? "/", "path");
                        var pattern = Values.Get(p, "pattern") ?? "*";
                        var recursive = Flag(p, "recursive");
                        var folders = Flag(p, "folders");
                        var max = Number(p, "max", DefaultMax);
                        await using var session = await open(cancellationToken);
                        var yielded = 0;
                        var walked = 0;
                        var pending = new Stack<IEnumerator<RemoteEntry>>();
                        pending.Push(Ordered(await session.ListAsync(folder, cancellationToken)).GetEnumerator());
                        while (pending.Count > 0 && yielded < max)
                        {
                            var at = pending.Peek();
                            if (!at.MoveNext())
                            {
                                pending.Pop().Dispose();
                                continue;
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            if (++walked > WalkLimit)
                            {
                                throw new InvalidOperationException($"The walk under '{folder}' passed {WalkLimit} entries - narrow it by its path.");
                            }

                            var entry = at.Current;
                            var isFolder = entry.Type == RemoteEntry.Folder;
                            if ((!isFolder || folders) && MatchesWildcard(entry.Name, pattern))
                            {
                                yielded++;
                                yield return Record(("name", entry.Name), ("path", entry.Path), ("type", entry.Type), ("size", entry.Size), ("modified", Iso(entry.Modified)));
                            }

                            if (isFolder && recursive)
                            {
                                pending.Push(Ordered(await session.ListAsync(entry.Path, cancellationToken)).GetEnumerator());
                            }
                        }

                        break;
                    }

                case DownloadOperation:
                    {
                        var folder = RemotePath(Values.Get(p, "path") ?? "/", "path");
                        var pattern = Values.Get(p, "pattern") ?? "*";
                        var targetFolder = (Values.Get(p, "target_folder") ?? string.Empty).Trim('/');
                        if (targetFolder.Length == 0)
                        {
                            throw new InvalidOperationException("The 'target_folder' parameter is required for the download operation.");
                        }

                        var sink = context.FileSink ?? throw new InvalidOperationException("The host does not provide managed file storage (no connector file sink is registered); the download operation requires it.");
                        await using var session = await open(cancellationToken);
                        foreach (var item in Ordered(await session.ListAsync(folder, cancellationToken)).Where(entry => entry.Type == RemoteEntry.File && MatchesWildcard(entry.Name, pattern)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (item.Size > sink.MaxBytes)
                            {
                                throw new InvalidOperationException($"The remote file '{item.Name}' is {item.Size} bytes, over the host's {sink.MaxBytes}-byte cap; it is not fetched.");
                            }

                            using var content = new MemoryStream();
                            await session.DownloadAsync(item.Path, new CappedStream(content, sink.MaxBytes, item.Name), cancellationToken);
                            content.Position = 0;
                            var length = content.Length;
                            var storedId = await sink.StoreAsync(item.Name, targetFolder, content, length, cancellationToken);
                            yield return Record(("name", item.Name), ("remote_path", item.Path), ("size", length), ("stored_path", $"{targetFolder}/{item.Name}"), ("stored_id", storedId));
                        }

                        break;
                    }

                case DownloadFileOperation:
                    {
                        var path = RemotePath(Values.Get(p, "path"), "path");
                        var file = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder the remote file is written.");
                        var files = context.Files ?? throw new InvalidOperationException("A file's download writes the run's pipeline folder - run it from a pipeline.");
                        await using var session = await open(cancellationToken);
                        var found = await session.StatAsync(path, cancellationToken) ?? throw new InvalidOperationException($"'{path}' is not on the {server}.");
                        if (found.Type == RemoteEntry.Folder)
                        {
                            throw new InvalidOperationException($"'{path}' is a folder - download a file, or list the folder.");
                        }

                        if (found.Size > files.MaxBytes)
                        {
                            throw new InvalidOperationException($"'{path}' is {found.Size} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                        }

                        var written = await CopyAsync((output, token) => session.DownloadAsync(path, output, token), (input, token) => files.WriteAsync(file, input, token), cancellationToken);
                        yield return Record(("path", path), ("file", file), ("size", written), ("modified", Iso(found.Modified)));
                        break;
                    }

                case UploadOperation:
                    {
                        var path = RemotePath(Values.Get(p, "path"), "path");
                        var file = Values.Get(p, "file");
                        var text = p.TryGetValue("text", out var given) && given.Length > 0 ? given : null;
                        if (file is not null && text is not null)
                        {
                            throw new InvalidOperationException("Give the file or the text - not both.");
                        }

                        if (file is null && text is null)
                        {
                            throw new InvalidOperationException("Give the file of the site's pipeline folder to send, or the text.");
                        }

                        if (file is not null && context.Files is null)
                        {
                            throw new InvalidOperationException("A file's upload reads the run's pipeline folder - run it from a pipeline.");
                        }

                        await using var session = await open(cancellationToken);
                        long size;
                        if (text is not null)
                        {
                            var bytes = Encoding.UTF8.GetBytes(text);
                            using var content = new MemoryStream(bytes, writable: false);
                            await session.UploadAsync(path, content, cancellationToken);
                            size = bytes.LongLength;
                        }
                        else
                        {
                            await using var opened = await context.Files!.OpenReadAsync(file!, cancellationToken);
                            await session.UploadAsync(path, opened.Content, cancellationToken);
                            size = opened.Length;
                        }

                        yield return Record(("path", path), ("size", size), ("file", file));
                        break;
                    }

                case DeleteOperation:
                    {
                        var path = RemotePath(Values.Get(p, "path"), "path");
                        var folder = Flag(p, "folder");
                        var recursive = Flag(p, "recursive");
                        if (recursive && !folder)
                        {
                            throw new InvalidOperationException("Its content too is a folder's - set folder = true as well.");
                        }

                        if (path.Trim('/', '.').Length == 0)
                        {
                            throw new InvalidOperationException($"'{path}' is the server's root - it is never deleted.");
                        }

                        await using var session = await open(cancellationToken);
                        var found = await session.StatAsync(path, cancellationToken) ?? throw new InvalidOperationException($"'{path}' is not on the {server}.");
                        if (!folder)
                        {
                            if (found.Type == RemoteEntry.Folder)
                            {
                                throw new InvalidOperationException($"'{path}' is a folder - set folder = true to delete it.");
                            }

                            await session.DeleteFileAsync(path, cancellationToken);
                            yield return Record(("path", path), ("deleted", true), ("folder", false));
                            break;
                        }

                        if (found.Type != RemoteEntry.Folder)
                        {
                            throw new InvalidOperationException($"'{path}' is not a folder - delete it with folder = false.");
                        }

                        var inside = Ordered(await session.ListAsync(path, cancellationToken)).ToList();
                        if (inside.Count > 0 && !recursive)
                        {
                            throw new InvalidOperationException($"'{path}' is not empty ({inside.Count} {(inside.Count == 1 ? "entry" : "entries")}) - delete its content too with recursive = true.");
                        }

                        var deleted = await DeleteTreeAsync(session, path, inside, cancellationToken);
                        yield return Record(("path", path), ("deleted", true), ("folder", true), ("entries_deleted", deleted));
                        break;
                    }

                case RenameOperation:
                    {
                        var from = RemotePath(Values.Get(p, "from"), "from");
                        var to = RemotePath(Values.Get(p, "to"), "to");
                        var createFolders = Flag(p, "create_folders");
                        await using var session = await open(cancellationToken);
                        if (createFolders && Parent(to) is { } parent)
                        {
                            await session.CreateFolderAsync(parent, cancellationToken);
                        }

                        await session.RenameAsync(from, to, cancellationToken);
                        yield return Record(("from", from), ("to", to), ("renamed", true));
                        break;
                    }
            }
        }

        /// <summary>A remote path as given, trimmed: never empty, never holding a control character - FTP sends it on a command line, where one would end it.</summary>
        internal static string RemotePath(string? given, string name)
        {
            var path = given?.Trim() ?? string.Empty;
            if (path.Length == 0)
            {
                throw new InvalidOperationException($"Give the path on the server ({name}).");
            }

            return path.Any(char.IsControl) ? throw new InvalidOperationException($"'{path.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}' holds a control character - a path never does ({name}).") : path;
        }

        /// <summary>The folder a path is in - null for an entry of the root or of the login folder.</summary>
        internal static string? Parent(string path)
        {
            var trimmed = path.TrimEnd('/');
            var cut = trimmed.LastIndexOf('/');
            return cut > 0 ? trimmed[..cut] : null;
        }

        /// <summary>An entry's path in <paramref name="folder"/>.</summary>
        internal static string Join(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

        /// <summary>Anchored <c>*</c>/<c>?</c> wildcard match over a remote name, case-insensitive.</summary>
        internal static bool MatchesWildcard(string name, string pattern) =>
            Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The bytes <paramref name="produce"/> writes, handed to <paramref name="consume"/> as they arrive - a download streamed into
        /// the run's folder without holding the file. The producer's failure is the one reported when it fails first; the
        /// consumer's (the folder's cap) when it does, the producer then stopped.
        /// </summary>
        internal static async Task<long> CopyAsync(Func<Stream, CancellationToken, Task> produce, Func<Stream, CancellationToken, Task<long>> consume, CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producing = Task.Run(async () =>
            {
                try
                {
                    await using (var output = pipe.Writer.AsStream(leaveOpen: true))
                    {
                        await produce(output, stop.Token);
                    }

                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                    throw;
                }
            }, CancellationToken.None);

            long consumed;
            try
            {
                await using (var input = pipe.Reader.AsStream(leaveOpen: true))
                {
                    consumed = await consume(input, stop.Token);
                }

                await pipe.Reader.CompleteAsync();
            }
            catch (Exception ex)
            {
                await pipe.Reader.CompleteAsync(ex);
                await stop.CancelAsync();
                try
                {
                    await producing;
                }
                catch (Exception)
                {
                    // The consumer saw the producer's failure, or failed first and stopped it: the consumer's is reported.
                }

                throw;
            }

            await producing;
            return consumed;
        }

        private static async Task<int> DeleteTreeAsync(IRemoteFileSession session, string folder, IReadOnlyList<RemoteEntry> inside, CancellationToken cancellationToken)
        {
            var deleted = 0;
            foreach (var entry in inside)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Type == RemoteEntry.Folder)
                {
                    deleted += await DeleteTreeAsync(session, entry.Path, Ordered(await session.ListAsync(entry.Path, cancellationToken)).ToList(), cancellationToken);
                    deleted++;
                }
                else
                {
                    await session.DeleteFileAsync(entry.Path, cancellationToken);
                    deleted++;
                }
            }

            await session.DeleteFolderAsync(folder, cancellationToken);
            return deleted;
        }

        private static IEnumerable<RemoteEntry> Ordered(IEnumerable<RemoteEntry> entries) =>
            entries.Where(entry => entry.Name is not ("." or "..")).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToList();

        private static string? Iso(DateTimeOffset? at) => at?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        private static int Number(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is not { } given ? fallback
            : int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n
            : throw new InvalidOperationException($"'{given}' is not a number above 0 ({name}).");

        private static bool Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given && (bool.TryParse(given, out var yes) ? yes : throw new InvalidOperationException($"'{given}' is not true or false ({name})."));

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in fields)
            {
                values[name] = value;
            }

            return new ConnectorRecord(values);
        }

        /// <summary>A write-only stream that refuses to pass <c>cap</c> bytes - a download whose size the listing did not say, held to the sink's cap.</summary>
        private sealed class CappedStream(Stream inner, long cap, string name) : Stream
        {
            private long written;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => written;

            public override long Position { get => written; set => throw new NotSupportedException(); }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Count(count);
                inner.Write(buffer, offset, count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Count(buffer.Length);
                await inner.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            private void Count(int count)
            {
                written += count;
                if (written > cap)
                {
                    throw new InvalidOperationException($"The remote file '{name}' passed the host's {cap}-byte cap; it is not stored.");
                }
            }
        }
    }
}
