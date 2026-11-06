using System.Globalization;
using System.IO.Compression;
using System.Text;
using Stomme.Cartographer.Sources;
using Stomme.Conductor.Policies;

namespace Stomme.Cartographer.Atlas;

/// <summary>
/// Zips the files a run reads (documents, images, design files, data; not code, not build output, not .git)
/// under backup/ in the output folder, named by the run's time and the input's commit, with a manifest of
/// hashes inside (ADR 0009). The survey's filling of the engine's backup policy.
/// </summary>
public static class ZipBackup
{
    public static BackupResult Run(RepositorySnapshot snapshot, string outputFolder, string runId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var folder = Path.Combine(outputFolder, "backup");
        Directory.CreateDirectory(folder);
        var commit = snapshot.Git?.Commit is { Length: >= 7 } c ? c[..7] : "nogit";
        var zipPath = Path.Combine(folder, $"{runId}-{commit}.zip");
        var included = snapshot.Files.Where(f => f.NotRead is null && IsBackedUp(f)).ToList();
        long bytes = 0;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new StringBuilder();
            manifest.AppendLine(CultureInfo.InvariantCulture, $"input: {snapshot.Root}");
            manifest.AppendLine(CultureInfo.InvariantCulture, $"commit: {snapshot.Git?.Commit ?? "(not a git repository)"}");
            manifest.AppendLine(CultureInfo.InvariantCulture, $"branch: {snapshot.Git?.Branch ?? ""}");
            manifest.AppendLine(CultureInfo.InvariantCulture, $"clean: {(snapshot.Git?.Clean.ToString() ?? "unknown")}");
            manifest.AppendLine(CultureInfo.InvariantCulture, $"taken: {DateTimeOffset.Now:O}");
            manifest.AppendLine();
            foreach (var file in included)
            {
                zip.CreateEntryFromFile(file.AbsolutePath, file.Path, CompressionLevel.Optimal);
                bytes += file.Size;
                manifest.AppendLine(CultureInfo.InvariantCulture, $"{file.Sha256}  {file.Path}");
            }

            var entry = zip.CreateEntry("BACKUP-MANIFEST.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(manifest.ToString());
        }

        return new BackupResult(zipPath, included.Count, bytes,
            $"{included.Count} files ({bytes / 1024:N0} KB) zipped: documents, images, design files and data; code is not included (it is in git).");
    }

    public static bool IsBackedUp(SourceFile file) =>
        file.Category is FileCategory.Document or FileCategory.Image or FileCategory.Design or FileCategory.Data or FileCategory.TextUnknown;
}
