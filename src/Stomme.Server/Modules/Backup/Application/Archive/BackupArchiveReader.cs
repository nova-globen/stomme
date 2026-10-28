#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.Backup.Application.Archive
{
    /// <summary>
    /// Reads a <c>DCBAK1</c> archive (BL-81, D106 item 4). Opening it bounds the header, derives the key and verifies the
    /// manifest - nothing in the manifest is believed before GCM has checked it. A part is then read FORWARD, one chunk at a
    /// time; every chunk is verified before a byte of it is returned, and the end of a part checks its length and SHA-256.
    /// <see cref="VerifyAsync"/> is the restore pre-flight's pass over every chunk. The archive stream must be seekable;
    /// parts share it, so one reader is used by one caller at a time.
    /// </summary>
    public sealed class BackupArchiveReader : IDisposable
    {
        internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

        private const string Altered = "The backup archive could not be opened: the passphrase is wrong, or the archive was altered.";

        private readonly Stream _archive;
        private readonly BackupChunkCipher _cipher;
        private readonly int _chunkSize;
        private readonly long _manifestOffset;

        private BackupArchiveReader(Stream archive, BackupChunkCipher cipher, int chunkSize, long manifestOffset, BackupManifest manifest)
        {
            _archive = archive;
            _cipher = cipher;
            _chunkSize = chunkSize;
            _manifestOffset = manifestOffset;
            Manifest = manifest;
        }

        public BackupManifest Manifest { get; }

        public static Task<BackupArchiveReader> OpenAsync(Stream archive, string passphrase, CancellationToken cancellationToken = default)
        {
            BackupArchiveFormat.RequirePassphrase(passphrase);
            return OpenCoreAsync(archive, (salt, iterations) => BackupArchiveFormat.DeriveKey(passphrase, salt, iterations), cancellationToken);
        }

        /// <summary>
        /// Opens an archive with its already-derived KEY (BL-81, D106 item 7): a staged restore is applied after a restart,
        /// and the operator's passphrase is never written down - the per-archive key derived from it is, for that window.
        /// </summary>
        internal static Task<BackupArchiveReader> OpenWithKeyAsync(Stream archive, byte[] key, CancellationToken cancellationToken = default)
        {
            if (key is not { Length: BackupArchiveFormat.KeyLength })
            {
                throw new ArgumentException("An archive key is 32 bytes.", nameof(key));
            }

            return OpenCoreAsync(archive, (_, _) => (byte[])key.Clone(), cancellationToken);
        }

        /// <summary>The key a passphrase derives for THIS archive (its salt and cost) - checked by opening the manifest.</summary>
        internal static async Task<byte[]> DeriveKeyAsync(Stream archive, string passphrase, CancellationToken cancellationToken = default)
        {
            byte[]? derived = null;
            using (await OpenCoreAsync(archive, (salt, iterations) =>
            {
                derived = BackupArchiveFormat.DeriveKey(passphrase, salt, iterations);
                return (byte[])derived.Clone();
            }, cancellationToken))
            {
            }

            return derived!;
        }

        private static async Task<BackupArchiveReader> OpenCoreAsync(Stream archive, Func<byte[], int, byte[]> keyFor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(archive);
            if (!archive.CanRead || !archive.CanSeek)
            {
                throw new ArgumentException("A backup archive is read from a readable, seekable stream.", nameof(archive));
            }

            var length = archive.Length;
            if (length < BackupArchiveFormat.HeaderLength + BackupArchiveFormat.FooterLength)
            {
                throw new BackupArchiveException("This is not a Nova Globen Stomme backup archive.");
            }

            var header = new byte[BackupArchiveFormat.HeaderLength];
            archive.Position = 0;
            await archive.ReadExactlyAsync(header, cancellationToken);
            var (iterations, salt, noncePrefix, chunkSize) = BackupArchiveFormat.ReadHeader(header);

            var footer = new byte[BackupArchiveFormat.FooterLength];
            archive.Position = length - BackupArchiveFormat.FooterLength;
            await archive.ReadExactlyAsync(footer, cancellationToken);
            if (!footer.AsSpan(16).SequenceEqual(BackupArchiveFormat.FooterMagic))
            {
                throw new BackupArchiveException("The backup archive is incomplete: it has no footer (a truncated file, or a snapshot that never finished).");
            }

            var manifestOffset = BinaryPrimitives.ReadInt64LittleEndian(footer);
            var manifestCounter = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(8));
            if (manifestOffset < BackupArchiveFormat.HeaderLength || manifestOffset >= length - BackupArchiveFormat.FooterLength || manifestCounter < 0)
            {
                throw new BackupArchiveException(Altered);
            }

            var cipher = new BackupChunkCipher(keyFor(salt, iterations), header, noncePrefix);
            try
            {
                var manifest = await ReadManifestAsync(archive, cipher, chunkSize, manifestOffset, manifestCounter, length - BackupArchiveFormat.FooterLength, cancellationToken);
                Validate(manifest, manifestOffset, manifestCounter);
                return new BackupArchiveReader(archive, cipher, chunkSize, manifestOffset, manifest);
            }
            catch
            {
                cipher.Dispose();
                throw;
            }
        }

        /// <summary>The part named <paramref name="name"/>, read from its start.</summary>
        public Stream OpenPart(string name)
        {
            var part = Manifest.Parts.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                ?? throw new BackupArchiveException($"The backup archive has no part named '{name}'.");
            return OpenPart(part);
        }

        public Stream OpenPart(BackupManifestPart part)
        {
            ArgumentNullException.ThrowIfNull(part);
            if (part.Index < 0 || part.Index >= Manifest.Parts.Count || !ReferenceEquals(Manifest.Parts[part.Index], part))
            {
                throw new ArgumentException("The part is not one of this archive's.", nameof(part));
            }

            return new PartStream(this, part);
        }

        /// <summary>Reads every chunk of every part: GCM, order, finality, length and hash. Writes nothing; the pre-flight's first check.</summary>
        public async Task VerifyAsync(CancellationToken cancellationToken = default)
        {
            var sink = new byte[_chunkSize];
            foreach (var part in Manifest.Parts)
            {
                await using var stream = OpenPart(part);
                while (await stream.ReadAsync(sink, cancellationToken) > 0)
                {
                }
            }
        }

        /// <summary>
        /// Whether a host of <paramref name="frameworkVersion"/> may restore this archive, refused here before any participant
        /// is asked anything (D106, corrected by BL-170). The same version, or an OLDER one, provided no release in between
        /// has a data upgrade step (<paramref name="upgradeStepVersions"/>: the step would never run again over the restored
        /// rows); whether each part's data reads unchanged is the parts' own shapes, compared next. A NEWER archive is refused,
        /// and so is a version either side cannot read as a release number.
        /// </summary>
        public void RequireRestorableOn(string frameworkVersion, IEnumerable<string> upgradeStepVersions)
        {
            if (string.Equals(Manifest.FrameworkVersion, frameworkVersion, StringComparison.Ordinal))
            {
                return;
            }

            if (!Version.TryParse(Manifest.FrameworkVersion, out var taken) || !Version.TryParse(frameworkVersion, out var host))
            {
                throw new BackupArchiveException(
                    $"This backup was taken on Nova Globen Stomme {Manifest.FrameworkVersion}; this host runs {frameworkVersion}. A restore needs the same version.");
            }

            if (taken > host)
            {
                throw new BackupArchiveException(
                    $"This backup was taken on Nova Globen Stomme {Manifest.FrameworkVersion}, which is newer than this host's {frameworkVersion}. Restore it on {Manifest.FrameworkVersion} or later.");
            }

            var step = upgradeStepVersions
                .Select(version => Version.TryParse(version, out var parsed) ? parsed : null)
                .Where(version => version is not null && version > taken && version <= host)
                .OrderBy(version => version)
                .FirstOrDefault();
            if (step is not null)
            {
                throw new BackupArchiveException(
                    $"This backup was taken on Nova Globen Stomme {Manifest.FrameworkVersion}; this host runs {frameworkVersion}, and the upgrade to {step} changed data after it was taken. Restore it on {Manifest.FrameworkVersion}.");
            }
        }

        public void Dispose() => _cipher.Dispose();

        private static async Task<BackupManifest> ReadManifestAsync(Stream archive, BackupChunkCipher cipher, int chunkSize, long offset, long counter, long end, CancellationToken cancellationToken)
        {
            var frame = new byte[chunkSize + BackupArchiveFormat.FrameOverhead];
            using var json = new MemoryStream();
            var chunkIndex = 0;
            var position = offset;
            var plaintext = new byte[chunkSize];
            while (true)
            {
                if (position + BackupArchiveFormat.FrameOverhead > end)
                {
                    throw new BackupArchiveException(Altered);
                }

                archive.Position = position;
                await archive.ReadExactlyAsync(frame.AsMemory(0, 4), cancellationToken);
                var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
                if (length < 0 || length > chunkSize || position + length + BackupArchiveFormat.FrameOverhead > end
                    || json.Length + length > BackupArchiveFormat.MaximumManifestLength)
                {
                    throw new BackupArchiveException(Altered);
                }

                await archive.ReadExactlyAsync(frame.AsMemory(4, length + BackupArchiveFormat.TagLength), cancellationToken);
                position += length + BackupArchiveFormat.FrameOverhead;

                // The manifest's last chunk is the last frame before the footer; any other chunk is not final.
                var final = position == end;
                try
                {
                    cipher.Open(counter + chunkIndex, BackupArchiveFormat.KindManifest, -1, chunkIndex, final,
                        frame.AsSpan(4, length), frame.AsSpan(4 + length, BackupArchiveFormat.TagLength), plaintext.AsSpan(0, length));
                }
                catch (CryptographicException ex)
                {
                    throw new BackupArchiveException(Altered, ex);
                }

                json.Write(plaintext, 0, length);
                chunkIndex++;
                if (final)
                {
                    break;
                }
            }

            try
            {
                json.Position = 0;
                return JsonSerializer.Deserialize<BackupManifest>(json, ManifestJson) ?? throw new BackupArchiveException(Altered);
            }
            catch (JsonException ex)
            {
                throw new BackupArchiveException(Altered, ex);
            }
        }

        /// <summary>The manifest is authentic here; this refuses one this writer could not have produced.</summary>
        private static void Validate(BackupManifest manifest, long manifestOffset, long manifestCounter)
        {
            if (manifest.FormatVersion != BackupArchiveFormat.FormatVersion || string.IsNullOrWhiteSpace(manifest.FrameworkVersion))
            {
                throw new BackupArchiveException("The backup archive's manifest is not one this host reads.");
            }

            long counter = 0;
            long offset = BackupArchiveFormat.HeaderLength;
            for (var index = 0; index < manifest.Parts.Count; index++)
            {
                var part = manifest.Parts[index];
                if (part.Index != index || string.IsNullOrWhiteSpace(part.Name) || part.Length < 0 || part.ChunkCount < 1
                    || part.FirstCounter != counter || part.Offset != offset)
                {
                    throw new BackupArchiveException("The backup archive's manifest is inconsistent.");
                }

                counter += part.ChunkCount;
                offset = part.Offset + part.Length + ((long)part.ChunkCount * BackupArchiveFormat.FrameOverhead);
            }

            if (counter != manifestCounter || offset != manifestOffset
                || manifest.Parts.Select(part => part.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Parts.Count)
            {
                throw new BackupArchiveException("The backup archive's manifest is inconsistent.");
            }
        }

        /// <summary>One part, decrypted chunk by chunk from where the manifest says it starts.</summary>
        private sealed class PartStream : Stream
        {
            private readonly BackupArchiveReader _reader;
            private readonly BackupManifestPart _part;
            private readonly byte[] _frame;
            private readonly byte[] _plaintext;
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private long _position;
            private int _chunkIndex;
            private int _available;
            private int _consumed;
            private long _delivered;
            private bool _ended;

            public PartStream(BackupArchiveReader reader, BackupManifestPart part)
            {
                _reader = reader;
                _part = part;
                _frame = new byte[reader._chunkSize + BackupArchiveFormat.FrameOverhead];
                _plaintext = new byte[reader._chunkSize];
                _position = part.Offset;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _part.Length;

            public override long Position { get => _delivered; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                while (_consumed == _available)
                {
                    if (_ended)
                    {
                        return 0;
                    }

                    SeekFrame();
                    _reader._archive.ReadExactly(_frame, 0, 4);
                    var length = FrameLength();
                    _reader._archive.ReadExactly(_frame, 4, length + BackupArchiveFormat.TagLength);
                    Decrypt(length);
                }

                return Deliver(buffer);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                while (_consumed == _available)
                {
                    if (_ended)
                    {
                        return 0;
                    }

                    SeekFrame();
                    await _reader._archive.ReadExactlyAsync(_frame.AsMemory(0, 4), cancellationToken);
                    var length = FrameLength();
                    await _reader._archive.ReadExactlyAsync(_frame.AsMemory(4, length + BackupArchiveFormat.TagLength), cancellationToken);
                    Decrypt(length);
                }

                return Deliver(buffer.Span);
            }

            private void SeekFrame()
            {
                if (_chunkIndex >= _part.ChunkCount || _position + BackupArchiveFormat.FrameOverhead > _reader._manifestOffset)
                {
                    throw Fail("it ends before its last chunk");
                }

                _reader._archive.Position = _position;
            }

            private int FrameLength()
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(_frame);
                if (length < 0 || length > _reader._chunkSize || _position + length + BackupArchiveFormat.FrameOverhead > _reader._manifestOffset)
                {
                    throw Fail("a chunk's length is not possible");
                }

                return length;
            }

            private void Decrypt(int length)
            {
                var final = _chunkIndex == _part.ChunkCount - 1;
                try
                {
                    _reader._cipher.Open(_part.FirstCounter + _chunkIndex, BackupArchiveFormat.KindPart, _part.Index, _chunkIndex, final,
                        _frame.AsSpan(4, length), _frame.AsSpan(4 + length, BackupArchiveFormat.TagLength), _plaintext.AsSpan(0, length));
                }
                catch (CryptographicException ex)
                {
                    throw new BackupArchiveException($"Part '{_part.Name}' of the backup archive was altered: chunk {_chunkIndex} does not verify.", ex);
                }

                _position += length + BackupArchiveFormat.FrameOverhead;
                _chunkIndex++;
                _available = length;
                _consumed = 0;
                if (_delivered + length > _part.Length)
                {
                    throw Fail("it is longer than the manifest says");
                }

                _hash.AppendData(_plaintext, 0, length);
                if (final)
                {
                    _ended = true;
                    var hash = Convert.ToHexString(_hash.GetHashAndReset());
                    if (_delivered + length != _part.Length || !string.Equals(hash, _part.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw Fail("its length or hash does not match the manifest");
                    }
                }
            }

            private int Deliver(Span<byte> buffer)
            {
                var count = Math.Min(buffer.Length, _available - _consumed);
                _plaintext.AsSpan(_consumed, count).CopyTo(buffer);
                _consumed += count;
                _delivered += count;
                return count;
            }

            private BackupArchiveException Fail(string why) => new($"Part '{_part.Name}' of the backup archive is damaged: {why}.");

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _hash.Dispose();
                }

                base.Dispose(disposing);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
