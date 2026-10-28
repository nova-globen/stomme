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
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Modules.Pipelines.Persistence;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-233 - the site's pipeline folders in a backup: a restore puts every site's folder back exactly (a file added later is
    /// gone, a changed one reverted, a deleted one back), the upload staging is never carried; a tenant-scoped restore replaces
    /// the restored tenants' site folders only; a path that escapes, one outside a site's folder, a repeated path and a
    /// truncated part are refused - and a refused replace leaves the live folders untouched, with nothing left behind.
    /// </summary>
    public sealed class PipelineFilesBackupParticipantTests : IDisposable
    {
        private static readonly BackupScope Full = new("10.34.0", new[] { 1, 2 });

        private static readonly BackupScope OnlyTenantOne = new("10.34.0", new[] { 1 }, new[] { 1, 2 });

        private readonly string _base = Path.Combine(Path.GetTempPath(), "stomme-pipeline-files-backup-" + Guid.NewGuid().ToString("N"));
        private readonly PipelineFilesBackupParticipant _participant;

        public PipelineFilesBackupParticipantTests()
        {
            var options = new PipelineStepOptions();
            options.Files.Root = Root;
            _participant = new PipelineFilesBackupParticipant(Options.Create(options));
        }

        private string Root => Path.Combine(_base, "Data", "Pipelines", "Files");

        [Fact]
        public async Task A_restore_puts_every_site_folder_back_exactly_and_never_the_upload_staging()
        {
            Write("1-1/reports/q3.csv", "customer,total\nAda,42\n");
            Write("1-1/webhooks/incoming.txt", "{\"card\":\"4111\"}");
            Write("2-1/in/data.bin", "\u0000\u0001binary");
            Write("default/legacy.txt", "the engine's default tenant");
            Write(".incoming/part-in-flight.part", "half an upload");
            var before = Tree();
            var part = await ExportAsync(Full);
            Assert.DoesNotContain(".incoming", Encoding.UTF8.GetString(part));

            Write("1-1/reports/q3.csv", "changed after the snapshot");
            Write("1-2/late.txt", "a site's folder made after the snapshot");
            File.Delete(Path.Combine(Root, "2-1", "in", "data.bin"));

            Assert.True((await _participant.ValidateAsync(new BytesPart(part), Full, CancellationToken.None)).IsValid);
            await _participant.ReplaceAsync(new BytesPart(part), Full, CancellationToken.None);

            Assert.Equal(before.Replace(".incoming/part-in-flight.part=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("half an upload")) + "\n", ""), Tree());
            Assert.Empty(Leftovers());
        }

        [Fact]
        public async Task A_tenant_scoped_restore_replaces_that_tenants_site_folders_only()
        {
            Write("1-1/a.txt", "tenant one, site one");
            Write("2-1/b.txt", "tenant two");
            Write("default/c.txt", "the installation's");
            var part = await ExportAsync(Full);

            Write("1-1/a.txt", "changed");
            Write("1-3/new.txt", "tenant one, a site made since");
            Write("2-1/b.txt", "tenant two changed");
            Write("default/c.txt", "the installation's changed");

            await _participant.ReplaceAsync(new BytesPart(part), OnlyTenantOne, CancellationToken.None);

            Assert.Equal("tenant one, site one", Read("1-1/a.txt"));
            Assert.False(Directory.Exists(Path.Combine(Root, "1-3"))); // tenant one's, not in the snapshot
            Assert.Equal("tenant two changed", Read("2-1/b.txt"));
            Assert.Equal("the installation's changed", Read("default/c.txt"));
            Assert.Empty(Leftovers());

            // And a tenant-scoped export carries that tenant's site folders only.
            Assert.Equal(new[] { "1-1/a.txt" }, Paths(await ExportAsync(OnlyTenantOne)));
        }

        [Fact]
        public async Task An_empty_snapshot_empties_the_folders()
        {
            var part = await ExportAsync(Full); // nothing there yet: no folder at all
            Assert.Empty(part);
            Write("1-1/late.txt", "added after the snapshot");
            await _participant.ReplaceAsync(new BytesPart(part), Full, CancellationToken.None);
            Assert.Equal("", Tree());
            Assert.Empty(Leftovers());
        }

        [Theory]
        [InlineData(1, "../escape.txt")]
        [InlineData(1, "1-1/../../escape.txt")]
        [InlineData(1, "/rooted.txt")]
        [InlineData(1, "C:/drive.txt")]
        [InlineData(1, "1-1\\back\\slash.txt")]
        [InlineData(1, "1-1//empty.txt")]
        [InlineData(1, "loose-at-the-root.txt")]
        [InlineData(1, ".incoming/smuggled.part")]
        [InlineData(2, "1-1/a.txt")]
        public async Task A_path_that_could_escape_or_is_not_a_sites_file_is_refused_and_nothing_changes(int root, string path)
        {
            Write("1-1/keep.txt", "live");
            var before = Tree();
            var part = Record((byte)root, path, "evil");

            Assert.False((await _participant.ValidateAsync(new BytesPart(part), Full, CancellationToken.None)).IsValid);
            await Assert.ThrowsAsync<InvalidDataException>(() => _participant.ReplaceAsync(new BytesPart(part), Full, CancellationToken.None));

            Assert.Equal(before, Tree());
            Assert.False(File.Exists(Path.Combine(_base, "Data", "Pipelines", "escape.txt")));
            Assert.Empty(Leftovers());
        }

        [Fact]
        public async Task A_repeated_path_or_a_truncated_part_is_refused_and_nothing_changes()
        {
            Write("1-1/keep.txt", "live");
            var before = Tree();

            var repeated = Record(1, "1-1/a.txt", "one").Concat(Record(1, "1-1/a.txt", "two")).ToArray();
            Assert.Contains((await _participant.ValidateAsync(new BytesPart(repeated), Full, CancellationToken.None)).Problems, p => p.Contains("twice", StringComparison.Ordinal));

            var truncated = Record(1, "1-1/a.txt", "a file cut short")[..^4];
            Assert.False((await _participant.ValidateAsync(new BytesPart(truncated), Full, CancellationToken.None)).IsValid);
            await Assert.ThrowsAsync<InvalidDataException>(() => _participant.ReplaceAsync(new BytesPart(truncated), Full, CancellationToken.None));

            Assert.Equal(before, Tree());
            Assert.Empty(Leftovers());
        }

        [Theory]
        [InlineData("1-2", true, 1)]
        [InlineData("12-7", true, 12)]
        [InlineData("default", false, 0)]
        [InlineData("1-x", false, 0)]
        [InlineData("-1", false, 0)]
        public void A_site_folder_names_its_tenant(string folder, bool named, int tenant)
        {
            Assert.Equal((named, tenant), (PipelineFileSandbox.TenantOfSiteFolder(folder, out var tenantId), tenantId));
            Assert.Equal("1-2", PipelineFileSandbox.SiteFolder("1/2")); // the folder a site's file steps use
        }

        private async Task<byte[]> ExportAsync(BackupScope scope)
        {
            using var buffer = new MemoryStream();
            await _participant.ExportAsync(new ArchivePartWriter(_participant.Name, buffer), scope, CancellationToken.None);
            return buffer.ToArray();
        }

        private static IReadOnlyList<string> Paths(byte[] part)
        {
            var paths = new List<string>();
            var at = 0;
            while (at < part.Length)
            {
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(part.AsSpan(at + 1));
                paths.Add(Encoding.UTF8.GetString(part, at + 5, nameLength));
                at += 5 + nameLength + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(part.AsSpan(at + 5 + nameLength));
            }

            return paths;
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

        private void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>The folder as "relative path = content" lines, sorted, each ending in a line break - empty when there is none.</summary>
        private string Tree() =>
            !Directory.Exists(Root) ? string.Empty : string.Concat(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/') + "=" + Convert.ToBase64String(File.ReadAllBytes(file)) + "\n")
                .OrderBy(line => line, StringComparer.Ordinal));

        /// <summary>Any staging or set-aside folder a replace left behind, beside the root or inside it.</summary>
        private IEnumerable<string> Leftovers() =>
            new[] { Path.GetDirectoryName(Root)!, Root }
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Where(folder => Path.GetFileName(folder).Contains(".restoring-", StringComparison.Ordinal) || Path.GetFileName(folder).Contains(".replaced-", StringComparison.Ordinal));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }

        private sealed class BytesPart(byte[] bytes) : IBackupPartReader
        {
            public string PartName => PipelineFilesBackupParticipant.PartName;

            public string SchemaVersion => "1";

            public long Length => bytes.Length;

            public Stream OpenRead() => new MemoryStream(bytes, writable: false);
        }
    }
}
