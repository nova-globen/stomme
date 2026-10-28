#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>The artifact grew past <c>DataExport:MaxArtifactBytes</c>. Reported as a code, never as a crash.</summary>
    public sealed class ExportArtifactTooLargeException : Exception
    {
        public ExportArtifactTooLargeException(long limitBytes)
            : base($"The export exceeded the artifact size limit of {limitBytes} bytes.")
        {
            LimitBytes = limitBytes;
        }

        public long LimitBytes { get; }
    }

    /// <summary>The job was cancelled, or its lease was lost, while it was rendering.</summary>
    public sealed class ExportRenderAbandonedException : Exception
    {
        public ExportRenderAbandonedException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The renderer's destination: forward-only, counts what passes, and refuses the byte that crosses the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cap fires DURING the render, which is only true because the renderers stream.</b> It counts bytes as
    /// they are written, so an export that would be 10 GB fails after <c>MaxArtifactBytes</c> of disk, not after
    /// 10 GB of it - and not after 10 GB of memory, which is what a buffering renderer would cost before its
    /// first byte got here (measured for XLSX: see the Server README).
    /// </para>
    /// <para>
    /// Deliberately not seekable and not readable. A renderer that needs either is one that cannot stream, and
    /// it should fail in a test against this type rather than in production against a file.
    /// </para>
    /// </remarks>
    public sealed class CappedCountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limitBytes;

        public CappedCountingStream(Stream inner, long limitBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (limitBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limitBytes), "An artifact size limit is positive.");
            }

            _limitBytes = limitBytes;
        }

        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Admit(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Admit(buffer.Length);
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Admit(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Admit(buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            Admit(1);
            _inner.WriteByte(value);
        }

        // Counted BEFORE the write: the byte that would cross the limit never reaches the disk.
        private void Admit(int count)
        {
            if (BytesWritten + count > _limitBytes)
            {
                throw new ExportArtifactTooLargeException(_limitBytes);
            }

            BytesWritten += count;
        }
    }

    /// <summary>
    /// Wraps a source's document so the render reports back between tables and every N rows (D108 refutation 9).
    /// </summary>
    /// <remarks>
    /// The renderers know nothing of jobs or leases, and should not. The pulse is injected where the rows are
    /// pulled instead: the callback renews the lease and re-reads the job's status, and THROWS to stop a render
    /// whose job was cancelled or whose lease was taken over - so a long export is neither reclaimed while
    /// healthy nor left writing after it was abandoned.
    /// </remarks>
    public sealed class PulsingDocument : ITabularDocument
    {
        private readonly ITabularDocument _inner;
        private readonly int _everyRows;
        private readonly Func<CancellationToken, Task> _pulse;

        public PulsingDocument(ITabularDocument inner, int everyRows, Func<CancellationToken, Task> pulse)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pulse = pulse ?? throw new ArgumentNullException(nameof(pulse));
            if (everyRows <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(everyRows), "A heartbeat interval is a positive row count.");
            }

            _everyRows = everyRows;
        }

        public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            var first = true;
            await foreach (var table in _inner.ReadTablesAsync(ct).WithCancellation(ct))
            {
                if (!first)
                {
                    await _pulse(ct);
                }

                first = false;
                yield return new PulsingTable(table, _everyRows, _pulse);
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private sealed class PulsingTable : ITabularTable
        {
            private readonly ITabularTable _inner;
            private readonly int _everyRows;
            private readonly Func<CancellationToken, Task> _pulse;

            public PulsingTable(ITabularTable inner, int everyRows, Func<CancellationToken, Task> pulse)
            {
                _inner = inner;
                _everyRows = everyRows;
                _pulse = pulse;
            }

            public string Name => _inner.Name;

            public IReadOnlyList<ExportColumn> Columns => _inner.Columns;

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                var sincePulse = 0;
                await foreach (var row in _inner.ReadRowsAsync(ct).WithCancellation(ct))
                {
                    yield return row;

                    if (++sincePulse >= _everyRows)
                    {
                        sincePulse = 0;
                        await _pulse(ct);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every registered <see cref="IExportSource"/>, by canonical key.
    /// </summary>
    /// <remarks>
    /// A source with an empty permission, a non-canonical key or an undeclared cardinality is REFUSED at
    /// construction, and so is a second source claiming a key: first-wins would let a later module answer for a
    /// key whose permission an administrator granted to a different source.
    /// </remarks>
    public sealed class ExportSourceRegistry
    {
        private readonly Dictionary<string, IExportSource> _sources = new(StringComparer.Ordinal);

        public ExportSourceRegistry(IEnumerable<IExportSource> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);

            foreach (var source in sources)
            {
                var name = source.GetType().FullName;

                if (!ExportSourceKeys.IsCanonical(source.Key))
                {
                    throw new InvalidOperationException($"Export source {name} declares a non-canonical key '{source.Key}'.");
                }

                if (string.IsNullOrWhiteSpace(source.RequiredPermission))
                {
                    throw new InvalidOperationException(
                        $"Export source {name} ('{source.Key}') declares no RequiredPermission. Every source names the permission its data needs.");
                }

                if (!Enum.IsDefined(source.TableCardinality))
                {
                    throw new InvalidOperationException($"Export source {name} ('{source.Key}') declares an undefined TableCardinality.");
                }

                if (_sources.TryGetValue(source.Key, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Two export sources claim the key '{source.Key}': {existing.GetType().FullName} and {name}.");
                }

                _sources.Add(source.Key, source);
            }
        }

        public IReadOnlyCollection<string> Keys => _sources.Keys;

        /// <summary>Exact, ordinal lookup: a key that is not canonical matches nothing - it is never normalised.</summary>
        public bool TryGet(string? key, out IExportSource source)
        {
            if (key is null)
            {
                source = null!;
                return false;
            }

            return _sources.TryGetValue(key, out source!);
        }
    }
}
