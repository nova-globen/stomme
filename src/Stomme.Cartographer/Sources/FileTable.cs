using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stomme.Cartographer.Sources;

/// <summary>A programming language the playbook's table knows: its extensions and its comment syntax (ADR 0016: data, not code).</summary>
public sealed record LanguageEntry(string Name, IReadOnlyList<string> Extensions, string? LineComment, string? BlockCommentStart, string? BlockCommentEnd);

/// <summary>A manifest file shape the playbook's table knows (npm, cargo, a .NET project, ...).</summary>
public sealed record ManifestEntry(string Name, IReadOnlyList<string> Patterns, string Language);

/// <summary>A documentation site shape the playbook's table knows (docfx, mkdocs, ...), with the files that mark it and the table of contents.</summary>
public sealed record SiteEntry(string Name, IReadOnlyList<string> Markers, string? TableOfContents);

/// <summary>
/// The playbook's file table (data/files.json): which extensions are documents, code (and which language), data,
/// configuration, images, design files or archives; the guide file names; what a plain walk ignores. Everything
/// here is data so that a new language or format is a data change (ADR 0016).
/// </summary>
public sealed class FileTable
{
    private readonly Dictionary<string, FileCategory> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanguageEntry> _languageByExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _guidePatterns = [];
    private readonly List<string> _ignoreFolders = [];
    private readonly List<(ManifestEntry Entry, Regex[] Patterns)> _manifestPatterns = [];
    private readonly List<(SiteEntry Site, Regex[] Markers)> _sitePatterns = [];

    public FileTable(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data["categories"] is JsonObject categories)
        {
            foreach (var (name, node) in categories)
            {
                var category = Enum.Parse<FileCategory>(name, ignoreCase: true);
                foreach (var ext in (node?["extensions"] as JsonArray ?? []).Select(e => e!.GetValue<string>()))
                {
                    _byExtension[ext] = category;
                }
            }
        }

        var languages = new List<LanguageEntry>();
        foreach (var node in data["languages"] as JsonArray ?? [])
        {
            var o = (JsonObject)node!;
            var entry = new LanguageEntry(
                o["name"]!.GetValue<string>(),
                (o["extensions"] as JsonArray ?? []).Select(e => e!.GetValue<string>()).ToList(),
                o["lineComment"]?.GetValue<string>(),
                (o["blockComment"] as JsonArray)?[0]?.GetValue<string>(),
                (o["blockComment"] as JsonArray)?[1]?.GetValue<string>());
            languages.Add(entry);
            foreach (var ext in entry.Extensions)
            {
                _languageByExtension[ext] = entry;
                _byExtension[ext] = FileCategory.Code;
            }
        }

        Languages = languages;

        Manifests = (data["manifests"] as JsonArray ?? []).Select(n => new ManifestEntry(
            n!["name"]!.GetValue<string>(),
            (n["patterns"] as JsonArray ?? []).Select(e => e!.GetValue<string>()).ToList(),
            n["language"]?.GetValue<string>() ?? "")).ToList();

        Sites = (data["sites"] as JsonArray ?? []).Select(n => new SiteEntry(
            n!["name"]!.GetValue<string>(),
            (n["markers"] as JsonArray ?? []).Select(e => e!.GetValue<string>()).ToList(),
            n["toc"]?.GetValue<string>())).ToList();

        foreach (var manifest in Manifests)
        {
            _manifestPatterns.Add((manifest, manifest.Patterns.Select(GlobToRegex).ToArray()));
        }

        foreach (var site in Sites)
        {
            _sitePatterns.Add((site, site.Markers.Select(GlobToRegex).ToArray()));
        }

        foreach (var pattern in (data["guides"] as JsonArray ?? []).Select(e => e!.GetValue<string>()))
        {
            _guidePatterns.Add(GlobToRegex(pattern));
        }

        foreach (var folder in (data["ignore"] as JsonArray ?? []).Select(e => e!.GetValue<string>()))
        {
            _ignoreFolders.Add(folder.Trim('/'));
        }
    }

    public IReadOnlyList<LanguageEntry> Languages { get; }

    public IReadOnlyList<ManifestEntry> Manifests { get; }

    public IReadOnlyList<SiteEntry> Sites { get; }

    /// <summary>The category by extension, or null when the table does not know the extension.</summary>
    public FileCategory? CategoryOf(string extension) =>
        _byExtension.TryGetValue(extension, out var category) ? category : null;

    public LanguageEntry? LanguageOf(string extension) =>
        _languageByExtension.TryGetValue(extension, out var language) ? language : null;

    /// <summary>Whether a path matches one of the guide patterns (CLAUDE.md, README*, ...).</summary>
    public bool IsGuide(string path) => _guidePatterns.Any(p => p.IsMatch(path));

    /// <summary>Whether a relative path lies in a folder a plain walk ignores (bin, obj, node_modules, ...).</summary>
    public bool IsIgnored(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => _ignoreFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The manifest shapes a path matches: a bare pattern matches the file name, a pattern with a slash matches the whole path.</summary>
    public IEnumerable<ManifestEntry> ManifestsOf(string path)
    {
        var name = path.Contains('/', StringComparison.Ordinal) ? path[(path.LastIndexOf('/') + 1)..] : path;
        foreach (var (manifest, patterns) in _manifestPatterns)
        {
            for (var i = 0; i < patterns.Length; i++)
            {
                var subject = manifest.Patterns[i].Contains('/', StringComparison.Ordinal) ? path : name;
                if (patterns[i].IsMatch(subject))
                {
                    yield return manifest;
                    break;
                }
            }
        }
    }

    /// <summary>The documentation site shapes whose marker files are among <paramref name="paths"/>.</summary>
    public IEnumerable<SiteEntry> SitesAmong(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        foreach (var (site, markers) in _sitePatterns)
        {
            if (markers.Any(m => list.Any(p => m.IsMatch(p))))
            {
                yield return site;
            }
        }
    }

    /// <summary>A glob over a repository-relative path: * is anything but a slash, ** is anything, ? is one character; case-insensitive.</summary>
    public static Regex GlobToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var pattern = "^" + Regex.Escape(glob)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True when the first bytes look like text: valid UTF-8 (or UTF-16 with a BOM) and no NUL in the first 8 KB.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        var head = bytes[..Math.Min(bytes.Length, 8192)];
        if (head.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            new System.Text.UTF8Encoding(false, true).GetCharCount(head[..Math.Max(0, head.Length - 4)]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
