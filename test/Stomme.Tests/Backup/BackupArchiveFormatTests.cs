#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Application.Archive;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// The <c>DCBAK1</c> archive: parts and the manifest round-trip, and every way of
    /// altering the file is REFUSED - the wrong passphrase, a flipped header, manifest or chunk byte, a dropped final chunk,
    /// reordered chunks, a chunk transplanted between parts, a truncated file. A version mismatch is refused before a part is
    /// read. Small chunks (4 KiB) so a part spans several without megabytes of test data.
    /// </summary>
    public class BackupArchiveFormatTests
    {
        private const string Passphrase = "correct horse battery staple";
        private const int Chunk = 4096;

        [Fact]
        public async Task Parts_and_the_manifest_round_trip()
        {
            var alpha = Bytes(Chunk * 2 + 123, seed: 1);
            var archive = await WriteAsync(("alpha", alpha), ("empty", Array.Empty<byte>()), ("beta", Bytes(10, seed: 2)));

            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            Assert.Equal("10.2.3", reader.Manifest.FrameworkVersion);
            Assert.Equal(new[] { 1, 2 }, reader.Manifest.TenantIds);
            Assert.Equal(new[] { "alpha", "empty", "beta" }, reader.Manifest.Parts.Select(part => part.Name));
            Assert.Equal(new[] { 3, 1, 1 }, reader.Manifest.Parts.Select(part => part.ChunkCount));
            Assert.Equal(alpha.LongLength, reader.Manifest.Parts[0].Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(alpha)), reader.Manifest.Parts[0].Sha256);
            Assert.Equal("schema-alpha", reader.Manifest.Parts[0].SchemaVersion);
            Assert.Equal(DateTimeKind.Utc, reader.Manifest.CreatedOnUtc.Kind);

            Assert.Equal(alpha, await ReadAllAsync(reader.OpenPart("alpha")));
            Assert.Empty(await ReadAllAsync(reader.OpenPart("empty")));
            Assert.Equal(Bytes(10, seed: 2), await ReadAllAsync(reader.OpenPart("beta")));
            Assert.Equal(alpha, ReadAllSync(reader.OpenPart("alpha")));             // the synchronous path reads the same bytes
            await reader.VerifyAsync();
        }

        // Pack_emptyPartList_producesContainerThatUnpacksEmpty and Pack_leavesDestinationStreamOpen: an empty part list packs to a container that unpacks empty, and packing leaves the destination open.
        [Fact]
        public async Task An_archive_with_no_parts_opens_empty_and_the_output_stays_open()
        {
            using var output = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(output, Passphrase))
            {
                await writer.CompleteAsync("10.2.3", Array.Empty<int>(), DateTime.UtcNow);
            }

            Assert.True(output.CanWrite);
            output.Position = 0;
            using var reader = await BackupArchiveReader.OpenAsync(output, Passphrase);
            Assert.Empty(reader.Manifest.Parts);
            await reader.VerifyAsync();
        }

        [Fact]
        public async Task The_wrong_passphrase_is_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(100, seed: 3)));
            var refused = await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(archive), "not the passphrase"));
            Assert.Contains("passphrase is wrong", refused.Message, StringComparison.Ordinal);
        }

        // Magic, format version, kdf, iterations, salt, nonce prefix, chunk size.
        [Theory]
        [InlineData(0)]
        [InlineData(6)]
        [InlineData(8)]
        [InlineData(10)]
        [InlineData(20)]
        [InlineData(31)]
        [InlineData(35)]
        public async Task A_flipped_header_byte_is_refused(int offset)
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk + 1, seed: 4)));
            archive[offset] ^= 0x01;
            await AssertRefusedAsync(archive);
        }

        [Fact]
        public async Task A_flipped_manifest_byte_is_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk + 1, seed: 5)));
            archive[ManifestOffset(archive) + 4 + 7] ^= 0x01;                          // inside the manifest's ciphertext
            await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase));
        }

        [Fact]
        public async Task A_flipped_chunk_byte_is_refused_when_the_part_is_read()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk * 2, seed: 6)), ("beta", Bytes(50, seed: 7)));
            archive[BackupArchiveFormat.HeaderLength + 4 + Chunk + BackupArchiveFormat.TagLength + 4 + 9] ^= 0x01;   // alpha, chunk 1

            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            var refused = await Assert.ThrowsAsync<BackupArchiveException>(() => ReadAllAsync(reader.OpenPart("alpha")));
            Assert.Contains("chunk 1 does not verify", refused.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<BackupArchiveException>(() => reader.VerifyAsync());
            Assert.Equal(Bytes(50, seed: 7), await ReadAllAsync(reader.OpenPart("beta")));   // the untouched part still reads
        }

        [Fact]
        public async Task Reordered_chunks_are_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk * 3, seed: 8)));
            var frame = Chunk + BackupArchiveFormat.FrameOverhead;
            Swap(archive, BackupArchiveFormat.HeaderLength, BackupArchiveFormat.HeaderLength + frame, frame);        // chunks 0 and 1

            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            await Assert.ThrowsAsync<BackupArchiveException>(() => ReadAllAsync(reader.OpenPart("alpha")));
        }

        [Fact]
        public async Task A_chunk_swapped_between_parts_is_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk, seed: 9)), ("beta", Bytes(Chunk, seed: 10)));
            var frame = Chunk + BackupArchiveFormat.FrameOverhead;
            Swap(archive, BackupArchiveFormat.HeaderLength, BackupArchiveFormat.HeaderLength + frame, frame);

            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            await Assert.ThrowsAsync<BackupArchiveException>(() => ReadAllAsync(reader.OpenPart("alpha")));
            await Assert.ThrowsAsync<BackupArchiveException>(() => ReadAllAsync(reader.OpenPart("beta")));
        }

        // The attacker cuts a part's LAST chunk out and moves the (unauthenticated) footer to match: the manifest still
        // verifies, and the part must not read as a shorter, complete part.
        [Fact]
        public async Task A_dropped_final_chunk_is_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk * 2 + 10, seed: 11)));
            var frame = Chunk + BackupArchiveFormat.FrameOverhead;
            var finalStart = BackupArchiveFormat.HeaderLength + 2 * frame;
            var finalLength = 10 + BackupArchiveFormat.FrameOverhead;
            var cut = archive.Take(finalStart).Concat(archive.Skip(finalStart + finalLength)).ToArray();
            var footer = cut.Length - BackupArchiveFormat.FooterLength;
            BinaryPrimitives.WriteInt64LittleEndian(cut.AsSpan(footer), BinaryPrimitives.ReadInt64LittleEndian(cut.AsSpan(footer)) - finalLength);

            // The manifest's own recorded offsets no longer add up, so it is refused on open; either way nothing reads.
            await AssertRefusedAsync(cut);
        }

        [Fact]
        public async Task A_truncated_archive_is_refused()
        {
            var archive = await WriteAsync(("alpha", Bytes(Chunk * 2, seed: 12)));
            foreach (var keep in new[] { archive.Length - 1, archive.Length - BackupArchiveFormat.FooterLength, archive.Length / 2, 10 })
            {
                var refused = await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(archive[..keep]), Passphrase));
                Assert.False(string.IsNullOrWhiteSpace(refused.Message));
            }
        }

        [Fact]
        public async Task Something_that_is_not_an_archive_is_named_as_such()
        {
            var refused = await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(Bytes(500, seed: 13)), Passphrase));
            Assert.Equal("This is not a Nova Globen Stomme backup archive.", refused.Message);
        }

        // The same version or an older one, never a newer one, never across an upgrade step;
        // whether each part's rows read unchanged is the parts' shapes (BackupPartShapeTests).
        [Fact]
        public async Task A_newer_archive_or_one_across_an_upgrade_step_is_refused_before_a_part_is_read_and_an_older_one_is_not()
        {
            var archive = await WriteAsync(("alpha", Bytes(10, seed: 14)));        // taken on 10.2.3
            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);

            reader.RequireRestorableOn("10.2.3", Array.Empty<string>());
            reader.RequireRestorableOn("10.5.0", Array.Empty<string>());
            reader.RequireRestorableOn("10.5.0", new[] { "10.2.3", "10.6.0" });  // a step at or before the archive, or after the host

            var newer = Assert.Throws<BackupArchiveException>(() => reader.RequireRestorableOn("10.2.2", Array.Empty<string>()));
            Assert.Contains("10.2.3", newer.Message, StringComparison.Ordinal);
            Assert.Contains("newer", newer.Message, StringComparison.Ordinal);

            var stepped = Assert.Throws<BackupArchiveException>(() => reader.RequireRestorableOn("10.5.0", new[] { "10.4.0" }));
            Assert.Contains("10.4.0", stepped.Message, StringComparison.Ordinal);

            var unreadable = Assert.Throws<BackupArchiveException>(() => reader.RequireRestorableOn("10.5.0-preview.1", Array.Empty<string>()));
            Assert.Contains("same version", unreadable.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Each_archive_has_its_own_salt_and_nonces()
        {
            var content = Bytes(Chunk, seed: 15);
            var first = await WriteAsync(("alpha", content));
            var second = await WriteAsync(("alpha", content));
            Assert.NotEqual(first[14..34], second[14..34]);                                     // salt + nonce prefix
            Assert.NotEqual(first[BackupArchiveFormat.HeaderLength..(BackupArchiveFormat.HeaderLength + 64)],
                second[BackupArchiveFormat.HeaderLength..(BackupArchiveFormat.HeaderLength + 64)]);
            Assert.Equal(-1, first.AsSpan().IndexOf(content.AsSpan(0, 64)));                    // no plaintext in the file
        }

        // The footer is not authenticated and names the manifest's counter, so the one relocation the global counter does NOT
        // stop is a PART chunk read as the manifest. Part content can be chosen by whoever writes data into the site; here it
        // is a well-formed manifest that describes only the first part, consistent in every offset and counter. The kind and
        // the part index in the AAD are what refuse it.
        [Fact]
        public async Task A_part_chunk_cannot_be_passed_off_as_the_manifest()
        {
            var alpha = Bytes(Chunk + 7, seed: 18);
            BackupManifest describingAlphaOnly;
            using (var honest = await BackupArchiveReader.OpenAsync(new MemoryStream(await WriteAsync(("alpha", alpha))), Passphrase))
            {
                describingAlphaOnly = honest.Manifest;
            }

            var forgedManifest = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(describingAlphaOnly, BackupArchiveReader.ManifestJson);
            var archive = await WriteAsync(("alpha", alpha), ("planted", forgedManifest), ("tail", Bytes(20, seed: 19)));
            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            var planted = reader.Manifest.Parts.Single(part => part.Name == "planted");
            Assert.Equal(1, planted.ChunkCount);

            var end = planted.Offset + planted.Length + BackupArchiveFormat.FrameOverhead;
            var forged = archive.Take((int)end).Concat(new byte[BackupArchiveFormat.FooterLength]).ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(forged.AsSpan((int)end), planted.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(forged.AsSpan((int)end + 8), planted.FirstCounter);
            BackupArchiveFormat.FooterMagic.CopyTo(forged.AsSpan((int)end + 16));

            await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(forged), Passphrase));
        }

        // A reused nonce still VERIFIES, so no tamper test can see it: two chunks of identical content under one key must not
        // encrypt alike - the global counter at work.
        [Fact]
        public async Task Identical_chunks_encrypt_differently()
        {
            var block = Bytes(Chunk, seed: 16);
            var archive = await WriteAsync(("alpha", block.Concat(block).Concat(block).ToArray()));
            var frame = Chunk + BackupArchiveFormat.FrameOverhead;
            var first = archive.AsSpan(BackupArchiveFormat.HeaderLength + 4, Chunk).ToArray();
            var second = archive.AsSpan(BackupArchiveFormat.HeaderLength + frame + 4, Chunk).ToArray();
            Assert.NotEqual(first, second);
        }

        // The cost is bounded BEFORE a key is derived: a header asking for 2^32-1 rounds is refused at once.
        [Fact]
        public async Task A_header_asking_for_an_absurd_key_derivation_cost_is_refused_before_deriving()
        {
            var archive = await WriteAsync(("alpha", Bytes(10, seed: 17)));
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(10), uint.MaxValue);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var refused = await Assert.ThrowsAsync<BackupArchiveException>(() => BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase));
            Assert.Contains("key derivation cost", refused.Message, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"refusing took {watch.Elapsed}.");
        }

        [Fact]
        public async Task The_writer_refuses_what_would_make_an_ambiguous_archive()
        {
            using var output = new MemoryStream();
            await using var writer = await BackupArchiveWriter.CreateAsync(output, Passphrase, Chunk, default);
            var open = writer.OpenPart("alpha", "1", "1");
            Assert.Throws<InvalidOperationException>(() => writer.OpenPart("beta", "1", "1"));                   // one part at a time
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync("10.2.3", Array.Empty<int>(), DateTime.UtcNow));
            await open.DisposeAsync();
            Assert.Throws<InvalidOperationException>(() => writer.OpenPart("alpha", "1", "1"));                  // a name once
            await Assert.ThrowsAsync<ArgumentException>(() => BackupArchiveWriter.CreateAsync(new MemoryStream(), string.Empty));
        }

        private static async Task AssertRefusedAsync(byte[] archive)
        {
            try
            {
                using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
                await reader.VerifyAsync();
            }
            catch (BackupArchiveException)
            {
                return;
            }

            Assert.Fail("An altered archive opened and verified.");
        }

        internal static async Task<byte[]> WriteAsync(params (string Name, byte[] Content)[] parts)
        {
            using var output = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(output, Passphrase, Chunk, default))
            {
                foreach (var (name, content) in parts)
                {
                    await using var part = writer.OpenPart(name, "schema-" + name, "1.0.0");
                    // Uneven writes, so a chunk boundary falls inside a write.
                    for (var at = 0; at < content.Length; at += 1000)
                    {
                        await part.WriteAsync(content.AsMemory(at, Math.Min(1000, content.Length - at)));
                    }
                }

                await writer.CompleteAsync("10.2.3", new[] { 1, 2 }, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc));
            }

            return output.ToArray();
        }

        private static long ManifestOffset(byte[] archive) =>
            BinaryPrimitives.ReadInt64LittleEndian(archive.AsSpan(archive.Length - BackupArchiveFormat.FooterLength));

        private static void Swap(byte[] data, int first, int second, int length)
        {
            var temporary = data.AsSpan(first, length).ToArray();
            data.AsSpan(second, length).CopyTo(data.AsSpan(first, length));
            temporary.CopyTo(data.AsSpan(second, length));
        }

        private static byte[] Bytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream)
        {
            await using (stream)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                return copy.ToArray();
            }
        }

        private static byte[] ReadAllSync(Stream stream)
        {
            using (stream)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }
}
