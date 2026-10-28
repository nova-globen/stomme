using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-100 F1 (D109 items 1, 10, 14, 15): ONE file-ingestion guard over every <c>FileController</c> path that brings
    /// bytes into a folder - the form/chunked upload, the unzip, and the upload-by-URL - driven over real HTTP through
    /// the composed host on SQLite (the <see cref="GateDrive"/> recipe), logged in as host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal asserts the same three things: the answer is a distinguishable 400 naming the refusal, the
    /// target folder holds ZERO new files (on disk AND as <c>File</c> rows), and the staging tree holds nothing -
    /// no half-written file, no chunk part, no ingest directory.
    /// </para>
    /// <para>
    /// The host is composed with small caps (<see cref="FileIngestionHostFixture"/>): 256 KiB per file, 5 zip entries,
    /// 600 KiB decompressed per archive, ratio 20. A bomb here is a few hundred KiB; the mechanism is the same at 100 MB.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class FileIngestionGuardTests : IClassFixture<FileIngestionHostFixture>
    {
        private readonly FileIngestionHostFixture _fixture;
        private readonly ITestOutputHelper _output;

        public FileIngestionGuardTests(FileIngestionHostFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public async Task An_over_count_zip_is_refused_with_zero_files_in_the_folder_and_no_staging()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            var zip = FileIngestionDrive.Zip(Enumerable.Range(0, FileIngestionHostFixture.MaxEntries + 1)
                .Select(i => ($"entry{i}.txt", FileIngestionDrive.Text("entry " + i), CompressionLevel.Optimal)).ToArray());
            var zipFile = await UploadZipAsync(drive, folder, "count.zip", zip);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            await AssertRefusedAsync(drive, folder, status, body, "EntryCount", "count.zip");
        }

        [Fact]
        public async Task A_header_lying_zip_bomb_is_refused_while_decompressing()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();

            // One STORED entry holding 300 KiB of incompressible bytes whose headers claim 100 bytes. Its ratio is ~1,
            // so only COUNTING the bytes as they decompress can catch it; a check on entry.Length would pass it.
            var honest = FileIngestionDrive.Zip(("lie.txt", FileIngestionDrive.RandomBytes(300 * 1024, 7), CompressionLevel.NoCompression));
            var lying = FileIngestionDrive.LieAboutUncompressedSizes(honest, 100);
            using (var archive = new ZipArchive(new MemoryStream(lying), ZipArchiveMode.Read))
            {
                var entry = archive.Entries.Single();
                _output.WriteLine($"[crafted] entry.Length={entry.Length} entry.CompressedLength={entry.CompressedLength}");
                Assert.Equal(100, entry.Length);
                Assert.True(entry.Length < FileIngestionHostFixture.MaxUploadBytes, "the header must look harmless");
            }

            // Placed directly (it is larger than the upload cap): a zip that is already in the folder, e.g. from before the cap.
            var zipFile = await drive.PlaceFileAsync(folder, "lie.zip", lying);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            await AssertRefusedAsync(drive, folder, status, body, "Size", "lie.zip");
        }

        [Fact]
        public async Task An_over_total_zip_is_refused_while_decompressing()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            var zip = FileIngestionDrive.Zip(
                ("t1.txt", FileIngestionDrive.RandomBytes(250 * 1024, 11), CompressionLevel.NoCompression),
                ("t2.txt", FileIngestionDrive.RandomBytes(250 * 1024, 12), CompressionLevel.NoCompression),
                ("t3.txt", FileIngestionDrive.RandomBytes(250 * 1024, 13), CompressionLevel.NoCompression));
            var zipFile = await drive.PlaceFileAsync(folder, "total.zip", zip);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            await AssertRefusedAsync(drive, folder, status, body, "TotalSize", "total.zip");
        }

        [Fact]
        public async Task An_over_ratio_entry_is_refused()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            // 200 KiB of zeros deflates to a few hundred bytes: under the size cap, far over ratio 20.
            var zip = FileIngestionDrive.Zip(("zeros.txt", new byte[200 * 1024], CompressionLevel.Optimal));
            _output.WriteLine($"[crafted] zip bytes={zip.Length}");
            var zipFile = await UploadZipAsync(drive, folder, "ratio.zip", zip);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            await AssertRefusedAsync(drive, folder, status, body, "Ratio", "ratio.zip");
        }

        [Fact]
        public async Task An_over_size_upload_is_refused()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();

            var (status, body) = await drive.UploadAsync(folder, "big.txt", FileIngestionDrive.RandomBytes(300 * 1024, 3));

            await AssertRefusedAsync(drive, folder, status, body, "Size");
        }

        [Fact]
        public async Task A_chunk_set_whose_first_part_declares_too_much_is_refused()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();

            // Part 1 of 10 at 64 KiB: the nine full parts alone are 576 KiB, over the 256 KiB cap - refused now,
            // not after nine more requests have filled the disk.
            var (status, body) = await drive.UploadPartAsync(folder.FolderId.ToString(), "chunked.txt", FileIngestionDrive.RandomBytes(64 * 1024, 5), 1, 10);

            await AssertRefusedAsync(drive, folder, status, body, "Size");
        }

        [Fact]
        public async Task A_rejecting_scanner_blocks_an_upload()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            var callsBefore = _fixture.Scanner.Calls;

            var (status, body) = await drive.UploadAsync(folder, "eicar-upload.txt", FileIngestionDrive.Text("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR"));

            Assert.True(_fixture.Scanner.Calls > callsBefore, "the scanner hook was never invoked");
            await AssertRefusedAsync(drive, folder, status, body, "Threat");
        }

        [Fact]
        public async Task A_rejecting_scanner_blocks_a_single_zip_entry_and_nothing_lands()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            var zip = FileIngestionDrive.Zip(
                ("clean-a.txt", FileIngestionDrive.Text("clean a"), CompressionLevel.Optimal),
                ("eicar-entry.txt", FileIngestionDrive.Text("threat"), CompressionLevel.Optimal),
                ("clean-b.txt", FileIngestionDrive.Text("clean b"), CompressionLevel.Optimal));
            var zipFile = await UploadZipAsync(drive, folder, "scan.zip", zip);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            await AssertRefusedAsync(drive, folder, status, body, "Threat", "scan.zip");
        }

        [Fact]
        public async Task Upload_by_url_to_loopback_private_and_link_local_addresses_is_refused()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            await using var receiver = new LoopbackHttpReceiver(FileIngestionDrive.Text("internal secret"));

            var targets = new[]
            {
                $"http://127.0.0.1:{receiver.Port}/probe.txt",
                $"http://[::ffff:127.0.0.1]:{receiver.Port}/probe.txt",
                $"http://[::1]:{receiver.Port}/probe.txt",
                "http://192.168.10.20:8081/probe.txt",
                "http://10.0.0.1/probe.txt",
                "http://169.254.169.254/probe.txt",
                "http://0.0.0.0/probe.txt"
            };

            foreach (var target in targets)
            {
                var watch = Stopwatch.StartNew();
                var (status, body) = await drive.UploadByUrlAsync(folder, target, "probe.txt");
                _output.WriteLine($"[{target}] {(int)status} in {watch.ElapsedMilliseconds} ms");
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"{target} took {watch.Elapsed} - a denied address must be refused before any connect, not time out");
                await AssertRefusedAsync(drive, folder, status, body, "Destination");
            }

            var (schemeStatus, schemeBody) = await drive.UploadByUrlAsync(folder, "file:///C:/Windows/win.ini", "probe.txt");
            await AssertRefusedAsync(drive, folder, schemeStatus, schemeBody, "Destination");

            Assert.True(receiver.Accepted == 0, $"expected no connection, the receiver accepted {receiver.Accepted}: {receiver.Connections} (BL-231)");
        }

        [Fact]
        public async Task Upload_by_url_to_an_allowlisted_loopback_receiver_succeeds()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            var content = FileIngestionDrive.Text("hello from an allowlisted receiver");
            await using var receiver = new LoopbackHttpReceiver(content);

            // Through the CLIENT FileService - the call the Files admin's "Add from URL" makes.
            var service = await drive.ClientFileServiceAsync();
            var file = await service.UploadFileAsync($"http://localhost:{receiver.Port}/hello.txt", folder.FolderId, "hello.txt");

            Assert.NotNull(file);
            Assert.True(receiver.Accepted == 1, $"expected one connection, the receiver accepted {receiver.Accepted}: {receiver.Connections} (BL-231)");
            Assert.Equal(new[] { "hello.txt" }, await drive.DiskNamesAsync(folder));
            Assert.Equal(new[] { "hello.txt" }, await drive.RowNamesAsync(folder));
            Assert.Equal(content, await System.IO.File.ReadAllBytesAsync(Path.Combine(await drive.FolderPathAsync(folder), "hello.txt")));
            Assert.Empty(await drive.StagingLeftoversAsync());
        }

        [Fact]
        public async Task Upload_by_url_over_the_size_cap_is_refused()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            await using var receiver = new LoopbackHttpReceiver(FileIngestionDrive.RandomBytes(300 * 1024, 17));

            var (status, body) = await drive.UploadByUrlAsync(folder, $"http://localhost:{receiver.Port}/big.txt", "big.txt");

            await AssertRefusedAsync(drive, folder, status, body, "Size");
        }

        [Fact]
        public async Task A_normal_zip_extracts_and_a_normal_upload_succeeds()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();

            var (uploadStatus, _) = await drive.UploadAsync(folder, "note.txt", FileIngestionDrive.Text("a note"));
            Assert.True(uploadStatus is HttpStatusCode.NoContent or HttpStatusCode.OK, $"upload answered {(int)uploadStatus}");

            var first = FileIngestionDrive.RandomBytes(100 * 1024, 21);
            var second = FileIngestionDrive.Text("the tail");
            var (part1, _) = await drive.UploadPartAsync(folder.FolderId.ToString(), "two.txt", first, 1, 2);
            Assert.True(part1 is HttpStatusCode.NoContent or HttpStatusCode.OK, $"part 1 answered {(int)part1}");
            Assert.DoesNotContain("two.txt", await drive.DiskNamesAsync(folder));
            var (part2, _) = await drive.UploadPartAsync(folder.FolderId.ToString(), "two.txt", second, 2, 2);
            Assert.True(part2 is HttpStatusCode.NoContent or HttpStatusCode.OK, $"part 2 answered {(int)part2}");

            var zip = FileIngestionDrive.Zip(
                ("a.txt", FileIngestionDrive.Text("alpha"), CompressionLevel.Optimal),
                ("b.txt", FileIngestionDrive.Text("bravo"), CompressionLevel.Optimal));
            var zipFile = await UploadZipAsync(drive, folder, "normal.zip", zip);
            var (unzipStatus, _) = await drive.UnzipAsync(zipFile.FileId);
            Assert.True(unzipStatus is HttpStatusCode.NoContent or HttpStatusCode.OK, $"unzip answered {(int)unzipStatus}");

            var expected = new[] { "a.txt", "b.txt", "note.txt", "two.txt" };
            Assert.Equal(expected, await drive.DiskNamesAsync(folder));
            Assert.Equal(expected, await drive.RowNamesAsync(folder));
            var folderPath = await drive.FolderPathAsync(folder);
            Assert.Equal(first.Concat(second).ToArray(), await System.IO.File.ReadAllBytesAsync(Path.Combine(folderPath, "two.txt")));
            Assert.Equal("alpha", await System.IO.File.ReadAllTextAsync(Path.Combine(folderPath, "a.txt")));
            Assert.Empty(await drive.StagingLeftoversAsync());
        }

        [Fact]
        public async Task An_unzip_that_is_refused_does_not_destroy_a_pre_existing_same_named_file()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync(capacityMegabytes: 1);
            var original = FileIngestionDrive.Text("the user's original");
            await drive.PlaceFileAsync(folder, "keep.txt", original);
            await drive.PlaceFileAsync(folder, "filler.txt", FileIngestionDrive.Text("f"), declaredSize: 900_000);

            // keep.txt's 200 KiB replacement would take the 1 MB folder over capacity.
            var zip = FileIngestionDrive.Zip(("keep.txt", FileIngestionDrive.RandomBytes(200 * 1024, 31), CompressionLevel.NoCompression));
            var zipFile = await drive.PlaceFileAsync(folder, "replace.zip", zip, declaredSize: 10);

            var (status, body) = await drive.UnzipAsync(zipFile.FileId);

            _output.WriteLine($"[after] disk={string.Join(",", await drive.DiskNamesAsync(folder))} rows={string.Join(",", await drive.RowNamesAsync(folder))}");
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("\"Capacity\"", body);
            Assert.Equal(new[] { "filler.txt", "keep.txt", "replace.zip" }, await drive.DiskNamesAsync(folder));
            Assert.Equal(new[] { "filler.txt", "keep.txt", "replace.zip" }, await drive.RowNamesAsync(folder));
            Assert.Equal(original, await System.IO.File.ReadAllBytesAsync(Path.Combine(await drive.FolderPathAsync(folder), "keep.txt")));
            Assert.Empty(await drive.StagingLeftoversAsync());
        }

        [Fact]
        public void The_size_cap_stays_below_int_max_value()
        {
            Assert.True(FileIngestionOptions.DefaultMaxUploadBytes < int.MaxValue);
            Assert.True(FileIngestionOptions.FromConfiguration(Configuration()).MaxUploadBytes < int.MaxValue);
            Assert.Equal(int.MaxValue - 1L, FileIngestionOptions.FromConfiguration(Configuration(("Files:MaxUploadBytes", (int.MaxValue - 1L).ToString()))).MaxUploadBytes);

            var atMax = Assert.Throws<InvalidOperationException>(() => FileIngestionOptions.FromConfiguration(Configuration(("Files:MaxUploadBytes", int.MaxValue.ToString()))));
            Assert.Contains("Files:MaxUploadBytes", atMax.Message);
            Assert.Throws<InvalidOperationException>(() => FileIngestionOptions.FromConfiguration(Configuration(("Files:MaxUploadBytes", "5000000000"))));
            Assert.Throws<InvalidOperationException>(() => FileIngestionOptions.FromConfiguration(Configuration(("Files:MaxUploadBytes", "0"))));
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("127.255.0.9", true)]
        [InlineData("::1", true)]
        [InlineData("10.1.2.3", true)]
        [InlineData("172.16.0.1", true)]
        [InlineData("172.31.255.255", true)]
        [InlineData("192.168.10.20", true)]
        [InlineData("169.254.169.254", true)]
        [InlineData("0.0.0.0", true)]
        [InlineData("::", true)]
        [InlineData("fe80::1", true)]
        [InlineData("fd00::1", true)]
        [InlineData("224.0.0.1", true)]
        [InlineData("255.255.255.255", true)]
        [InlineData("ff02::1", true)]
        [InlineData("::ffff:127.0.0.1", true)]
        [InlineData("::ffff:192.168.10.20", true)]
        [InlineData("8.8.8.8", false)]
        [InlineData("172.15.0.1", false)]
        [InlineData("172.32.0.1", false)]
        [InlineData("104.16.0.1", false)]
        [InlineData("2606:4700::1111", false)]
        [InlineData("::ffff:8.8.8.8", false)]
        public void The_outbound_connect_guard_denies_exactly_the_internal_ranges(string address, bool denied)
        {
            Assert.Equal(denied, OutboundConnectGuard.IsDenied(IPAddress.Parse(address)));
        }

        // ---- helpers ----------------------------------------------------------------------------------------

        private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))).Build();

        private static async Task<Models.File> UploadZipAsync(FileIngestionDrive drive, Models.Folder folder, string name, byte[] zip)
        {
            var (status, _) = await drive.UploadAsync(folder, name, zip);
            Assert.True(status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"uploading {name} answered {(int)status}");
            return (await drive.RowsAsync(folder)).Single(file => file.Name == name);
        }

        private async Task AssertRefusedAsync(FileIngestionDrive drive, Models.Folder folder, HttpStatusCode status, string body, string reason, params string[] alreadyThere)
        {
            var disk = await drive.DiskNamesAsync(folder);
            var rows = await drive.RowNamesAsync(folder);
            var staging = await drive.StagingLeftoversAsync();
            _output.WriteLine($"[refusal {reason}] status={(int)status} disk=[{string.Join(",", disk)}] rows=[{string.Join(",", rows)}] staging=[{string.Join(",", staging)}]");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains($"\"{reason}\"", body);
            var expected = alreadyThere.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, disk);
            Assert.Equal(expected, rows);
            Assert.Empty(staging);
        }
    }
}