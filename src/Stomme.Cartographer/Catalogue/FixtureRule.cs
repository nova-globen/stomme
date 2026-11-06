namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// What lies under a folder named example, sample, test, fixture, demo or the like is usually not the repository's
/// own: a ledger there is set aside until a person says read (backlog TR-25), a document there carries the flag
/// from-fixture, and a decision only such documents hold is never the repository's recorded one nor a draft
/// (the verdict's redo, change 13). The rule is the folder's name, in any repository.
/// </summary>
public static class FixtureRule
{
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "example", "examples", "sample", "samples", "fixture", "fixtures", "test", "tests", "testdata", "test-data", "__tests__", "spec", "specs-fixtures", "demo", "demos", "mock", "mocks", "snapshots", "__snapshots__",
    };

    /// <summary>The first folder of the path that names examples, tests or fixtures, or null when none does. The file's own name never counts.</summary>
    public static string? FolderOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/')[..^1].FirstOrDefault(Folders.Contains);
    }
}
