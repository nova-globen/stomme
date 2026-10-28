#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Modules.Backup.Application.Archive;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (D106 item 3, gate <c>TenantContentFilesParticipantTests</c>) - the tenants' files under BOTH roots: a restore
    /// puts each tree back exactly (a file added later is gone, a changed one reverted, a deleted one back); a path that
    /// escapes, a repeated path and a truncated part are refused - and a refused replace leaves the live trees untouched,
    /// with no staging folder left behind.
    /// </summary>
    public sealed class TenantContentFilesBackupParticipantTests : IDisposable
    {
        private static readonly BackupScope Scope = new("10.2.3", new[] { 1 });
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-content-backup-" + Guid.NewGuid().ToString("N"));
        private readonly TenantContentFilesBackupParticipant _participant;

        public TenantContentFilesBackupParticipantTests()
        {
            _participant = new TenantContentFilesBackupParticipant(new Environment(Path.Combine(_root, "app"), Path.Combine(_root, "app", "wwwroot")));
        }

        private string ContentTree => Path.Combine(_root, "app", "Content", "Tenants");

        private string WebTree => Path.Combine(_root, "app", "wwwroot", "Content", "Tenants");

        [Fact]
        public async Task A_restore_puts_both_trees_back_exactly()
        {
            Write(ContentTree, "1/Sites/1/readme.txt", "private one");
            Write(ContentTree, "1/Sites/1/deep/nested/data.bin", "\u0000\u0001binary");
            Write(WebTree, "1/Sites/1/images/logo.png", "public logo");
            var before = (Tree(ContentTree), Tree(WebTree));
            var part = await ExportAsync();

            Write(ContentTree, "1/Sites/1/readme.txt", "changed after the snapshot");
            Write(ContentTree, "1/Sites/2/late.txt", "added after the snapshot");
            File.Delete(Path.Combine(WebTree, "1", "Sites", "1", "images", "logo.png"));
            Assert.NotEqual(before, (Tree(ContentTree), Tree(WebTree)));

            Assert.True((await _participant.ValidateAsync(new BytesPart(part), Scope, CancellationToken.None)).IsValid);
            await _participant.ReplaceAsync(new BytesPart(part), Scope, CancellationToken.None);

            Assert.Equal(before, (Tree(ContentTree), Tree(WebTree)));
            Assert.Empty(Leftovers());
        }

        [Fact]
        public async Task An_empty_snapshot_empties_the_trees()
        {
            var part = await ExportAsync();
            Write(ContentTree, "1/Sites/1/late.txt", "added after the snapshot");
            await _participant.ReplaceAsync(new BytesPart(part), Scope, CancellationToken.None);
            Assert.Empty(Tree(ContentTree));
            Assert.Empty(Leftovers());
        }

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("1/../../escape.txt")]
        [InlineData("/rooted.txt")]
        [InlineData("C:/drive.txt")]
        [InlineData("1\\back\\slash.txt")]
        [InlineData("1//empty.txt")]
        public async Task A_path_that_could_escape_is_refused_and_nothing_changes(string path)
        {
            Write(ContentTree, "1/Sites/1/keep.txt", "live");
            var before = Tree(ContentTree);
            var part = Record(1, path, "evil");

            var verdict = await _participant.ValidateAsync(new BytesPart(part), Scope, CancellationToken.None);
            Assert.False(verdict.IsValid);
            await Assert.ThrowsAsync<InvalidDataException>(() => _participant.ReplaceAsync(new BytesPart(part), Scope, CancellationToken.None));

            Assert.Equal(before, Tree(ContentTree));
            Assert.False(File.Exists(Path.Combine(_root, "app", "Content", "escape.txt")));
            Assert.Empty(Leftovers());
        }

        [Fact]
        public async Task A_repeated_path_or_a_truncated_part_is_refused_and_nothing_changes()
        {
            Write(ContentTree, "1/Sites/1/keep.txt", "live");
            var before = Tree(ContentTree);

            var repeated = Record(1, "1/a.txt", "one").Concat(Record(1, "1/a.txt", "two")).ToArray();
            Assert.Contains((await _participant.ValidateAsync(new BytesPart(repeated), Scope, CancellationToken.None)).Problems, p => p.Contains("twice", StringComparison.Ordinal));

            var truncated = Record(1, "1/a.txt", "a file cut short")[..^4];
            Assert.False((await _participant.ValidateAsync(new BytesPart(truncated), Scope, CancellationToken.None)).IsValid);
            await Assert.ThrowsAsync<InvalidDataException>(() => _participant.ReplaceAsync(new BytesPart(truncated), Scope, CancellationToken.None));

            Assert.Equal(before, Tree(ContentTree));
            Assert.Empty(Leftovers());
        }

        private async Task<byte[]> ExportAsync()
        {
            using var buffer = new MemoryStream();
            await _participant.ExportAsync(new ArchivePartWriter(_participant.Name, buffer), Scope, CancellationToken.None);
            return buffer.ToArray();
        }

        private static byte[] Record(byte root, string path, string content)
        {
            var name = Encoding.UTF8.GetBytes(path);
            var body = Encoding.UTF8.GetBytes(content);
            var record = new byte[1 + 4 + name.Length + 8 + body.Length];
            record[0] = root;
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(1), name.Length);
            name.CopyTo(record, 5);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(5 + name.Length), body.Length);
            body.CopyTo(record, 13 + name.Length);
            return record;
        }

        private static void Write(string tree, string relative, string content)
        {
            var path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        /// <summary>A tree as "relative path = content" lines, sorted - empty when the tree does not exist.</summary>
        private static string Tree(string tree) =>
            !Directory.Exists(tree) ? string.Empty : string.Join("\n", Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(tree, file).Replace(Path.DirectorySeparatorChar, '/') + "=" + Convert.ToBase64String(File.ReadAllBytes(file)))
                .OrderBy(line => line, StringComparer.Ordinal));

        /// <summary>Any staging or set-aside folder a replace left behind, in either root.</summary>
        private IEnumerable<string> Leftovers() =>
            new[] { Path.Combine(_root, "app", "Content"), Path.Combine(_root, "app", "wwwroot", "Content") }
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Where(folder => Path.GetFileName(folder).Contains(".restoring-", StringComparison.Ordinal) || Path.GetFileName(folder).Contains(".replaced-", StringComparison.Ordinal));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }

        private sealed class BytesPart(byte[] bytes) : IBackupPartReader
        {
            public string PartName => TenantContentFilesBackupParticipant.PartName;

            public string SchemaVersion => "1";

            public long Length => bytes.Length;

            public Stream OpenRead() => new MemoryStream(bytes, writable: false);
        }

        private sealed class Environment(string contentRoot, string webRoot) : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = webRoot;

            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

            public string ApplicationName { get; set; } = "Stomme.Tests";

            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

            public string ContentRootPath { get; set; } = contentRoot;

            public string EnvironmentName { get; set; } = "Test";
        }
    }
}
