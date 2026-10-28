#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Infrastructure;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// An in-memory S3 bucket that keeps S3's rules where the archive store could break them (BL-169): a multipart object
    /// appears only when the upload COMPLETES, every part but the last is at least 5 MiB or the completion is refused (infra's
    /// store refuses it too), part numbers run 1..n, an aborted upload leaves nothing. A failure can
    /// be planted at a named operation.
    /// </summary>
    internal sealed class InMemoryS3ObjectClient : IS3ObjectClient
    {
        public const int MinimumPartBytes = 5 * 1024 * 1024;

        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
        /// <summary>Throws an <see cref="IOException"/> from the named operation (e.g. "UploadPart:2", "Complete") once.</summary>
        public string? FailAt { get; set; }

        public IReadOnlyDictionary<string, byte[]> Objects => _objects;

        public int OpenUploads => _uploads.Count;

        public List<(string Key, int PartNumber, int Length)> PartsUploaded { get; } = new();

        public Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken = default)
        {
            Maybe("Start");
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = new Upload(key);
            return Task.FromResult(id);
        }

        public Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            Maybe("UploadPart:" + partNumber);
            var upload = UploadOf(key, uploadId);
            if (partNumber < 1 || partNumber > 10_000)
            {
                throw new InvalidOperationException("InvalidPart: a part number is 1-10000.");
            }

            upload.Parts[partNumber] = content.ToArray();
            lock (PartsUploaded)
            {
                PartsUploaded.Add((key, partNumber, content.Length));
            }

            return Task.FromResult("\"etag-" + partNumber + "-" + content.Length + "\"");
        }

        public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, CancellationToken cancellationToken = default)
        {
            Maybe("Complete");
            var upload = UploadOf(key, uploadId);
            var numbers = parts.Select(part => part.PartNumber).ToList();
            if (numbers.Count == 0 || !numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
            {
                throw new InvalidOperationException("InvalidPartOrder: the parts run 1..n.");
            }

            foreach (var (number, etag) in parts)
            {
                if (!upload.Parts.TryGetValue(number, out var bytes) || etag != "\"etag-" + number + "-" + bytes.Length + "\"")
                {
                    throw new InvalidOperationException($"InvalidPart: part {number} was not uploaded as named.");
                }

                if (number < numbers.Count && bytes.Length < MinimumPartBytes)
                {
                    throw new InvalidOperationException($"EntityTooSmall: part {number} is {bytes.Length} bytes; only the last part may be under 5 MiB.");
                }
            }

            _objects[key] = parts.SelectMany(part => upload.Parts[part.PartNumber]).ToArray();
            _uploads.TryRemove(uploadId, out _);
            return Task.CompletedTask;
        }

        public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default)
        {
            _uploads.TryRemove(uploadId, out _);
            return Task.CompletedTask;
        }

        public Task PutTextAsync(string key, string text, CancellationToken cancellationToken = default)
        {
            Maybe("Put");
            _objects[key] = Encoding.UTF8.GetBytes(text);
            return Task.CompletedTask;
        }

        public async Task<bool> DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default)
        {
            Maybe("Get");
            if (!_objects.TryGetValue(key, out var bytes))
            {
                return false;
            }

            await destination.WriteAsync(bytes, cancellationToken);
            return true;
        }

        public Task<string?> GetTextAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_objects.TryGetValue(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Maybe("Delete");
            _objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken = default)
        {
            var keys = _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }

        /// <summary>Puts an object directly - a stray, another installation's archive, a tampered sidecar.</summary>
        public void Plant(string key, string text) => _objects[key] = Encoding.UTF8.GetBytes(text);

        private Upload UploadOf(string key, string uploadId) =>
            _uploads.TryGetValue(uploadId, out var upload) && upload.Key == key ? upload : throw new InvalidOperationException("NoSuchUpload");

        private void Maybe(string operation)
        {
            if (FailAt is { } planted && planted == operation)
            {
                FailAt = null;
                throw new IOException($"planted failure at {operation}");
            }
        }

        private sealed class Upload(string key)
        {
            public string Key { get; } = key;

            public ConcurrentDictionary<int, byte[]> Parts { get; } = new();
        }
    }
}
