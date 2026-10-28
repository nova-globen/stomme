#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Where the file-ingestion guard stages bytes before they may reach a folder (BL-100 F1, D109).
    /// </summary>
    /// <remarks>
    /// The staging tree is <c>{ContentRoot}/Content/Staging/Tenants/{tenantId}/Sites/{siteId}</c> - OUTSIDE every
    /// folder's directory (a folder resolves under <c>Content/Tenants/...</c> or <c>wwwroot/Content/Tenants/...</c>),
    /// so no folder listing, no <c>File</c> row and no download route can ever see a half-written, unscanned or
    /// refused file. Chunk parts live in <c>chunks/{hash of the target directory}</c>; one ingestion's merged or
    /// extracted files live in a fresh <c>ingest-{guid}</c> directory deleted in a <c>finally</c>.
    /// </remarks>
    public static class FileIngestionPaths
    {
        public const string ChunksDirectoryName = "chunks";
        public const string IngestDirectoryPrefix = "ingest-";

        public static string StagingRoot(string contentRoot, int tenantId, int siteId) =>
            Path.Combine(contentRoot, "Content", "Staging", "Tenants", tenantId.ToString(), "Sites", siteId.ToString());

        public static string ChunkDirectory(string stagingRoot, string targetDirectory)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(targetDirectory)));
            return Path.Combine(stagingRoot, ChunksDirectoryName, Convert.ToHexString(hash, 0, 16).ToLowerInvariant());
        }

        public static string NewIngestDirectory(string stagingRoot) =>
            Path.Combine(stagingRoot, IngestDirectoryPrefix + Guid.NewGuid().ToString("N"));
    }
}