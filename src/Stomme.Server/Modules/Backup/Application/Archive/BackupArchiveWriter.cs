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
    /// Writes a <c>DCBAK1</c> archive FORWARD ONLY - the output needs not seek, and memory stays at one chunk however large a
    /// part is (D106: no ~2 GB whole-archive buffer, which the predecessor platform's protector had). Parts are written one at a time through
    /// <see cref="OpenPart"/>; <see cref="CompleteAsync"/> seals the manifest and the footer. An archive never completed has
    /// no footer and does not open.
    /// </summary>
    public sealed class BackupArchiveWriter : IAsyncDisposable, IDisposable
    {
        private readonly Stream _output;
        private readonly BackupChunkCipher _cipher;
        private readonly int _chunkSize;
        private readonly byte[] _plaintext;
        private readonly byte[] _frame;
        private readonly List<BackupManifestPart> _parts = new();
        private long _position;
        private long _counter;
        private PartStream? _open;
        private bool _completed;

        private BackupArchiveWriter(Stream output, BackupChunkCipher cipher, int chunkSize, long position)
        {
            _output = output;
            _cipher = cipher;
            _chunkSize = chunkSize;
            _plaintext = new byte[chunkSize];
            _frame = new byte[chunkSize + BackupArchiveFormat.FrameOverhead];
            _position = position;
        }

        public static Task<BackupArchiveWriter> CreateAsync(Stream output, string passphrase, CancellationToken cancellationToken = default) =>
            CreateAsync(output, passphrase, BackupArchiveFormat.DefaultChunkSize, cancellationToken);

        internal static async Task<BackupArchiveWriter> CreateAsync(Stream output, string passphrase, int chunkSize, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(output);
            BackupArchiveFormat.RequirePassphrase(passphrase);
            if (!output.CanWrite)
            {
                throw new ArgumentException("A backup archive is written to a writable stream.", nameof(output));
            }

            if (chunkSize < BackupArchiveFormat.MinimumChunkSize || chunkSize > BackupArchiveFormat.MaximumChunkSize || (chunkSize & (chunkSize - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            var salt = RandomNumberGenerator.GetBytes(BackupArchiveFormat.SaltLength);
            var noncePrefix = RandomNumberGenerator.GetBytes(BackupArchiveFormat.NoncePrefixLength);
            var header = BackupArchiveFormat.WriteHeader(BackupArchiveFormat.Iterations, salt, noncePrefix, chunkSize);
            var cipher = new BackupChunkCipher(BackupArchiveFormat.DeriveKey(passphrase, salt, BackupArchiveFormat.Iterations), header, noncePrefix);

            await output.WriteAsync(header, cancellationToken);
            return new BackupArchiveWriter(output, cipher, chunkSize, header.Length);
        }

        /// <summary>The parts written so far, as the manifest will record them.</summary>
        public IReadOnlyList<BackupManifestPart> Parts => _parts;

        /// <summary>
        /// Starts the next part. Write its bytes, then dispose the stream (async preferred) to seal its final chunk. One part is
        /// open at a time; a name is used once.
        /// </summary>
        public Stream OpenPart(string name, string schemaVersion, string moduleVersion)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A part has a name.", nameof(name));
            }

            if (_completed)
            {
                throw new InvalidOperationException("The archive is complete.");
            }

            if (_open is not null)
            {
                throw new InvalidOperationException($"Part '{_open.Part.Name}' is still open; dispose it before starting another.");
            }

            if (_parts.Any(part => string.Equals(part.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"The archive already has a part named '{name}'.");
            }

            var part = new BackupManifestPart
            {
                Index = _parts.Count, Name = name, SchemaVersion = schemaVersion ?? string.Empty, ModuleVersion = moduleVersion ?? string.Empty,
                Offset = _position, FirstCounter = _counter,
            };
            _open = new PartStream(this, part);
            return _open;
        }

        /// <summary>Seals the manifest after the last part and writes the footer that points at it.</summary>
        public async Task CompleteAsync(string frameworkVersion, IReadOnlyList<int> tenantIds, DateTime createdOnUtc, CancellationToken cancellationToken = default)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The archive is already complete.");
            }

            if (_open is not null)
            {
                throw new InvalidOperationException($"Part '{_open.Part.Name}' is still open; dispose it before completing the archive.");
            }

            var manifest = new BackupManifest
            {
                FormatVersion = BackupArchiveFormat.FormatVersion, FrameworkVersion = frameworkVersion,
                CreatedOnUtc = DateTime.SpecifyKind(createdOnUtc, DateTimeKind.Utc), TenantIds = tenantIds.ToList(), Parts = _parts.ToList(),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupArchiveReader.ManifestJson);
            if (json.Length > BackupArchiveFormat.MaximumManifestLength)
            {
                throw new InvalidOperationException("The manifest is larger than an archive may carry.");
            }

            var offset = _position;
            var firstCounter = _counter;
            var chunkIndex = 0;
            var at = 0;
            do
            {
                var length = Math.Min(_chunkSize, json.Length - at);
                var final = at + length == json.Length;
                await WriteFrameAsync(BackupArchiveFormat.KindManifest, -1, chunkIndex++, final, json.AsMemory(at, length), cancellationToken);
                at += length;
            }
            while (at < json.Length);

            var footer = new byte[BackupArchiveFormat.FooterLength];
            BinaryPrimitives.WriteInt64LittleEndian(footer, offset);
            BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8), firstCounter);
            BackupArchiveFormat.FooterMagic.CopyTo(footer.AsSpan(16));
            await _output.WriteAsync(footer, cancellationToken);
            await _output.FlushAsync(cancellationToken);
            _position += footer.Length;
            _completed = true;
        }

        private int Seal(byte kind, int partIndex, int chunkIndex, bool final, ReadOnlySpan<byte> plaintext)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_frame, plaintext.Length);
            var ciphertext = _frame.AsSpan(4, plaintext.Length);
            var tag = _frame.AsSpan(4 + plaintext.Length, BackupArchiveFormat.TagLength);
            _cipher.Seal(_counter, kind, partIndex, chunkIndex, final, plaintext, ciphertext, tag);
            _counter++;
            var frameLength = plaintext.Length + BackupArchiveFormat.FrameOverhead;
            _position += frameLength;
            return frameLength;
        }

        private async ValueTask WriteFrameAsync(byte kind, int partIndex, int chunkIndex, bool final, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            var frameLength = Seal(kind, partIndex, chunkIndex, final, plaintext.Span);
            await _output.WriteAsync(_frame.AsMemory(0, frameLength), cancellationToken);
        }

        private void WriteFrame(byte kind, int partIndex, int chunkIndex, bool final, ReadOnlySpan<byte> plaintext)
        {
            var frameLength = Seal(kind, partIndex, chunkIndex, final, plaintext);
            _output.Write(_frame, 0, frameLength);
        }

        private void Closed(PartStream part)
        {
            _parts.Add(part.Part);
            _open = null;
        }

        public void Dispose() => _cipher.Dispose();

        public ValueTask DisposeAsync()
        {
            _cipher.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// A part being written. A full chunk is held back until more bytes arrive (then it is sealed as NOT final) or the part
        /// is closed (then it is sealed as final) - so the final flag is always on the last chunk, and an empty part is one
        /// empty final chunk.
        /// </summary>
        private sealed class PartStream : Stream
        {
            private readonly BackupArchiveWriter _writer;
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private int _filled;
            private int _chunkIndex;
            private long _length;
            private bool _closed;

            public PartStream(BackupArchiveWriter writer, BackupManifestPart part)
            {
                _writer = writer;
                Part = part;
            }

            public BackupManifestPart Part { get; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => !_closed;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => _length; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                ThrowIfClosed();
                while (!buffer.IsEmpty)
                {
                    if (_filled == _writer._chunkSize)
                    {
                        _writer.WriteFrame(BackupArchiveFormat.KindPart, Part.Index, _chunkIndex++, final: false, _writer._plaintext);
                        _filled = 0;
                    }

                    buffer = Take(buffer);
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ThrowIfClosed();
                while (!buffer.IsEmpty)
                {
                    if (_filled == _writer._chunkSize)
                    {
                        await _writer.WriteFrameAsync(BackupArchiveFormat.KindPart, Part.Index, _chunkIndex++, final: false, _writer._plaintext, cancellationToken);
                        _filled = 0;
                    }

                    var taken = Math.Min(buffer.Length, _writer._chunkSize - _filled);
                    Take(buffer.Span[..taken]);
                    buffer = buffer[taken..];
                }
            }

            private ReadOnlySpan<byte> Take(ReadOnlySpan<byte> buffer)
            {
                var taken = Math.Min(buffer.Length, _writer._chunkSize - _filled);
                var slice = buffer[..taken];
                slice.CopyTo(_writer._plaintext.AsSpan(_filled));
                _hash.AppendData(slice);
                _filled += taken;
                _length += taken;
                return buffer[taken..];
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_closed)
                {
                    _writer.WriteFrame(BackupArchiveFormat.KindPart, Part.Index, _chunkIndex++, final: true, _writer._plaintext.AsSpan(0, _filled));
                    Close(disposing);
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (!_closed)
                {
                    await _writer.WriteFrameAsync(BackupArchiveFormat.KindPart, Part.Index, _chunkIndex++, final: true, _writer._plaintext.AsMemory(0, _filled), CancellationToken.None);
                    Close(true);
                }

                await base.DisposeAsync();
            }

            private void Close(bool disposing)
            {
                _closed = true;
                Part.Length = _length;
                Part.ChunkCount = _chunkIndex;
                Part.Sha256 = Convert.ToHexString(_hash.GetHashAndReset());
                _hash.Dispose();
                _writer.Closed(this);
            }

            private void ThrowIfClosed()
            {
                if (_closed)
                {
                    throw new ObjectDisposedException(nameof(PartStream), $"Part '{Part.Name}' is sealed.");
                }
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
