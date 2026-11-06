using System.Security.Cryptography;
using System.Text;

namespace Stomme.Cartographer.Sources;

/// <summary>
/// Lists and hashes the input's files (ADR 0009). A git repository is listed by git (tracked files, plus
/// untracked files git does not ignore, marked as such); anything else is walked, skipping the folders the
/// playbook's table ignores. Folders a person added (ADR 0010) are listed under their own name. Nothing is
/// written anywhere.
/// </summary>
/// <summary>What a person said about the files before the run (ADR 0028): patterns to leave out, and paths the secret scan withheld that may be read after all.</summary>
public sealed record ScanRules(IReadOnlyList<string> Exclude, IReadOnlyList<string> Allow)
{
    public static ScanRules None { get; } = new([], []);
}

public static class RepositoryScanner
{
    public static RepositorySnapshot Scan(string root, IReadOnlyList<string> extraFolders, FileTable table, ScanRules? rules = null)
    {
        rules ??= ScanRules.None;
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(table);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The input folder does not exist: {root}");
        }

        var notes = new List<string>();
        var files = new List<SourceFile>();
        string listedBy;
        var git = GitInfo.Read(root);
        if (git is not null)
        {
            listedBy = "git";
            foreach (var (relative, tracked) in git.ListFiles())
            {
                var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(absolute))
                {
                    files.Add(Mark(Describe(relative, absolute, table, tracked), rules));
                }
            }
        }
        else
        {
            listedBy = "walk";
            notes.Add("The input is not a git repository (or git is not available): the tree was walked, skipping the folders the playbook ignores.");
            foreach (var absolute in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
                if (relative.StartsWith(".git/", StringComparison.Ordinal) || table.IsIgnored(relative))
                {
                    continue;
                }

                files.Add(Mark(Describe(relative, absolute, table, tracked: false), rules));
            }
        }

        foreach (var extra in extraFolders)
        {
            var extraRoot = Path.GetFullPath(extra);
            if (!Directory.Exists(extraRoot))
            {
                notes.Add($"The added documents folder does not exist and was skipped: {extraRoot}");
                continue;
            }

            var prefix = "added/" + new DirectoryInfo(extraRoot).Name + "/";
            foreach (var absolute in Directory.EnumerateFiles(extraRoot, "*", SearchOption.AllDirectories))
            {
                var relative = prefix + Path.GetRelativePath(extraRoot, absolute).Replace('\\', '/');
                if (table.IsIgnored(relative))
                {
                    continue;
                }

                files.Add(Mark(Describe(relative, absolute, table, tracked: false), rules));
            }

            notes.Add($"Documents added by a person were read from {extraRoot} under {prefix}.");
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var hash = new StringBuilder();
        foreach (var file in files)
        {
            hash.Append(file.Path).Append('\n').Append(file.Sha256).Append('\n');
        }

        var filesHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())));
        return new RepositorySnapshot(root, files, git, filesHash, listedBy, notes);
    }

    /// <summary>
    /// What must not be read (ADR 0028), settled before any phase runs: a file the person excluded, then a backup copy,
    /// then a file that looks like it holds a secret, by its name or by its text, unless the person allowed it by its path.
    /// </summary>
    private static SourceFile Mark(SourceFile file, ScanRules rules)
    {
        if (rules.Exclude.FirstOrDefault(p => PathGlob.IsMatch(p, file.Path)) is { } pattern)
        {
            return file with { NotRead = "excluded by the person: " + pattern, ExcludedBy = pattern };
        }

        if (file.NotRead is not null)
        {
            return file;
        }

        var hit = SecretScan.ByName(file.Path) ?? (file.IsText ? InContent(file) : null);
        if (hit is null)
        {
            return file;
        }

        return rules.Allow.Any(p => PathGlob.IsMatch(p, file.Path))
            ? file with { Secret = hit }
            : file with { Secret = hit, NotRead = "it looks like it holds a secret: " + hit };
    }

    private static SecretHit? InContent(SourceFile file)
    {
        try
        {
            using var stream = File.OpenRead(file.AbsolutePath);
            var bytes = new byte[Math.Min(SecretScan.MaxScanBytes, stream.Length)];
            stream.ReadExactly(bytes);
            return SecretScan.InText(Decoders.TextBytes.Decode(bytes));
        }
        catch (IOException)
        {
            return new SecretHit("scan-not-finished", null);
        }
    }

    private static SourceFile Describe(string relative, string absolute, FileTable table, bool tracked)
    {
        var info = new FileInfo(absolute);
        var extension = Path.GetExtension(relative);
        var category = table.CategoryOf(extension);
        var language = table.LanguageOf(extension)?.Name;
        string sha;
        bool isText;
        using (var stream = File.OpenRead(absolute))
        {
            var head = new byte[Math.Min(8192, info.Length)];
            var read = 0;
            while (read < head.Length)
            {
                var n = stream.Read(head, read, head.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            isText = FileTable.LooksLikeText(head.AsSpan(0, read));
            stream.Position = 0;
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        if (category is null)
        {
            category = isText ? FileCategory.TextUnknown : FileCategory.Binary;
        }
        // A document with a text extension but binary content (a docx is a zip, a pdf a stream) keeps its category and
        // is not text: no decoder reads it until one for its format exists (TR-8), and its bytes are never a body.

        return new SourceFile(relative, absolute, info.Length, extension, category.Value, language, isText, sha, tracked) { NotRead = BackupCopy(relative, extension) };
    }
    private static readonly HashSet<string> BackupExtensions = new(StringComparer.OrdinalIgnoreCase) { ".bak", ".orig", ".old", ".tmp", ".swp", ".swo", ".rej" };

    /// <summary>A backup or scratch copy of another file (app_offline.bak, notes.md.orig, plan.md~) is not a document of the repository: listed, never read.</summary>
    internal static string? BackupCopy(string relative, string extension) =>
        BackupExtensions.Contains(extension) || relative.EndsWith('~') ? $"a backup copy ({(relative.EndsWith('~') ? "~" : extension)})" : null;
}
