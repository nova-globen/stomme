#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure.Backup
{
    /// <summary>
    /// The record form the framework's participants write a part in (BL-81): a sequence of <c>[scope][length][UTF-8 JSON]</c>
    /// records - the scope a row lives in (a tenant id, or none) and the row's document. Length-prefixed rather than JSON
    /// Lines because the stores' document form is indented. Read forward, one record in memory at a time.
    /// </summary>
    internal static class BackupRecords
    {
        /// <summary>A single row larger than this is refused on read - a corrupt length, not a real row.</summary>
        public const int MaximumRecordBytes = 64 * 1024 * 1024;

        public static async Task WriteAsync(Stream stream, Guid? scope, string json, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = new byte[1 + 16 + 4];
            header[0] = scope is null ? (byte)0 : (byte)1;
            if (scope is { } tenant)
            {
                tenant.TryWriteBytes(header.AsSpan(1, 16));
            }

            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17), body.Length);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }

        public static async IAsyncEnumerable<(Guid? Scope, string Json)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var header = new byte[1 + 16 + 4];
            while (true)
            {
                var first = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
                if (first == 0)
                {
                    yield break;
                }

                if (first < header.Length || header[0] > 1)
                {
                    throw new InvalidDataException("A backup record is truncated or malformed.");
                }

                Guid? scope = header[0] == 1 ? new Guid(header.AsSpan(1, 16)) : null;
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17));
                if (length < 0 || length > MaximumRecordBytes)
                {
                    throw new InvalidDataException($"A backup record claims {length} bytes.");
                }

                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken);
                yield return (scope, Encoding.UTF8.GetString(body));
            }
        }
    }
}
