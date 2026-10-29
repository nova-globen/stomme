#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// A file's stream lent to one attempt's request content (the file connectors' uploads): the retry policy builds a
    /// request per attempt and disposes it - its content with it - so the content holds this, which leaves the stream open,
    /// and the upload rewinds the stream and lends it again for the next attempt. Read-only.
    /// </summary>
    public sealed class LentStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
