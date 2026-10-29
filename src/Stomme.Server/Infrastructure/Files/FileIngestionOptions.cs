#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The installation-wide caps of the file-ingestion guard, read from the <c>Files</c>
    /// configuration section on every use so a changed <c>appsettings</c> takes effect without a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys and defaults: <c>Files:MaxUploadBytes</c> 100 MB (one file, from any path: upload, chunk set, zip entry,
    /// URL download); <c>Files:Unzip:MaxEntries</c> 1000 (every entry in the central directory counts, including the
    /// ones skipped for their name or extension); <c>Files:Unzip:MaxTotalBytes</c> 1 GB (decompressed bytes of one
    /// archive, COUNTED while decompressing, never taken from the entry headers); <c>Files:Unzip:MaxEntryRatio</c>
    /// 100 (decompressed / compressed bytes of one entry, enforced once an entry has produced more than
    /// <c>Files:Unzip:RatioFloorBytes</c>, 64 KiB, so a tiny well-compressed file is not refused);
    /// <c>Files:Chunks:OrphanMinutes</c> 60 (age after which an incomplete chunk set or a staging directory is
    /// swept); <c>Files:UploadByUrl:AllowedHosts</c> (empty; host names the outbound connect guard lets through even
    /// when they resolve to an internal address).
    /// </para>
    /// <para>
    /// <b>The size cap stays below <see cref="int.MaxValue"/>.</b> <c>File.Size</c> is an <c>int</c> column; a
    /// file above 2 GB used to overflow it (<c>(int)fileinfo.Length</c>). A configured
    /// <c>Files:MaxUploadBytes</c> at or above <see cref="int.MaxValue"/> is REJECTED (an
    /// <see cref="InvalidOperationException"/> naming the key), never silently clamped.
    /// </para>
    /// </remarks>
    public sealed class FileIngestionOptions
    {
        public const string SectionName = "Files";

        public const long DefaultMaxUploadBytes = 100L * 1024 * 1024;
        public const int DefaultUnzipMaxEntries = 1000;
        public const long DefaultUnzipMaxTotalBytes = 1024L * 1024 * 1024;
        public const double DefaultUnzipMaxEntryRatio = 100;
        public const long DefaultUnzipRatioFloorBytes = 64L * 1024;
        public const int DefaultChunkOrphanMinutes = 60;

        public long MaxUploadBytes { get; init; } = DefaultMaxUploadBytes;

        public int UnzipMaxEntries { get; init; } = DefaultUnzipMaxEntries;

        public long UnzipMaxTotalBytes { get; init; } = DefaultUnzipMaxTotalBytes;

        public double UnzipMaxEntryRatio { get; init; } = DefaultUnzipMaxEntryRatio;

        public long UnzipRatioFloorBytes { get; init; } = DefaultUnzipRatioFloorBytes;

        public int ChunkOrphanMinutes { get; init; } = DefaultChunkOrphanMinutes;

        public IReadOnlyList<string> UploadByUrlAllowedHosts { get; init; } = Array.Empty<string>();

        public TimeSpan ChunkOrphanWindow => TimeSpan.FromMinutes(ChunkOrphanMinutes);

        /// <summary>Reads and validates the <c>Files</c> section. Throws on a value the guard cannot honour.</summary>
        public static FileIngestionOptions FromConfiguration(IConfiguration? configuration)
        {
            var section = configuration?.GetSection(SectionName);
            var options = new FileIngestionOptions
            {
                MaxUploadBytes = ReadLong(section, "MaxUploadBytes", DefaultMaxUploadBytes),
                UnzipMaxEntries = (int)ReadLong(section, "Unzip:MaxEntries", DefaultUnzipMaxEntries),
                UnzipMaxTotalBytes = ReadLong(section, "Unzip:MaxTotalBytes", DefaultUnzipMaxTotalBytes),
                UnzipMaxEntryRatio = ReadDouble(section, "Unzip:MaxEntryRatio", DefaultUnzipMaxEntryRatio),
                UnzipRatioFloorBytes = ReadLong(section, "Unzip:RatioFloorBytes", DefaultUnzipRatioFloorBytes),
                ChunkOrphanMinutes = (int)ReadLong(section, "Chunks:OrphanMinutes", DefaultChunkOrphanMinutes),
                UploadByUrlAllowedHosts = section?.GetSection("UploadByUrl:AllowedHosts").GetChildren()
                    .Select(child => child.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .ToArray() ?? Array.Empty<string>()
            };
            options.Validate();
            return options;
        }

        /// <summary>Throws when a cap is out of range. <c>MaxUploadBytes</c> must be in 1..int.MaxValue-1.</summary>
        public void Validate()
        {
            if (MaxUploadBytes <= 0 || MaxUploadBytes >= int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:MaxUploadBytes must be greater than 0 and less than {int.MaxValue} (File.Size is an int column); it is {MaxUploadBytes}.");
            }

            if (UnzipMaxEntries <= 0)
            {
                throw new InvalidOperationException($"{SectionName}:Unzip:MaxEntries must be greater than 0; it is {UnzipMaxEntries}.");
            }

            if (UnzipMaxTotalBytes <= 0)
            {
                throw new InvalidOperationException($"{SectionName}:Unzip:MaxTotalBytes must be greater than 0; it is {UnzipMaxTotalBytes}.");
            }

            if (UnzipMaxEntryRatio <= 0 || double.IsNaN(UnzipMaxEntryRatio))
            {
                throw new InvalidOperationException($"{SectionName}:Unzip:MaxEntryRatio must be greater than 0; it is {UnzipMaxEntryRatio}.");
            }

            if (UnzipRatioFloorBytes < 0)
            {
                throw new InvalidOperationException($"{SectionName}:Unzip:RatioFloorBytes must not be negative; it is {UnzipRatioFloorBytes}.");
            }

            if (ChunkOrphanMinutes <= 0)
            {
                throw new InvalidOperationException($"{SectionName}:Chunks:OrphanMinutes must be greater than 0; it is {ChunkOrphanMinutes}.");
            }
        }

        private static long ReadLong(IConfigurationSection? section, string key, long fallback)
        {
            var raw = section?[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"{SectionName}:{key} must be a whole number; it is '{raw}'.");
            }

            return value;
        }

        private static double ReadDouble(IConfigurationSection? section, string key, double fallback)
        {
            var raw = section?[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"{SectionName}:{key} must be a number; it is '{raw}'.");
            }

            return value;
        }
    }
}