#nullable enable
using System;
using System.IO;

namespace Stomme.Modules.Backup.Infrastructure
{
    /// <summary>
    /// <c>Backup:Archive</c>: which store takes NEW snapshots - <c>Local</c> (the default, <c>|DataDirectory|backups</c>) or
    /// <c>S3</c> (an S3-compatible object store) - and the object store's settings. Read from THIS host's configuration, the
    /// override file included. A snapshot records the store that holds it, so switching the provider leaves the
    /// earlier snapshots restorable from where they are.
    /// </summary>
    public sealed class BackupArchiveOptions
    {
        public const string SectionName = "Backup:Archive";
        public const string LocalProvider = "Local";
        public const string S3Provider = "S3";

        public string Provider { get; set; } = LocalProvider;

        public S3ArchiveOptions S3 { get; set; } = new();

        /// <summary>True when new snapshots go to the object store.</summary>
        public bool UsesS3 => string.Equals(Provider?.Trim(), S3Provider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>Backup:Archive:S3</c>: the object store (infra's shared store: LAN-direct, path-style, SigV4 the gate). The key pair is
    /// read from FILES when <see cref="AccessKeyIdFile"/>/<see cref="SecretAccessKeyFile"/> are set - a deployed container gets
    /// two read-only files and never the key in its environment - and from the plain keys otherwise (a test run gets them from
    /// its environment). A value is never logged, returned to a page or written to a row.
    /// </summary>
    public sealed class S3ArchiveOptions
    {
        public const int MinimumPartBytes = 5 * 1024 * 1024; // S3 rejects a smaller part unless it is the last
        public const int DefaultPartBytes = 8 * 1024 * 1024;

        public string? Endpoint { get; set; }

        public string? Bucket { get; set; }

        /// <summary>The key prefix inside the bucket; empty: the installation's id, so installations can share a bucket.</summary>
        public string? Prefix { get; set; }

        public string Region { get; set; } = "us-east-1";

        public bool ForcePathStyle { get; set; } = true;

        public string? AccessKeyId { get; set; }

        public string? SecretAccessKey { get; set; }

        public string? AccessKeyIdFile { get; set; }

        public string? SecretAccessKeyFile { get; set; }

        /// <summary>The size of one multipart part; at least 5 MiB (S3's own floor), 8 MiB by default - the heap a publish holds.</summary>
        public int PartSizeBytes { get; set; } = DefaultPartBytes;

        /// <summary>
        /// What is missing for the object store to be used, or null when nothing is. Names the SETTING, never a value; the key
        /// files are checked for existence only.
        /// </summary>
        public string? Problem()
        {
            if (string.IsNullOrWhiteSpace(Endpoint) || !Stomme.Shared.Utilities.TryCreateAbsoluteUri(Endpoint.Trim(), out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                return "Backup:Archive:S3:Endpoint is not an http(s) address.";
            }

            if (string.IsNullOrWhiteSpace(Bucket))
            {
                return "Backup:Archive:S3:Bucket is not set.";
            }

            if (PartSizeBytes < MinimumPartBytes)
            {
                return $"Backup:Archive:S3:PartSizeBytes is below S3's floor of {MinimumPartBytes} bytes.";
            }

            if (!string.IsNullOrWhiteSpace(AccessKeyIdFile) ? !File.Exists(AccessKeyIdFile) : string.IsNullOrWhiteSpace(AccessKeyId))
            {
                return string.IsNullOrWhiteSpace(AccessKeyIdFile) ? "Backup:Archive:S3:AccessKeyId is not set." : "Backup:Archive:S3:AccessKeyIdFile names no file.";
            }

            if (!string.IsNullOrWhiteSpace(SecretAccessKeyFile) ? !File.Exists(SecretAccessKeyFile) : string.IsNullOrWhiteSpace(SecretAccessKey))
            {
                return string.IsNullOrWhiteSpace(SecretAccessKeyFile) ? "Backup:Archive:S3:SecretAccessKey is not set." : "Backup:Archive:S3:SecretAccessKeyFile names no file.";
            }

            return null;
        }

        /// <summary>The key pair - each from its file when one is named (one raw line, trimmed), from its setting otherwise.</summary>
        public (string AccessKeyId, string SecretAccessKey) ResolveCredentials()
        {
            if (Problem() is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            var id = !string.IsNullOrWhiteSpace(AccessKeyIdFile) ? File.ReadAllText(AccessKeyIdFile).Trim() : AccessKeyId!.Trim();
            var secret = !string.IsNullOrWhiteSpace(SecretAccessKeyFile) ? File.ReadAllText(SecretAccessKeyFile).Trim() : SecretAccessKey!.Trim();
            if (id.Length == 0 || secret.Length == 0)
            {
                throw new InvalidOperationException("A Backup:Archive:S3 key file is empty.");
            }

            return (id, secret);
        }

        /// <summary>The prefix every key of this installation lives under, without slashes at either end.</summary>
        public string ResolvePrefix(string installationId)
        {
            var prefix = string.IsNullOrWhiteSpace(Prefix) ? installationId : Prefix;
            prefix = (prefix ?? string.Empty).Trim().Trim('/');
            if (prefix.Length == 0 || prefix.Contains("..", StringComparison.Ordinal) || prefix.Contains('\\'))
            {
                throw new InvalidOperationException("The backup archive prefix is empty or not a plain path (Backup:Archive:S3:Prefix, or the installation id).");
            }

            return prefix;
        }
    }
}
