#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The record format of a part that carries files (<c>content:tenants</c>, <c>pipelines:files</c>):
    /// a sequence of <c>[root u8][path length i32][path, '/'-separated UTF-8][length i64][bytes]</c> - binary, streamed, no
    /// file held in memory - and the one rule a carried path is held to (zip-slip). Moved here
    /// from the content participant unchanged when a second part needed it, so both read and write the same bytes.
    /// </summary>
    public static class FileTreePart
    {
        /// <summary>The longest path, in UTF-8 bytes, a record may name.</summary>
        public const int MaximumPathBytes = 4096;

        /// <summary>Why a carried path cannot be one - null when it can: relative, '/'-separated, without an empty, '.' or '..' segment, a drive or a backslash.</summary>
        public static string? PathProblem(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/'))
            {
                return $"'{path}' is not a relative '/'-separated path";
            }

            return path.Split('/').Any(segment => segment is "" or "." or "..") ? $"'{path}' has an empty, '.' or '..' segment" : null;
        }

        /// <summary>Where a carried path lands inside <paramref name="folder"/> - refused when it would resolve outside it.</summary>
        public static string Target(string folder, string path, string partName)
        {
            var target = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{partName}: '{path}' resolves outside its tree.");
            }

            return target;
        }

        public static async Task WriteHeaderAsync(Stream stream, byte root, string path, long length, CancellationToken cancellationToken)
        {
            var name = Encoding.UTF8.GetBytes(path);
            var header = new byte[1 + 4 + name.Length + 8];
            header[0] = root;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), name.Length);
            name.CopyTo(header, 5);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5 + name.Length), length);
            await stream.WriteAsync(header, cancellationToken);
        }

        /// <summary>The next record's header; null at the end of the part.</summary>
        public static async Task<(byte Root, string Path, long Length)?> ReadHeaderAsync(Stream stream, string partName, CancellationToken cancellationToken)
        {
            var head = new byte[5];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            if (read < head.Length)
            {
                throw new InvalidDataException($"{partName}: the part is truncated.");
            }

            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
            if (nameLength <= 0 || nameLength > MaximumPathBytes)
            {
                throw new InvalidDataException($"{partName}: a path claims {nameLength} bytes.");
            }

            var rest = new byte[nameLength + 8];
            try
            {
                await stream.ReadExactlyAsync(rest, cancellationToken);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"{partName}: the part is truncated.");
            }

            var length = BinaryPrimitives.ReadInt64LittleEndian(rest.AsSpan(nameLength));
            if (length < 0)
            {
                throw new InvalidDataException($"{partName}: a file claims {length} bytes.");
            }

            return (head[0], Encoding.UTF8.GetString(rest, 0, nameLength), length);
        }

        public static Task SkipAsync(Stream stream, long length, string partName, CancellationToken cancellationToken) =>
            CopyExactlyAsync(stream, Stream.Null, length, partName, cancellationToken);

        public static async Task CopyExactlyAsync(Stream source, Stream target, long length, string partName, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException($"{partName}: the part is truncated.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
    }
}
