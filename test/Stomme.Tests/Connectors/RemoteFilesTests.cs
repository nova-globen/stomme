#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Steps;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The file operations FTP and SFTP share, on a server kept in memory: a folder listed in name order - its folders
    /// only when asked, its subfolders' entries only when recursive, a link never followed, no further than max, a walk past its
    /// limit refused; files stored into a File Manager folder by a pattern, one over the sink's cap refused before it is fetched
    /// and one whose size was not said stopped at the cap; one file downloaded into the run's folder - a missing path, a folder
    /// and one over the folder's cap refused before a byte is fetched, a download failing half-way leaving no file - and one
    /// uploaded from it or as text; a file or a folder deleted - the root never, a folder's content only when asked, deepest
    /// first; an entry renamed, its folders made only when asked. Every parameter is checked before the server is dialled.
    /// </summary>
    public sealed class RemoteFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "stomme-remote-" + Guid.NewGuid().ToString("N"));

        public RemoteFilesTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>A file server in memory: its entries by path, what was asked of it, and a download that can fail half-way.</summary>
        private sealed class MemoryServer
        {
            public Dictionary<string, (string Type, byte[] Content, long? Size)> Entries { get; } = new(StringComparer.Ordinal);

            public List<string> Calls { get; } = new();

            public int Opened { get; private set; }

            public bool FailHalfWay { get; init; }

            public MemoryServer Folder(string path)
            {
                Entries[path] = (RemoteEntry.Folder, Array.Empty<byte>(), null);
                return this;
            }

            public MemoryServer File(string path, string content, bool sizeSaid = true)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                Entries[path] = (RemoteEntry.File, bytes, sizeSaid ? bytes.LongLength : null);
                return this;
            }

            public MemoryServer Link(string path)
            {
                Entries[path] = (RemoteEntry.Link, Array.Empty<byte>(), 7);
                return this;
            }

            public Task<IRemoteFileSession> Open(CancellationToken cancellationToken)
            {
                Opened++;
                return Task.FromResult<IRemoteFileSession>(new Session(this));
            }

            private sealed class Session(MemoryServer server) : IRemoteFileSession
            {
                public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
                {
                    server.Calls.Add("list " + folder);
                    var prefix = folder.TrimEnd('/') + "/";
                    IReadOnlyList<RemoteEntry> entries = server.Entries
                        .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !pair.Key[prefix.Length..].Contains('/'))
                        .Select(pair => new RemoteEntry(pair.Key[prefix.Length..], pair.Key, pair.Value.Type, pair.Value.Size, new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero)))
                        .Reverse() // the server's own order is not the records'
                        .Concat(new[] { new RemoteEntry(".", folder, RemoteEntry.Folder, null, null), new RemoteEntry("..", folder + "/..", RemoteEntry.Folder, null, null) }) // as a LIST can
                        .ToList();
                    return Task.FromResult(entries);
                }

                public Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken)
                {
                    server.Calls.Add("stat " + path);
                    return Task.FromResult(server.Entries.TryGetValue(path, out var entry) ? new RemoteEntry(path[(path.LastIndexOf('/') + 1)..], path, entry.Type, entry.Size, null) : null);
                }

                public async Task DownloadAsync(string path, Stream output, CancellationToken cancellationToken)
                {
                    server.Calls.Add("download " + path);
                    var content = server.Entries[path].Content;
                    if (server.FailHalfWay)
                    {
                        await output.WriteAsync(content.AsMemory(0, content.Length / 2), cancellationToken);
                        throw new InvalidOperationException("The server closed the data connection half-way.");
                    }

                    await output.WriteAsync(content, cancellationToken);
                }

                public async Task UploadAsync(string path, Stream content, CancellationToken cancellationToken)
                {
                    using var copy = new MemoryStream();
                    await content.CopyToAsync(copy, cancellationToken);
                    server.Calls.Add("upload " + path);
                    server.Entries[path] = (RemoteEntry.File, copy.ToArray(), copy.Length);
                }

                public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
                {
                    server.Calls.Add("mkdir " + path);
                    return Task.CompletedTask;
                }

                public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
                {
                    server.Calls.Add("delete " + path);
                    server.Entries.Remove(path);
                    return Task.CompletedTask;
                }

                public Task DeleteFolderAsync(string path, CancellationToken cancellationToken)
                {
                    server.Calls.Add("rmdir " + path);
                    server.Entries.Remove(path);
                    return Task.CompletedTask;
                }

                public Task RenameAsync(string from, string to, CancellationToken cancellationToken)
                {
                    server.Calls.Add("rename " + from + " " + to);
                    server.Entries[to] = server.Entries[from];
                    server.Entries.Remove(from);
                    return Task.CompletedTask;
                }

                public ValueTask DisposeAsync()
                {
                    server.Calls.Add("bye");
                    return ValueTask.CompletedTask;
                }
            }
        }

        private sealed class Sink(long cap) : IConnectorFileSink
        {
            public List<(string Name, string Folder, string Text)> Stored { get; } = new();

            public long MaxBytes => cap;

            public async Task<int> StoreAsync(string fileName, string folderPath, Stream content, long length, CancellationToken cancellationToken = default)
            {
                using var reader = new StreamReader(content);
                Stored.Add((fileName, folderPath, await reader.ReadToEndAsync(cancellationToken)));
                return Stored.Count;
            }
        }

        private async Task<List<ConnectorRecord>> Run(MemoryServer server, string operation, Dictionary<string, string> parameters, bool files = true, long cap = 1 << 20, IConnectorFileSink? sink = null)
        {
            var context = Contexts.Extract(new StubEgress(), "", operation, parameters) with { Files = files ? PipelineConnectorFiles.Under(root, cap) : null, FileSink = sink };
            var records = new List<ConnectorRecord>();
            await foreach (var record in RemoteFiles.RunAsync(context, server.Open, "test server"))
            {
                records.Add(record);
            }

            return records;
        }

        private async Task<string> Refused(MemoryServer server, string operation, Dictionary<string, string> parameters, bool files = true, IConnectorFileSink? sink = null)
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(server, operation, parameters, files, sink: sink));
            return refusal.Message;
        }

        private static List<string> Names(IEnumerable<ConnectorRecord> records) => records.Select(record => (string)record.Fields["name"]!).ToList();

        private static MemoryServer Tree() => new MemoryServer()
            .Folder("/files").Folder("/files/kvitton").Folder("/files/kvitton/2026").Folder("/files/arkiv")
            .File("/files/b.csv", "b").File("/files/A.csv", "a").File("/files/a.txt", "t").Link("/files/senaste")
            .File("/files/kvitton/1042.csv", "1042").File("/files/kvitton/2026/1043.csv", "1043").Link("/files/arkiv/loop");

        [Fact]
        public void The_operations_are_declared_with_their_parameters()
        {
            var operations = RemoteFiles.Operations("FTP server");
            Assert.Equal(new[] { "list", "download", "download_file", "upload", "delete", "rename" }, operations.Select(o => o.Name).ToArray());
            Assert.All(operations, o => Assert.Contains("FTP server", o.Description, StringComparison.Ordinal));
            Assert.Equal("/", operations[0].Parameters.Fields.Single(f => f.Name == "path").Default);
            Assert.Equal("*", operations[0].Parameters.Fields.Single(f => f.Name == "pattern").Default);
            Assert.Equal(new[] { "false", "false", "10000" }, operations[0].Parameters.Fields.Where(f => f.Name is "recursive" or "folders" or "max").Select(f => f.Default).ToArray());
            Assert.True(operations[1].Parameters.Fields.Single(f => f.Name == "target_folder").Required);
            Assert.True(operations[2].Parameters.Fields.All(f => f.Required));
            Assert.Equal(new[] { "path" }, operations[3].Parameters.Fields.Where(f => f.Required).Select(f => f.Name).ToArray());
            Assert.Equal(new[] { "path" }, operations[4].Parameters.Fields.Where(f => f.Required).Select(f => f.Name).ToArray());
            Assert.Equal(new[] { "from", "to" }, operations[5].Parameters.Fields.Where(f => f.Required).Select(f => f.Name).ToArray());
        }

        [Fact]
        public async Task A_folder_is_listed_in_name_order_its_folders_and_subfolders_only_when_asked_and_a_link_never_followed()
        {
            var server = Tree();
            var files = await Run(server, "list", new() { ["path"] = "/files" });
            Assert.Equal(new[] { "A.csv", "a.txt", "b.csv", "senaste" }, Names(files));
            Assert.Equal(("/files/A.csv", "file", 1L, "2026-10-17T08:00:00.0000000Z"), ((string)files[0].Fields["path"]!, (string)files[0].Fields["type"]!, (long)files[0].Fields["size"]!, (string)files[0].Fields["modified"]!));
            Assert.Equal("link", files[3].Fields["type"]);
            Assert.Equal(new[] { "list /files", "bye" }, server.Calls); // nothing walked, the session closed

            Assert.Equal(new[] { "A.csv", "a.txt", "arkiv", "b.csv", "kvitton", "senaste" }, Names(await Run(server, "list", new() { ["path"] = "/files", ["folders"] = "true" })));
            server.Calls.Clear();
            var tree = await Run(server, "list", new() { ["path"] = "/files", ["recursive"] = "true", ["folders"] = "true" });
            Assert.Equal(new[] { "A.csv", "a.txt", "arkiv", "loop", "b.csv", "kvitton", "1042.csv", "2026", "1043.csv", "senaste" }, Names(tree));
            Assert.Equal(new[] { "list /files", "list /files/arkiv", "list /files/kvitton", "list /files/kvitton/2026", "bye" }, server.Calls); // the links listed, never listed into
            Assert.Equal(new[] { "1042.csv", "1043.csv" }, Names(await Run(server, "list", new() { ["path"] = "/files/kvitton", ["recursive"] = "true", ["pattern"] = "1*.csv" })));
            Assert.Equal(new[] { "A.csv", "a.txt" }, Names(await Run(server, "list", new() { ["path"] = "/files", ["recursive"] = "true", ["folders"] = "true", ["max"] = "2" })));
            Assert.Equal(new[] { "A.csv", "b.csv", "1042.csv", "1043.csv" }, Names(await Run(server, "list", new() { ["path"] = "/files", ["recursive"] = "true", ["pattern"] = "*.CSV" })));
        }

        [Fact]
        public async Task A_walk_past_its_limit_is_refused_and_narrowed_by_its_path()
        {
            var server = new MemoryServer().Folder("/big");
            for (var i = 0; i <= RemoteFiles.WalkLimit; i++)
            {
                server.Entries["/big/f" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)] = (RemoteEntry.File, Array.Empty<byte>(), 0);
            }

            var refusal = await Refused(server, "list", new() { ["path"] = "/big", ["pattern"] = "none" });
            Assert.Equal($"The walk under '/big' passed {RemoteFiles.WalkLimit} entries - narrow it by its path.", refusal);
            server.Entries.Remove("/big/f000000");
            Assert.Empty(await Run(server, "list", new() { ["path"] = "/big", ["pattern"] = "none" })); // exactly the limit: walked
        }

        [Fact]
        public async Task Files_are_stored_into_a_file_manager_folder_by_a_pattern_and_held_to_its_cap()
        {
            var server = Tree();
            var sink = new Sink(1 << 20);
            var stored = await Run(server, "download", new() { ["path"] = "/files", ["pattern"] = "*.csv", ["target_folder"] = "/imports/" }, sink: sink);
            Assert.Equal(new[] { ("A.csv", "imports", "a"), ("b.csv", "imports", "b") }, sink.Stored);
            Assert.Equal(("A.csv", "/files/A.csv", 1L, "imports/A.csv", 1), ((string)stored[0].Fields["name"]!, (string)stored[0].Fields["remote_path"]!, (long)stored[0].Fields["size"]!, (string)stored[0].Fields["stored_path"]!, (int)stored[0].Fields["stored_id"]!));

            var small = new Sink(3);
            var big = new MemoryServer().Folder("/in").File("/in/stor.csv", "fyra");
            Assert.Equal("The remote file 'stor.csv' is 4 bytes, over the host's 3-byte cap; it is not fetched.", await Refused(big, "download", new() { ["path"] = "/in", ["target_folder"] = "x" }, sink: small));
            Assert.DoesNotContain(big.Calls, call => call.StartsWith("download", StringComparison.Ordinal));
            var unsaid = new MemoryServer().Folder("/in").File("/in/stor.csv", "fyra", sizeSaid: false);
            Assert.Equal("The remote file 'stor.csv' passed the host's 3-byte cap; it is not stored.", await Refused(unsaid, "download", new() { ["path"] = "/in", ["target_folder"] = "x" }, sink: small));
            Assert.Empty(small.Stored);
            Assert.Single(await Run(new MemoryServer().Folder("/in").File("/in/tre.csv", "tre"), "download", new() { ["path"] = "/in", ["target_folder"] = "x" }, sink: small)); // exactly the cap

            var none = new MemoryServer();
            Assert.Equal("The 'target_folder' parameter is required for the download operation.", await Refused(none, "download", new() { ["target_folder"] = "/" }, sink: sink));
            Assert.Equal("The host does not provide managed file storage (no connector file sink is registered); the download operation requires it.", await Refused(none, "download", new() { ["target_folder"] = "imports" }));
            Assert.Equal(0, none.Opened);
        }

        [Fact]
        public async Task One_file_is_downloaded_into_the_runs_folder_and_a_failure_half_way_leaves_none()
        {
            var server = Tree();
            var got = Assert.Single(await Run(server, "download_file", new() { ["path"] = "/files/kvitton/1042.csv", ["file"] = "in/1042.csv" }));
            Assert.Equal("1042", await File.ReadAllTextAsync(Path.Combine(root, "in", "1042.csv")));
            Assert.Equal(("/files/kvitton/1042.csv", "in/1042.csv", 4L), ((string)got.Fields["path"]!, (string)got.Fields["file"]!, (long)got.Fields["size"]!));

            Assert.Equal("'/files/saknas.csv' is not on the test server.", await Refused(server, "download_file", new() { ["path"] = "/files/saknas.csv", ["file"] = "x" }));
            Assert.Equal("'/files/kvitton' is a folder - download a file, or list the folder.", await Refused(server, "download_file", new() { ["path"] = "/files/kvitton", ["file"] = "x" }));
            server.Calls.Clear();
            var tight = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(server, "download_file", new() { ["path"] = "/files/kvitton/1042.csv", ["file"] = "x" }, cap: 3));
            Assert.Equal("'/files/kvitton/1042.csv' is 4 bytes, over the pipeline folder's 3; it is not fetched.", tight.Message);
            Assert.DoesNotContain(server.Calls, call => call.StartsWith("download", StringComparison.Ordinal));
            Assert.Single(await Run(server, "download_file", new() { ["path"] = "/files/kvitton/1042.csv", ["file"] = "x" }, cap: 4)); // exactly the cap

            var unsaid = new MemoryServer().File("/stor.csv", "fyra", sizeSaid: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unsaid, "download_file", new() { ["path"] = "/stor.csv", ["file"] = "stor.csv" }, cap: 3));
            Assert.False(File.Exists(Path.Combine(root, "stor.csv"))); // the folder's own cap stopped it

            var broken = new MemoryServer { FailHalfWay = true }.File("/halv.csv", "hela innehållet");
            Assert.Equal("The server closed the data connection half-way.", await Refused(broken, "download_file", new() { ["path"] = "/halv.csv", ["file"] = "halv.csv" }));
            Assert.False(File.Exists(Path.Combine(root, "halv.csv")));

            var none = new MemoryServer();
            Assert.Equal("Give the file - where in the site's pipeline folder the remote file is written.", await Refused(none, "download_file", new() { ["path"] = "/a" }));
            Assert.Equal("A file's download writes the run's pipeline folder - run it from a pipeline.", await Refused(none, "download_file", new() { ["path"] = "/a", ["file"] = "a" }, files: false));
            Assert.Equal("Give the path on the server (path).", await Refused(none, "download_file", new() { ["path"] = "  ", ["file"] = "a" }));
            Assert.Equal(0, none.Opened);
        }

        [Fact]
        public async Task A_file_of_the_runs_folder_or_text_is_uploaded()
        {
            var server = new MemoryServer();
            await File.WriteAllTextAsync(Path.Combine(root, "export.csv"), "nummer;belopp\n1042;99.50\n");
            var sent = Assert.Single(await Run(server, "upload", new() { ["path"] = "/ut/1042.csv", ["file"] = "export.csv" }));
            Assert.Equal(("/ut/1042.csv", 25L, "export.csv"), ((string)sent.Fields["path"]!, (long)sent.Fields["size"]!, (string)sent.Fields["file"]!));
            Assert.Equal("nummer;belopp\n1042;99.50\n", Encoding.UTF8.GetString(server.Entries["/ut/1042.csv"].Content));

            var text = Assert.Single(await Run(server, "upload", new() { ["path"] = "/ut/hej.txt", ["text"] = " Hej från kassan " }));
            Assert.Equal(" Hej från kassan ", Encoding.UTF8.GetString(server.Entries["/ut/hej.txt"].Content)); // as given, untrimmed
            Assert.Equal((18L, (object?)null), ((long)text.Fields["size"]!, text.Fields["file"]));

            var none = new MemoryServer();
            Assert.Equal("Give the file or the text - not both.", await Refused(none, "upload", new() { ["path"] = "/a", ["file"] = "a", ["text"] = "b" }));
            Assert.Equal("Give the file of the site's pipeline folder to send, or the text.", await Refused(none, "upload", new() { ["path"] = "/a", ["text"] = "" }));
            Assert.Equal("A file's upload reads the run's pipeline folder - run it from a pipeline.", await Refused(none, "upload", new() { ["path"] = "/a", ["file"] = "a" }, files: false));
            Assert.Single(await Run(none, "upload", new() { ["path"] = "/a", ["text"] = "b" }, files: false)); // text needs no folder
            Assert.Equal(1, none.Opened);
        }

        [Fact]
        public async Task A_file_or_a_folder_is_deleted_its_content_only_when_asked_and_the_root_never()
        {
            var server = Tree();
            Assert.Equal(true, Assert.Single(await Run(server, "delete", new() { ["path"] = "/files/a.txt" })).Fields["deleted"]);
            Assert.DoesNotContain("/files/a.txt", server.Entries.Keys);
            Assert.Equal("'/files/kvitton' is a folder - set folder = true to delete it.", await Refused(server, "delete", new() { ["path"] = "/files/kvitton" }));
            Assert.Equal("'/files/b.csv' is not a folder - delete it with folder = false.", await Refused(server, "delete", new() { ["path"] = "/files/b.csv", ["folder"] = "true" }));
            Assert.Equal("'/files/kvitton' is not empty (2 entries) - delete its content too with recursive = true.", await Refused(server, "delete", new() { ["path"] = "/files/kvitton", ["folder"] = "true" }));
            Assert.Equal("'/files/saknas' is not on the test server.", await Refused(server, "delete", new() { ["path"] = "/files/saknas" }));
            Assert.Equal("'/files/arkiv' is not empty (1 entry) - delete its content too with recursive = true.", await Refused(server, "delete", new() { ["path"] = "/files/arkiv", ["folder"] = "true" }));

            server.Calls.Clear();
            var all = Assert.Single(await Run(server, "delete", new() { ["path"] = "/files/kvitton", ["folder"] = "true", ["recursive"] = "true" }));
            Assert.Equal((true, true, 3), ((bool)all.Fields["deleted"]!, (bool)all.Fields["folder"]!, (int)all.Fields["entries_deleted"]!));
            Assert.Equal(new[] { "stat /files/kvitton", "list /files/kvitton", "delete /files/kvitton/1042.csv", "list /files/kvitton/2026", "delete /files/kvitton/2026/1043.csv", "rmdir /files/kvitton/2026", "rmdir /files/kvitton", "bye" }, server.Calls);
            server.Calls.Clear();
            Assert.Equal(1, Assert.Single(await Run(server, "delete", new() { ["path"] = "/files/arkiv", ["folder"] = "true", ["recursive"] = "true" })).Fields["entries_deleted"]);
            Assert.Contains("delete /files/arkiv/loop", server.Calls); // the link removed, its target never walked
            server.Folder("/files/tom");
            Assert.Equal(0, Assert.Single(await Run(server, "delete", new() { ["path"] = "/files/tom", ["folder"] = "true" })).Fields["entries_deleted"]); // an empty folder needs no recursive

            var none = new MemoryServer();
            foreach (var rootPath in new[] { "/", ".", "./", "//" })
            {
                Assert.Equal($"'{rootPath}' is the server's root - it is never deleted.", await Refused(none, "delete", new() { ["path"] = rootPath, ["folder"] = "true", ["recursive"] = "true" }));
            }

            Assert.Equal("Its content too is a folder's - set folder = true as well.", await Refused(none, "delete", new() { ["path"] = "/a", ["recursive"] = "true" }));
            Assert.Equal(0, none.Opened);
        }

        [Fact]
        public async Task An_entry_is_renamed_its_folders_made_only_when_asked()
        {
            var server = Tree();
            var moved = Assert.Single(await Run(server, "rename", new() { ["from"] = "/files/b.csv", ["to"] = "/files/arkiv/2026/b.csv", ["create_folders"] = "true" }));
            Assert.Equal(("/files/b.csv", "/files/arkiv/2026/b.csv", true), ((string)moved.Fields["from"]!, (string)moved.Fields["to"]!, (bool)moved.Fields["renamed"]!));
            Assert.Equal(new[] { "mkdir /files/arkiv/2026", "rename /files/b.csv /files/arkiv/2026/b.csv", "bye" }, server.Calls);
            server.Calls.Clear();
            await Run(server, "rename", new() { ["from"] = "/files/A.csv", ["to"] = "/files/C.csv" });
            Assert.Equal(new[] { "rename /files/A.csv /files/C.csv", "bye" }, server.Calls);
            server.Calls.Clear();
            await Run(server, "rename", new() { ["from"] = "/files/C.csv", ["to"] = "/C.csv", ["create_folders"] = "true" });
            Assert.Equal(new[] { "rename /files/C.csv /C.csv", "bye" }, server.Calls); // the root needs no folder made

            var none = new MemoryServer();
            Assert.Equal("Give the path on the server (to).", await Refused(none, "rename", new() { ["from"] = "/a" }));
            Assert.Equal("Give the path on the server (from).", await Refused(none, "rename", new() { ["to"] = "/a" }));
            Assert.Equal(0, none.Opened);
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_the_server_is_dialled()
        {
            var none = new MemoryServer();
            Assert.Equal("'/files/a\\r\\nDELE x' holds a control character - a path never does (path).", await Refused(none, "list", new() { ["path"] = "/files/a\r\nDELE x" }));
            Assert.Equal("'b\\tc' holds a control character - a path never does (to).", await Refused(none, "rename", new() { ["from"] = "/a", ["to"] = "b\tc" }));
            Assert.Equal("'many' is not a number above 0 (max).", await Refused(none, "list", new() { ["max"] = "many" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(none, "list", new() { ["max"] = "0" }));
            Assert.Equal("'yes' is not true or false (recursive).", await Refused(none, "list", new() { ["recursive"] = "yes" }));
            Assert.Equal("'yes' is not true or false (folders).", await Refused(none, "list", new() { ["folders"] = "yes" }));
            Assert.Equal("'yes' is not true or false (folder).", await Refused(none, "delete", new() { ["path"] = "/a", ["folder"] = "yes" }));
            Assert.Equal("'yes' is not true or false (create_folders).", await Refused(none, "rename", new() { ["from"] = "/a", ["to"] = "/b", ["create_folders"] = "yes" }));
            Assert.Equal("'mkdir' is not an operation of the test server connector - list, download, download_file, upload, delete or rename.", await Refused(none, "mkdir", new()));
            Assert.Equal(0, none.Opened);
            Assert.Single(await Run(new MemoryServer().File("/a.csv", "a"), " LIST ", new() { ["path"] = " / " })); // the operation's case and spaces, the path's spaces
        }

        [Fact]
        public void A_paths_folder_and_an_entrys_path_in_a_folder()
        {
            Assert.Equal("/files/kvitton", RemoteFiles.Parent("/files/kvitton/1042.csv"));
            Assert.Equal("/files", RemoteFiles.Parent("/files/kvitton/"));
            Assert.Null(RemoteFiles.Parent("/1042.csv"));
            Assert.Null(RemoteFiles.Parent("1042.csv"));
            Assert.Equal("kvitton", RemoteFiles.Parent("kvitton/1042.csv"));
            Assert.Equal("/a/b", RemoteFiles.Join("/a", "b"));
            Assert.Equal("/a/b", RemoteFiles.Join("/a/", "b"));
            Assert.Equal("/b", RemoteFiles.Join("/", "b"));
        }

        [Fact]
        public async Task A_copy_reports_the_producers_failure_or_the_consumers_and_stops_the_producer()
        {
            var produced = await Assert.ThrowsAsync<IOException>(() => RemoteFiles.CopyAsync(
                async (output, token) =>
                {
                    await output.WriteAsync(new byte[10], token);
                    throw new IOException("producer");
                },
                async (input, token) =>
                {
                    var buffer = new byte[100];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        total += read;
                    }

                    return total;
                },
                CancellationToken.None));
            Assert.Equal("producer", produced.Message);

            var stopped = false;
            var consumed = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteFiles.CopyAsync(
                async (output, token) =>
                {
                    try
                    {
                        while (true)
                        {
                            await output.WriteAsync(new byte[4096], token);
                            await Task.Delay(1, token);
                        }
                    }
                    catch (Exception)
                    {
                        stopped = true;
                        throw;
                    }
                },
                async (input, token) =>
                {
                    await input.ReadAsync(new byte[10], token);
                    throw new InvalidOperationException("consumer");
                },
                CancellationToken.None));
            Assert.Equal("consumer", consumed.Message);
            Assert.True(stopped);

            Assert.Equal(12, await RemoteFiles.CopyAsync((output, token) => output.WriteAsync(new byte[12], token).AsTask(), async (input, token) =>
            {
                using var copy = new MemoryStream();
                await input.CopyToAsync(copy, token);
                return copy.Length;
            }, CancellationToken.None));
        }
    }
}
