using System.Text.RegularExpressions;
using Stomme.Cartographer.Sources.Decoders;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// A document's title, by code only (the verdict's redo, change 17): its front matter's title, else its front
/// matter's name, else its first level-one heading, else an html title element, else a name from its path. Never
/// a raw first line ("---", "open"), never a section heading ("When to use"), never a model's word. The title is
/// kept as written, in whatever language.
/// </summary>
public static partial class DocumentTitle
{
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme", "agents", "agent", "claude", "gemini", "skill", "index", "_index", "copilot-instructions",
        "changelog", "license", "contributing", "todo", "notes", "overview",
    };

    public static string Of(DecodedDocument? decoded, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (decoded is null)
        {
            return PathName(path);
        }

        var title = FrontMatter(decoded.Lines)
            ?? decoded.Sections.FirstOrDefault(s => s.Level == 1)?.Heading
            ?? (decoded.Sections.Count == 0 ? TopHeadingLine(decoded.Lines) : null)
            ?? HtmlTitle(decoded.Lines);
        title = title is null ? null : Plain(title);
        return string.IsNullOrWhiteSpace(title) ? PathName(path) : title;
    }

    /// <summary>A name from the path: the file's name without its extension; for a file every folder has (README, AGENTS, SKILL, index), its folder too.</summary>
    public static string PathName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return path;
        }

        var file = parts[^1];
        var dot = file.LastIndexOf('.');
        var stem = dot > 0 ? file[..dot] : file;
        return parts.Length > 1 && GenericNames.Contains(stem) ? parts[^2] + "/" + stem : stem;
    }

    /// <summary>The last <paramref name="segments"/> parts of the path, the file's without its extension: what tells two files of one name apart.</summary>
    public static string PathName(string path, int segments)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return path;
        }

        var file = parts[^1];
        var dot = file.LastIndexOf('.');
        parts[^1] = dot > 0 ? file[..dot] : file;
        return string.Join('/', parts[Math.Max(0, parts.Length - Math.Max(1, segments))..]);
    }

    /// <summary>The title, else the name, of a front matter block at the top of the text, in any text format (a .mdc rule file has one too).</summary>
    internal static string? FrontMatter(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].Trim() != "---")
        {
            return null;
        }

        string? name = null;
        for (var i = 1; i < Math.Min(lines.Count, 80); i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                return name;
            }

            if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["title:".Length..].Trim().Trim('"', '\'');
                if (value.Length > 0)
                {
                    return value;
                }
            }
            else if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                var value = line["name:".Length..].Trim().Trim('"', '\'');
                name = value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>A level-one heading written in a plain text file ("# Auto Pilot" in a .txt), outside any front matter.</summary>
    private static string? TopHeadingLine(IReadOnlyList<string> lines)
    {
        var start = 0;
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            for (var i = 1; i < Math.Min(lines.Count, 80); i++)
            {
                if (lines[i].Trim() == "---")
                {
                    start = i + 1;
                    break;
                }
            }
        }

        for (var i = start; i < Math.Min(lines.Count, start + 60); i++)
        {
            var match = TopHeading().Match(lines[i]);
            if (match.Success)
            {
                return match.Groups["text"].Value;
            }
        }

        return null;
    }

    private static string? HtmlTitle(IReadOnlyList<string> lines)
    {
        var head = string.Join("\n", lines.Take(120));
        var match = TitleElement().Match(head);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["text"].Value) : null;
    }

    /// <summary>The text without its markup: emphasis, code marks, a link's target, trailing hashes.</summary>
    private static string Plain(string text)
    {
        var plain = Link().Replace(text, "$1");
        plain = plain.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        plain = Whitespace().Replace(plain, " ").Trim().Trim('#', '*', '_').Trim();
        return plain;
    }

    [GeneratedRegex(@"^#\s+(?<text>\S.*?)\s*#*\s*$")]
    private static partial Regex TopHeading();

    [GeneratedRegex(@"<title[^>]*>\s*(?<text>.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleElement();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
