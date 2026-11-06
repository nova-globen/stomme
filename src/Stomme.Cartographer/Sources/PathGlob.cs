using System.Text;
using System.Text.RegularExpressions;

namespace Stomme.Cartographer.Sources;

/// <summary>
/// How a person names files to leave out or to allow: an exact path, a folder (everything under it), or a pattern
/// with * (within a folder), ** (across folders) and ?. A pattern without a slash matches the name in any folder.
/// Paths use forward slashes and are compared without regard to case.
/// </summary>
public static class PathGlob
{
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        pattern = pattern.Trim().Replace('\\', '/');
        while (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            pattern = pattern[2..];
        }

        pattern = pattern.TrimStart('/');
        if (pattern.Length == 0)
        {
            return false;
        }

        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            var folder = pattern.TrimEnd('/');
            return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        var regex = new StringBuilder("^");
        if (!pattern.TrimEnd('/').Contains('/', StringComparison.Ordinal))
        {
            regex.Append("(?:.*/)?");
        }

        pattern = pattern.TrimEnd('/');
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    regex.Append("(?:.*/)?");
                }
                else
                {
                    regex.Append(".*");
                }
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append("(?:/.*)?$");
        return Regex.IsMatch(path, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}
