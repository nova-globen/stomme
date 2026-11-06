using System.Text.Json.Nodes;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

using R = Stomme.Cartographer.Tests.ScriptedRow;
using Rig = Stomme.Cartographer.Tests.LedgerRig;

namespace Stomme.Cartographer.Tests;

/// <summary>
/// The shapes the second re-check of the bundles wrote against the rows by code (its scenarios S01 to S14, from
/// the verifying session): the reader says which rows exist, code says where each one is, and a row of the
/// reader's is never taken into another without a word. Each test scripts what the reader returns, so it turns on
/// the code alone, with no provider.
/// </summary>
public sealed class RowPlacementTests
{
    private static string Key(JsonNode item) => item["key"]!.GetValue<string>();

    private static string Title(JsonNode item) => item["title"]!.GetValue<string>();

    private static int Line(JsonNode item) => item["sources"]![0]!["line"]!.GetValue<int>();

    private static bool Gone(JsonNode item) => item["sourceGone"]!.GetValue<bool>();

    private static List<string> Flags(JsonNode item) => item["flags"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();

    private static List<JsonNode> Live(IEnumerable<JsonNode> items) => items.Where(i => !Gone(i) && !i["placeholder"]!.GetValue<bool>()).ToList();

    /// <summary>S01, S14: a short title is found on its own row, not on an earlier row that mentions it, whether the reader gives the title or the whole line.</summary>
    [Fact]
    public async Task ATitleMentionedInAnEarlierRowIsFoundOnItsOwnRow()
    {
        foreach (var whole in new[] { false, true })
        {
            using var rig = new Rig();
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Backlog\n\n## Next\n\n- **Fix login**: the retry also touches the Release notes and the Homebrew formula.\n- **Release notes**: write them.\n- **Homebrew formula**: publish it.\n- **Fix login tests**: cover the retry.\n");
            rig.Script["docs/backlog.md"] = whole
                ? [new("Fix login: the retry also touches the Release notes and the Homebrew formula."), new("Release notes: write them."), new("Homebrew formula: publish it."), new("Fix login tests: cover the retry.")]
                : [new("Fix login"), new("Release notes"), new("Homebrew formula"), new("Fix login tests")];
            var items = Live(await rig.Run());
            Assert.Equal(
                [("docs/backlog.md#fix-login", 5), ("docs/backlog.md#release-notes", 6), ("docs/backlog.md#homebrew-formula", 7), ("docs/backlog.md#fix-login-tests", 8)],
                items.OrderBy(Line).Select(i => (Key(i), Line(i))));
            Assert.All(items, i => Assert.DoesNotContain("row-not-located", Flags(i)));
        }
    }

    /// <summary>S02, S03: rows are where the reader's rows are found, whatever marks the file uses: plain lines, bullets of any kind, boxes, bare checkboxes.</summary>
    [Fact]
    public async Task RowsAreSplitAtEveryRowFoundWhateverTheFileMarksItsEntriesWith()
    {
        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.txt", "open-ledger"));
            rig.Write("docs/backlog.txt", "TODO\n\nFix login\nAdd tests\nShip the release\n");
            rig.Script["docs/backlog.txt"] = [new("Fix login"), new("Add tests"), new("Ship the release")];
            var items = Live(await rig.Run());
            Assert.Equal([("Fix login", 3), ("Add tests", 4), ("Ship the release", 5)], items.OrderBy(Line).Select(i => (Title(i), Line(i))));
            Assert.All(items, i => Assert.DoesNotContain("row-not-located", Flags(i)));
        }

        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Next\n\n• Fix login\n• Add tests\n• Ship the release\n\n☐ Write the guide\n☑ Pick a name\n\n[ ] Bare checkbox one\n[x] Bare checkbox two\n");
            rig.Script["docs/backlog.md"] = [new("Fix login"), new("Add tests"), new("Ship the release"), new("Write the guide"), new("Pick a name"), new("Bare checkbox one"), new("Bare checkbox two", Status: "done")];
            var items = Live(await rig.Run());
            Assert.Equal(
                [("Fix login", 3), ("Add tests", 4), ("Ship the release", 5), ("Write the guide", 7), ("Pick a name", 8), ("Bare checkbox one", 10), ("Bare checkbox two", 11)],
                items.OrderBy(Line).Select(i => (Title(i), Line(i))));
            Assert.All(items, i => Assert.DoesNotContain("row-not-located", Flags(i)));
            Assert.Equal(7, items.Select(i => i["rowSha256"]!.GetValue<string>()).Distinct().Count());
        }
    }

    /// <summary>S04: the id alone in the bracket that ends a row is the row's own, so a "blocked by" lands on a row and no placeholder is made.</summary>
    [Fact]
    public async Task AnIdAloneInTheBracketThatEndsARowIsTheRowsOwn()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- [ ] Do the first thing (BL-1)\n- [ ] Do the second thing [BL-2]\n- [ ] Do the third thing, blocked by BL-1 (BL-3)\n- [ ] Do the fourth thing - BL-4\n");
        rig.Script["docs/backlog.md"] = [new("Do the first thing", "BL-1"), new("Do the second thing", "BL-2"), new("Do the third thing, blocked by BL-1", "BL-3", Refs: "BL-1"), new("Do the fourth thing", "BL-4")];
        var items = await rig.Run();
        Assert.DoesNotContain(items, i => i["placeholder"]!.GetValue<bool>());
        Assert.Equal(["BL-1", "BL-2", "BL-3"], items.OrderBy(Line).Take(3).Select(Key));
        Assert.Equal(["Do the first thing", "Do the second thing", "Do the third thing, blocked by BL-1"], items.OrderBy(Line).Take(3).Select(Title));
        Assert.Equal(4, Live(items).Count);
    }

    /// <summary>S05, S08, S10: shapes left as they are; no row is lost in any of them.</summary>
    [Fact]
    public async Task ARowThatOpensWithAnotherRowsIdAndRowsAlikeInTitleEachKeepAnItem()
    {
        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Next\n\n- BL-2 must land first, then do the migration\n- BL-1 Do the first thing\n- BL-2 Do the second thing\n");
            rig.Script["docs/backlog.md"] = [new("BL-2 must land first, then do the migration"), new("Do the first thing", "BL-1"), new("Do the second thing", "BL-2")];
            var items = Live(await rig.Run());
            Assert.Equal([3, 4, 5], items.Select(Line).OrderBy(l => l));
        }

        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "finished-ledger"));
            rig.Write("docs/backlog.md", "# Done\n\n- 2026-11-01: Upgraded the server. To 10.80.\n- 2026-10-01: Upgraded the server. To 10.79.\n");
            rig.Script["docs/backlog.md"] = [new("Upgraded the server"), new("Upgraded the server")];
            Assert.Equal([3, 4], Live(await rig.Run()).Select(Line).OrderBy(l => l));
            rig.Edit("docs/backlog.md", t => t.Replace("# Done\n\n", "# Done\n\n- 2026-12-01: Upgraded the server. To 10.81.\n", StringComparison.Ordinal));
            rig.Script["docs/backlog.md"] = [new("Upgraded the server"), new("Upgraded the server"), new("Upgraded the server")];
            var after = Live(await rig.Run());
            Assert.Equal([3, 4, 5], after.Select(Line).OrderBy(l => l));
            Assert.Equal(3, after.Select(i => i["rowSha256"]!.GetValue<string>()).Distinct().Count());
        }

        using (var rig = new Rig())
        {
            rig.Ledgers(("specs/a/backlog.md", "open-ledger"));
            rig.Write("specs/a/backlog.md", "# Tasks\n\n- [ ] BL-1 Design the login\n- [ ] BL-2 Build the login\n- [ ] BL-3 Test the login\n");
            rig.Script["specs/a/backlog.md"] = [new("Design the login", "BL-1"), new("Build the login", "BL-2"), new("Test the login", "BL-3")];
            Assert.Equal(["BL-1", "BL-2", "BL-3"], Live(await rig.Run()).Select(Key));
            rig.Ledgers(("specs/a/backlog.md", "open-ledger"), ("specs/b/backlog.md", "open-ledger"));
            rig.Write("specs/b/backlog.md", "# Tasks\n\n- [ ] BL-1 Design the export\n- [ ] BL-2 Build the export\n");
            rig.Script["specs/b/backlog.md"] = [new("Design the export", "BL-1"), new("Build the export", "BL-2")];
            rig.Options = rig.NewOptions(rerun: true);
            var both = Live(await rig.Run());
            Assert.Equal(5, both.Count);
            Assert.Equal(5, both.Select(i => i["sources"]![0]!["path"] + ":" + Line(i)).Distinct().Count());
        }
    }

    /// <summary>S06, S13: a row keeps its id as its key whatever is put around the id, so closing a row by striking it or ticking it does not turn it into a new row.</summary>
    [Fact]
    public async Task ARowsIdIsItsKeyWhateverStandsAroundItAndClosingARowKeepsItsKey()
    {
        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md",
                "# Next\n\n" +
                "- BL-1 plain\n- BL-2: colon\n- BL-3 - dash\n- **BL-4** bold id\n- **BL-5: bold all**\n- (BL-6) parens\n- [BL-7] brackets\n- [BL-8](http://x/8) linked\n" +
                "- `BL-9` code\n- ~~BL-10 struck~~\n- ✅ BL-11 emoji\n- [~] BL-12 odd checkbox\n- [ ] BL-13 plain box\n- [x] **BL-14**: bold box\n- BL-15/BL-16 two ids\n- BL-17, BL-18: two with comma\n- bl-19 lower case\n\n" +
                "### BL-20: heading row\n\nThe text under it.\n\n" +
                "| ID | Item | Status |\n|---|---|---|\n| [BL-21](http://x/21) | linked in a table | OPEN |\n| **BL-22** | bold in a table | OPEN |\n\n" +
                "| Item | Ticket | Status |\n|---|---|---|\n| id not first | BL-23 | OPEN |\n| another | BL-24 | OPEN |\n");
            string[] titles = ["plain", "colon", "dash", "bold id", "bold all", "parens", "brackets", "linked", "code", "struck", "emoji", "odd checkbox", "plain box", "bold box", "two ids", "two with comma", "lower case", "heading row", "linked in a table", "bold in a table", "id not first", "another"];
            int[] numbers = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 19, 20, 21, 22, 23, 24];
            rig.Script["docs/backlog.md"] = titles.Select((x, i) => new R(x, "BL-" + numbers[i])).ToArray();
            var items = Live(await rig.Run());
            Assert.Equal(numbers.Select(n => "BL-" + n), items.OrderBy(Line).Select(Key));
            Assert.Equal(titles, items.OrderBy(Line).Select(Title));
        }

        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Next\n\n- BL-1 Do the first thing\n- BL-2 Do the second thing\n- BL-3 Do the third thing\n");
            rig.Script["docs/backlog.md"] = [new("Do the first thing", "BL-1"), new("Do the second thing", "BL-2"), new("Do the third thing", "BL-3")];
            Assert.Equal(["BL-1", "BL-2", "BL-3"], (await rig.Run()).Select(Key));
            rig.Edit("docs/backlog.md", t => t.Replace("- BL-1 Do the first thing", "- ~~BL-1 Do the first thing~~", StringComparison.Ordinal).Replace("- BL-2 Do the second thing", "- ✅ BL-2 Do the second thing", StringComparison.Ordinal));
            rig.Script["docs/backlog.md"] = [new("Do the first thing", "BL-1", Status: "done"), new("Do the second thing", "BL-2", Status: "done"), new("Do the third thing", "BL-3")];
            var after = await rig.Run();
            Assert.Equal(["BL-1", "BL-2", "BL-3"], after.Select(Key));
            Assert.DoesNotContain(after, Gone);
            Assert.Equal(["done", "done", "backlog"], after.Select(i => i["status"]!.GetValue<string>()));
            Assert.Equal(["Do the first thing", "Do the second thing", "Do the third thing"], after.Select(Title));
        }
    }

    /// <summary>S07: a table's title column is the one the reader's titles point at, over the table's rows, and stays while the header row stands; an edit of one row changes that row alone.</summary>
    [Fact]
    public async Task ATablesTitleColumnIsTheOneTheReadersTitlesPointAtAndStaysWhileTheHeaderStands()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n| Area | Task | Status |\n|---|---|---|\n| Auth | Fix login | OPEN |\n| Auth | Add MFA | OPEN |\n| UI | Dark mode | OPEN |\n\n| Priority | Owner | Task |\n|---|---|---|\n| High | Ana | Ship the release |\n| High | Ana | Write the guide |\n");
        rig.Script["docs/backlog.md"] = [new("Fix login", AsWritten: "OPEN"), new("Add MFA", AsWritten: "OPEN"), new("Dark mode", AsWritten: "OPEN"), new("Ship the release"), new("Write the guide")];
        var first = Live(await rig.Run());
        Assert.Equal(["Fix login", "Add MFA", "Dark mode", "Ship the release", "Write the guide"], first.OrderBy(Line).Select(Title));
        Assert.Equal(["docs/backlog.md#fix-login", "docs/backlog.md#add-mfa", "docs/backlog.md#dark-mode", "docs/backlog.md#ship-the-release", "docs/backlog.md#write-the-guide"], first.OrderBy(Line).Select(Key));

        // A reader that words its titles otherwise on the next reading does not move the column: the choice stands with the header.
        rig.Edit("docs/backlog.md", t => t.Replace("| Add MFA |", "| Add MFA for admins |", StringComparison.Ordinal).Replace("| Write the guide |", "| Write the user guide |", StringComparison.Ordinal));
        rig.Script["docs/backlog.md"] = [new("Auth", AsWritten: "OPEN"), new("Add MFA for admins", AsWritten: "OPEN"), new("UI", AsWritten: "OPEN"), new("High"), new("Write the user guide")];
        var after = await rig.Run();
        var live = Live(after);
        Assert.Equal(["Fix login", "Add MFA for admins", "Dark mode", "Ship the release", "Write the user guide"], live.OrderBy(Line).Select(Title));
        foreach (var unchanged in new[] { "docs/backlog.md#fix-login", "docs/backlog.md#dark-mode", "docs/backlog.md#ship-the-release" })
        {
            Assert.Equal(first.Single(i => Key(i) == unchanged)["bodySha256"]!.GetValue<string>(), live.Single(i => Key(i) == unchanged)["bodySha256"]!.GetValue<string>());
        }
    }

    /// <summary>S09: the lines under a row, up to the next row, belong to it: a status line below it changes its hash, so it is read again and is not left stale.</summary>
    [Fact]
    public async Task ALineUnderARowBelongsToItSoAStatusWrittenThereIsNotLeftStale()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n\nStatus: open\n\n- Add tests\n  - [ ] write the first test\n\n- Other row\n\n## Legend\n");
        rig.Script["docs/backlog.md"] = [new("Fix login", Status: "backlog"), new("write the first test", Status: "backlog"), new("Other row", Status: "backlog")];
        var before = (await rig.Run()).ToDictionary(Key);
        rig.Edit("docs/backlog.md", t => t.Replace("Status: open", "Status: done", StringComparison.Ordinal));
        rig.Script["docs/backlog.md"] = [new("Fix login", Status: "done", AsWritten: "done"), new("write the first test", Status: "in-progress"), new("Other row", Status: "in-progress")];
        var after = (await rig.Run()).ToDictionary(Key);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.NotEqual(before["docs/backlog.md#fix-login"]["rowSha256"]!.GetValue<string>(), after["docs/backlog.md#fix-login"]["rowSha256"]!.GetValue<string>());
        Assert.Equal("done", after["docs/backlog.md#fix-login"]["status"]!.GetValue<string>());

        // The rows whose lines did not change keep what was read of them, whatever the reader says this time.
        foreach (var same in new[] { "docs/backlog.md#write-the-first-test", "docs/backlog.md#other-row" })
        {
            Assert.Equal(before[same]["rowSha256"]!.GetValue<string>(), after[same]["rowSha256"]!.GetValue<string>());
            Assert.Equal("backlog", after[same]["status"]!.GetValue<string>());
        }
    }

    /// <summary>S11, S12: a row is gone only when its lines have left the ledger. A reader that leaves a row out, or an edit above a row, does not mark it gone.</summary>
    [Fact]
    public async Task ARowIsGoneOnlyWhenItsLinesHaveLeftTheLedger()
    {
        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Add tests\n- Ship the release\n");
            rig.Script["docs/backlog.md"] = [new("Fix login"), new("Add tests"), new("Ship the release")];
            await rig.Run();
            rig.Edit("docs/backlog.md", t => t.Replace("- Fix login", "- Fix login now", StringComparison.Ordinal));
            rig.Script["docs/backlog.md"] = [new("Fix login now"), new("Ship the release")];
            var after = await rig.Run();
            var kept = Assert.Single(after, i => Key(i) == "docs/backlog.md#add-tests");
            Assert.False(Gone(kept));
            Assert.Contains("not-returned", Flags(kept));
            Assert.Equal(4, Line(kept));
            Assert.Equal(["Fix login now", "Add tests", "Ship the release"], Live(after).OrderBy(Line).Select(Title));

            // The edited row's own text is its own line: the row the reader left out was not taken into it.
            var edited = Assert.Single(after, i => Key(i) == "docs/backlog.md#fix-login-now");
            Assert.DoesNotContain("Add tests", File.ReadAllText(Path.Combine(rig.Output, edited["body"]!.GetValue<string>())), StringComparison.Ordinal);

            // The title of the first row was edited, so its earlier key names a row that is no longer written: that one is gone.
            Assert.True(Gone(Assert.Single(after, i => Key(i) == "docs/backlog.md#fix-login")));
        }

        using (var rig = new Rig())
        {
            rig.Ledgers(("docs/backlog.md", "open-ledger"));
            rig.Write("docs/backlog.md", "# Next\n\n- **Fix login**: the retry.\n- **Release notes**: write them.\n");
            rig.Script["docs/backlog.md"] = [new("Fix login"), new("Release notes", Status: "in-progress")];
            await rig.Run();
            rig.Edit("docs/backlog.md", t => t.Replace("the retry.", "the retry, then the Release notes.", StringComparison.Ordinal));
            var after = await rig.Run();
            Assert.Equal([("docs/backlog.md#fix-login", 3), ("docs/backlog.md#release-notes", 4)], after.OrderBy(Line).Select(i => (Key(i), Line(i))));
            Assert.DoesNotContain(after, Gone);
            Assert.Equal("in-progress", after.Single(i => Key(i) == "docs/backlog.md#release-notes")["status"]!.GetValue<string>());
        }
    }
}
