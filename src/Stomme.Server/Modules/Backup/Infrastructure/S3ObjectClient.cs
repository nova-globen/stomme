#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Stomme.Modules.Backup.Infrastructure
{
    /// <summary>
    /// The object-store operations the S3 archive store uses - exactly the ones infra's key permits: multipart (start,
    /// part, complete, abort), put, get, delete, list. Its own seam so the store's rules are tested against an in-memory store
    /// that behaves like S3 (a part below 5 MiB other than the last refused), and the SDK is one adapter.
    /// </summary>
    public interface IS3ObjectClient
    {
        Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Uploads one part (1-based) and answers its ETag.</summary>
        Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

        Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, CancellationToken cancellationToken = default);

        Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default);

        Task PutTextAsync(string key, string text, CancellationToken cancellationToken = default);

        /// <summary>Streams the object into <paramref name="destination"/>; false when there is no such object.</summary>
        Task<bool> DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default);

        /// <summary>The object as text; null when there is no such object.</summary>
        Task<string?> GetTextAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Removes the object; removing one that is not there is not an error.</summary>
        Task DeleteAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Every key under <paramref name="prefix"/>, across pages.</summary>
        Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// <see cref="IS3ObjectClient"/> over AWSSDK.S3 (Apache-2.0) for an S3-COMPATIBLE store: path-style addressing and the region
    /// in the signature (both required by infra's store), and the SDK's newer default of streamed trailing checksums turned down
    /// to "when required" - an S3-compatible gateway is not obliged to accept aws-chunked uploads. The client is built on first
    /// use, so a host with the object store misconfigured still boots and the first backup says what is missing.
    /// </summary>
    public sealed class AwsS3ObjectClient : IS3ObjectClient, IDisposable
    {
        private readonly S3ArchiveOptions _options;
        private readonly Lazy<AmazonS3Client> _client;

        public AwsS3ObjectClient(S3ArchiveOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _client = new Lazy<AmazonS3Client>(Create, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private string Bucket => _options.Bucket!.Trim();

        private AmazonS3Client Client => _client.Value;

        public async Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken = default) =>
            (await Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = key }, cancellationToken)).UploadId;

        public async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            var response = await Client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                PartSize = content.Length,
                InputStream = stream,
            }, cancellationToken);
            return response.ETag;
        }

        public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, CancellationToken cancellationToken = default) =>
            Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts.Select(part => new PartETag(part.PartNumber, part.ETag)).ToList(),
            }, cancellationToken);

        public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default) =>
            Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = Bucket, Key = key, UploadId = uploadId }, cancellationToken);

        public Task PutTextAsync(string key, string text, CancellationToken cancellationToken = default) =>
            Client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = text, ContentType = "text/plain" }, cancellationToken);

        public async Task<bool> DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await Client.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key }, cancellationToken);
                await response.ResponseStream.CopyToAsync(destination, 81920, cancellationToken);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<string?> GetTextAsync(string key, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            if (!await DownloadAsync(key, buffer, cancellationToken))
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key }, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone.
            }
        }

        public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken = default)
        {
            var keys = new List<string>();
            string? token = null;
            do
            {
                var response = await Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix, ContinuationToken = token }, cancellationToken);
                keys.AddRange((response.S3Objects ?? new List<S3Object>()).Select(item => item.Key));
                token = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (token is not null);

            return keys;
        }

        public void Dispose()
        {
            if (_client.IsValueCreated)
            {
                _client.Value.Dispose();
            }
        }

        private AmazonS3Client Create()
        {
            var (accessKeyId, secretAccessKey) = _options.ResolveCredentials();
            var config = new AmazonS3Config
            {
                ServiceURL = _options.Endpoint!.Trim(),
                ForcePathStyle = _options.ForcePathStyle,
                AuthenticationRegion = _options.Region,
                UseHttp = _options.Endpoint!.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };
            return new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
        }
    }
}
