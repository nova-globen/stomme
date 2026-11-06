using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Stomme.Cartographer.Sources;

/// <summary>One commit of the input's history, for the decisions phase (ADR 0006) and the manifest.</summary>
public sealed record Commit(string Hash, DateTimeOffset Date, string Author, string Subject, string Body);

/// <summary>What git says about the input: the commit, the branch, whether the tree is clean, and the log (ADR 0009).</summary>
public sealed class GitInfo
{
    private readonly string _root;
    private readonly Dictionary<string, string> _withheld = new(StringComparer.Ordinal);

    private GitInfo(string root, string commit, string branch, bool clean)
    {
        _root = root;
        Commit = commit;
        Branch = branch;
        Clean = clean;
    }

    public string Commit { get; }

    public string Branch { get; }

    public bool Clean { get; }

    /// <summary>Commits whose message looks like it holds a secret, with the rule (ADR 0028): left out of every log this returns, so no phase sends them.</summary>
    public IReadOnlyDictionary<string, string> WithheldCommits
    {
        get
        {
            lock (_withheld)
            {
                return new Dictionary<string, string>(_withheld, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Reads the repository at <paramref name="root"/>, or null when it is not one or git is not installed.</summary>
    public static GitInfo? Read(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            return null;
        }

        var commit = Run(root, "rev-parse HEAD");
        if (commit is null)
        {
            return null;
        }

        var branch = Run(root, "branch --show-current") ?? "";
        var status = Run(root, "status --porcelain") ?? "";
        return new GitInfo(root, commit.Trim(), branch.Trim(), string.IsNullOrWhiteSpace(status));
    }

    /// <summary>Tracked files, then untracked files git does not ignore; forward slashes.</summary>
    public IEnumerable<(string Path, bool Tracked)> ListFiles()
    {
        var tracked = Run(_root, "ls-files -z", nulSeparated: true) ?? "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(path))
            {
                yield return (path, true);
            }
        }

        var untracked = Run(_root, "ls-files -z --others --exclude-standard", nulSeparated: true) ?? "";
        foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(path))
            {
                yield return (path, false);
            }
        }
    }

    /// <summary>The log, newest first, bodies cut at <paramref name="bodyChars"/>, at most <paramref name="max"/> commits.</summary>
    public IReadOnlyList<Commit> Log(int max = 5000, int bodyChars = 1500)
    {
        var raw = Run(_root, $"log --max-count={max.ToString(CultureInfo.InvariantCulture)} --format=%H%x1f%aI%x1f%an%x1f%s%x1f%b%x1e", nulSeparated: true) ?? "";
        var commits = new List<Commit>();
        foreach (var entry in raw.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split('\x1f');
            if (fields.Length < 4)
            {
                continue;
            }

            var hash = fields[0].Trim();
            if (hash.Length == 0)
            {
                continue;
            }

            DateTimeOffset.TryParse(fields[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date);
            var body = fields.Length > 4 ? fields[4].Trim() : "";
            if (SecretScan.InText(fields[3] + "\n" + body) is { } hit)
            {
                lock (_withheld)
                {
                    _withheld[hash] = hit.Rule;
                }

                continue;
            }

            if (body.Length > bodyChars)
            {
                body = body[..bodyChars] + "...";
            }

            commits.Add(new Commit(hash, date, fields[2].Trim(), fields[3].Trim(), body));
        }

        return commits;
    }

    private static string? Run(string root, string arguments, bool nulSeparated = false)
    {
        try
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                process.Kill(true);
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
