using System.Text.RegularExpressions;

namespace Stomme.Conductor.Tests;

/// <summary>
/// ADR 0022: the engine's source, its playbook and its tests become public through Stomme's snapshot, so none of it
/// may carry a local path, a session name, a dev-host name or the names of the key variables outside the console.
/// </summary>
public sealed class PublicTextTests
{
    // The account and machine names are written as character classes ([m], [T], [t], 0[1-9]) so this public file names
    // none of them itself; each still matches only the plain word.
    private static readonly Regex Private = new(
        @"[A-Za-z]:\\Users|/c/Users|\bdes[m]a\b|\bROS[T]AM\b|\bros[t]am\b|\b(docker|runner|store)0[1-9]\b|\blocal_[0-9a-f]{8}\b|\bfravashi:[a-z-]+|\bstomme:[a-z-]+|\binfra:[a-z-]+|192\.168\.|\bside-session-notes\b",
        RegexOptions.Compiled);

    private static readonly Regex KeyNames = new(@"NG_FRAVASHI_", RegexOptions.Compiled);

    [Fact]
    public void NothingPublicNamesALocalPathASessionAHostOrAKeyVariable()
    {
        var root = RepositoryRoot();
        var hits = new List<string>();
        var scanned = 0;
        var folders = new[]
        {
            "src/Stomme.Conductor", "src/Stomme.Cartographer", "src/Stomme.Cartographer.Cli", "playbooks",
            "test/Stomme.Conductor.Tests", "test/Stomme.Cartographer.Tests",
        };
        foreach (var folder in folders)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.EndsWith("/PublicTextTests.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                var lines = File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match match in Private.Matches(lines[i]))
                    {
                        hits.Add($"{relative}:{i + 1}: {match.Value}");
                    }

                    if (relative.StartsWith("src/", StringComparison.Ordinal)
                        && !relative.StartsWith("src/Stomme.Cartographer.Cli/", StringComparison.Ordinal) && KeyNames.IsMatch(lines[i]))
                    {
                        hits.Add($"{relative}:{i + 1}: a key variable's name outside the console");
                    }
                }
            }
        }

        Assert.True(scanned > 30, "the source and the playbook were found and read");
        Assert.True(hits.Count == 0, "public text carries what must stay local (ADR 0022):\n" + string.Join("\n", hits));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Stomme.slnx")))
            {
                return folder.FullName;
            }

            folder = folder.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found above the test output.");
    }
}
