using System.Text.RegularExpressions;

namespace Stomme.Conductor.Tests;

/// <summary>
/// ADR 0018: Conductor never knows the words file, repository, item, document or graph; those belong to a host.
/// The scan reads every source and project file of src/Stomme.Conductor, comments and strings included.
/// </summary>
public sealed class ForbiddenWordsTests
{
    private static readonly Regex Forbidden = new(
        @"\b(file|files|repository|repositories|item|items|document|documents|graph|graphs)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>.NET's own names that carry one of the words; an API's name is not the engine's vocabulary.</summary>
    private static readonly Regex DotNet = new(
        @"\bFile\.(?=[A-Z])|\bFile(Stream|Mode|Access|Share|Info|NotFoundException)\b|\bEnumerateFiles\b|\bGetFileName\b",
        RegexOptions.Compiled);

    [Fact]
    public void ConductorNeverNamesAFileARepositoryAnItemADocumentOrAGraph()
    {
        var root = TestPaths.ConductorSource;
        var hits = new List<string>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Forbidden.Matches(DotNet.Replace(lines[i], " ")))
                {
                    hits.Add($"{relative}:{i + 1}: {match.Value}");
                }
            }
        }

        Assert.True(scanned > 10, "the Conductor source was found and read");
        Assert.True(hits.Count == 0, "Conductor's source names what only a host may know (ADR 0018):\n" + string.Join("\n", hits));
    }
}

internal static class TestPaths
{
    /// <summary>The engine's source folder in the repository, found above the test output.</summary>
    public static string ConductorSource
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null)
            {
                var candidate = Path.Combine(folder.FullName, "src", "Stomme.Conductor");
                if (File.Exists(Path.Combine(candidate, "Stomme.Conductor.csproj")))
                {
                    return candidate;
                }

                folder = folder.Parent;
            }

            throw new DirectoryNotFoundException("The Conductor source folder was not found above the test output.");
        }
    }
}
