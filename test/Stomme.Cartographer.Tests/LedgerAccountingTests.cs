using Stomme.Cartographer.Catalogue;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.State;
using R = Stomme.Cartographer.Tests.ScriptedRow;
using Rig = Stomme.Cartographer.Tests.LedgerRig;

namespace Stomme.Cartographer.Tests;

/// <summary>
/// The third re-check of the bundles (its scenarios N01 to N27, from the verifying session): a row that is lost,
/// closed or merged without a word. The general answer is the final accounting of a ledger by code: every line
/// written as an entry like the rows found is set against an item, an entry with no item is listed as a finding and
/// never made an item, and every merge is flagged and a finding. Each test scripts what the reader returns.
/// </summary>
public sealed class LedgerAccountingTests
{
    private static List<WorkItem> Items(Rig rig)
    {
        using var state = RunState.Open(Path.Combine(rig.Output, "state.sqlite"));
        return ItemCatalogue.Load(state).Where(i => !i.SourceGone && !i.Placeholder).OrderBy(i => i.Sources[0].Path, StringComparer.Ordinal).ThenBy(i => i.Sources[0].Line).ToList();
    }

    private static int Line(WorkItem item) => item.Sources[0].Line!.Value;

    /// <summary>N01: the reader leaves a list row out on a first read. The entry is listed by its line, no item is made of it, and the verify phase says so.</summary>
    [Fact]
    public async Task AnEntryTheReaderLeftOutOnAFirstReadIsListedAsAFinding()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Add tests\n- Ship the release\n");
        rig.Script["docs/backlog.md"] = [new("Fix login"), new("Ship the release")];
        await rig.Run();

        Assert.Equal([("docs/backlog.md#fix-login", 3), ("docs/backlog.md#ship-the-release", 5)], Items(rig).Select(i => (i.Key, Line(i))));
        var accounting = rig.Accounting("docs/backlog.md");
        Assert.Equal((3, 2), (accounting.Entries, accounting.WithItem));
        var left = Assert.Single(accounting.WithoutItem);
        Assert.Equal((4, "- Add tests"), (left.Line, left.Text));
        Assert.Empty(accounting.Merged);
        Assert.Contains(rig.Findings(), f => f.StartsWith("1 line(s) of a ledger written as entries", StringComparison.Ordinal) && f.Contains("docs/backlog.md:4 \"- Add tests\"", StringComparison.Ordinal));
    }

    /// <summary>The accounting holds for every way a ledger writes its entries: a table without ids, headings, and a list with a list of another kind in the same section.</summary>
    [Fact]
    public async Task EveryShapeOfEntryIsAccountedFor()
    {
        using var rig = new Rig();
        rig.Ledgers(("specs/table/backlog.md", "open-ledger"), ("specs/headings/backlog.md", "open-ledger"), ("specs/lists/backlog.md", "open-ledger"));
        rig.Write("specs/table/backlog.md", "# Next\n\n| Task | Owner |\n|---|---|\n| Fix login | Ana |\n| Add tests | Bo |\n| Ship the release | Ana |\n");
        rig.Write("specs/headings/backlog.md", "# Next\n\n## Fix login\n\nThe retry.\n\n## Add tests\n\nAll of them.\n\n## Ship the release\n\nOn Friday.\n");
        rig.Write("specs/lists/backlog.md", "# Work\n\n## Notes\n\n- A note that is no work\n\n## Next\n\nContext:\n\n- The pilot starts in May\n\nTasks:\n\n1. Fix login\n   - the retry\n   - the lockout\n2. Add tests\n3. Ship the release\n");
        rig.Script["specs/table/backlog.md"] = [new("Fix login"), new("Ship the release")];
        rig.Script["specs/headings/backlog.md"] = [new("Fix login"), new("Ship the release")];
        rig.Script["specs/lists/backlog.md"] = [new("Fix login"), new("Ship the release")];
        await rig.Run();
        // A table row, a heading and a list entry the reader left out: each listed by its line.
        Assert.Equal([(6, "| Add tests | Bo |")], rig.Accounting("specs/table/backlog.md").WithoutItem.Select(e => (e.Line, e.Text)));
        Assert.Equal([(7, "## Add tests")], rig.Accounting("specs/headings/backlog.md").WithoutItem.Select(e => (e.Line, e.Text)));

        // The list of another kind in the rows' own section is listed too (a person passes over it); the entries
        // under a row and the list under another heading are not entries of the ledger's rows.
        Assert.Equal([(11, "- The pilot starts in May"), (18, "2. Add tests")], rig.Accounting("specs/lists/backlog.md").WithoutItem.Select(e => (e.Line, e.Text)));
        Assert.Equal(6, Items(rig).Count);
    }

    /// <summary>A ledger whose every entry the reader returned accounts with nothing left over, and the verify phase has no finding of it.</summary>
    [Fact]
    public async Task ALedgerReadInFullLeavesNothingOver()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n  - the retry\n- Add tests\n\nSome words between.\n\n- Ship the release\n");
        rig.Script["docs/backlog.md"] = [new("Fix login"), new("Add tests"), new("Ship the release")];
        await rig.Run();

        var accounting = rig.Accounting("docs/backlog.md");
        Assert.Equal((3, 3), (accounting.Entries, accounting.WithItem));
        Assert.Empty(accounting.WithoutItem);
        Assert.DoesNotContain(rig.Findings(), f => f.Contains("written as entries", StringComparison.Ordinal) || f.Contains("more than one row", StringComparison.Ordinal));
    }

    /// <summary>N05, N23: plain lines, the middle one left out. A row with no mark takes its title and its key from its first line alone, and its hash from its own line, whichever rows the reader returned.</summary>
    [Fact]
    public async Task AnUnmarkedRowIsItsFirstLineWhicheverRowsTheReaderReturned()
    {
        const string text = "TODO\n\nFix login\nAdd tests\nShip the release\n";
        using var all = new Rig();
        all.Ledgers(("docs/backlog.txt", "open-ledger"));
        all.Write("docs/backlog.txt", text);
        all.Script["docs/backlog.txt"] = [new("Fix login"), new("Add tests"), new("Ship the release")];
        await all.Run();

        using var some = new Rig();
        some.Ledgers(("docs/backlog.txt", "open-ledger"));
        some.Write("docs/backlog.txt", text);
        some.Script["docs/backlog.txt"] = [new("Fix login"), new("Ship the release")];
        await some.Run();

        var three = Items(all);
        var two = Items(some);
        Assert.Equal([("docs/backlog.txt#fix-login", "Fix login", 3), ("docs/backlog.txt#add-tests", "Add tests", 4), ("docs/backlog.txt#ship-the-release", "Ship the release", 5)], three.Select(i => (i.Key, i.Title, Line(i))));
        Assert.Equal([("docs/backlog.txt#fix-login", "Fix login", 3), ("docs/backlog.txt#ship-the-release", "Ship the release", 5)], two.Select(i => (i.Key, i.Title, Line(i))));
        Assert.Equal(three[0].RowSha256, two[0].RowSha256);
        Assert.Equal(three[2].RowSha256, two[1].RowSha256);
        Assert.Equal([(4, "Add tests")], some.Accounting("docs/backlog.txt").WithoutItem.Select(e => (e.Line, e.Text)));

        // A paragraph that wraps, with one row in it: the title is its first line, never two lines joined.
        using var wrapped = new Rig();
        wrapped.Ledgers(("docs/backlog.txt", "open-ledger"));
        wrapped.Write("docs/backlog.txt", "TODO\n\nFix the login for\neveryone who signs in\n");
        wrapped.Script["docs/backlog.txt"] = [new("Fix the login for")];
        await wrapped.Run();
        var row = Assert.Single(Items(wrapped));
        Assert.Equal(("docs/backlog.txt#fix-the-login-for", "Fix the login for"), (row.Key, row.Title));
    }

    /// <summary>N06: an open row that names a finished row in the bracket that ends it is a row of its own, not the finished row moved: five rows, five items, and a reference.</summary>
    [Fact]
    public async Task AnIdInTheLastBracketGivesWayToTheRowThatOpensWithIt()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog-done.md", "finished-ledger"));
        rig.Write("docs/backlog-done.md", "# Done\n\n- BL-1 Build the login page\n- BL-2 Something else\n- BL-3 Something more\n");
        rig.Write("docs/backlog.md", "# Next\n\n- Build the login page follow-up (BL-1)\n- [ ] BL-4 Another thing\n");
        rig.Script["docs/backlog-done.md"] = [new("Build the login page", "BL-1", Status: "done"), new("Something else", "BL-2", Status: "done"), new("Something more", "BL-3", Status: "done")];
        rig.Script["docs/backlog.md"] = [new("Build the login page follow-up", "BL-1"), new("Another thing", "BL-4")];
        await rig.Run();

        var items = Items(rig);
        Assert.Equal(["BL-1", "BL-2", "BL-3", "docs/backlog.md#build-the-login-page-follow-up", "BL-4"], items.Select(i => i.Key));
        Assert.All(items, i => Assert.DoesNotContain("in-two-ledgers", i.Flags));
        var finished = items[0];
        Assert.Equal(("Build the login page", "done", "docs/backlog-done.md"), (finished.Title, finished.Status, finished.Sources[0].Path));
        var followUp = items[3];
        Assert.Equal(("Build the login page follow-up", "backlog"), (followUp.Title, followUp.Status));
        Assert.DoesNotContain("BL-1", followUp.Aliases);
        Assert.Contains(followUp.References, r => r.Kind == "id" && r.Text == "BL-1" && r.ResolvedKey == "BL-1");
        Assert.DoesNotContain(rig.Findings(), f => f.Contains("more than one row", StringComparison.Ordinal) || f.Contains("written as entries", StringComparison.Ordinal));
    }

    /// <summary>N07: the same inside one ledger, and when the reader did not return the row that opens with the id: the file says whose id it is.</summary>
    [Fact]
    public async Task AnIdInTheLastBracketGivesWayInsideOneLedgerAndToARowTheReaderLeftOut()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Write the guide (BL-1)\n- BL-1 Build the login page\n- BL-2 Another\n- BL-3 More\n");
        rig.Script["docs/backlog.md"] = [new("Write the guide", Refs: "BL-1"), new("Build the login page", "BL-1"), new("Another", "BL-2"), new("More", "BL-3")];
        await rig.Run();
        var items = Items(rig);
        Assert.Equal([("docs/backlog.md#write-the-guide", "Write the guide"), ("BL-1", "Build the login page"), ("BL-2", "Another"), ("BL-3", "More")], items.Select(i => (i.Key, i.Title)));
        Assert.Contains(items[0].References, r => r.Text == "BL-1" && r.ResolvedKey == "BL-1");

        using var left = new Rig();
        left.Ledgers(("docs/backlog.md", "open-ledger"));
        left.Write("docs/backlog.md", "# Next\n\n- Write the guide (BL-1)\n- BL-1 Build the login page\n");
        left.Script["docs/backlog.md"] = [new("Write the guide", "BL-1")];
        await left.Run();
        Assert.Equal(["docs/backlog.md#write-the-guide"], Items(left).Select(i => i.Key));
        Assert.Equal([(4, "- BL-1 Build the login page")], left.Accounting("docs/backlog.md").WithoutItem.Select(e => (e.Line, e.Text)));
    }

    /// <summary>
    /// An id in an open and a finished ledger is one row that moved only when the two rows read alike. A title that
    /// only begins with the other's is another row; and a row that did move is merged, flagged, and a finding.
    /// </summary>
    [Fact]
    public async Task ATitleThatOnlyBeginsWithTheOthersIsNoAgreementAndARealMergeIsAFinding()
    {
        using var apart = new Rig();
        apart.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog-done.md", "finished-ledger"));
        apart.Write("docs/backlog-done.md", "# Done\n\n- BL-1 Build the login page\n");
        apart.Write("docs/backlog.md", "# Next\n\n- BL-1 Build the login page follow-up\n");
        apart.Script["docs/backlog-done.md"] = [new("Build the login page", "BL-1", Status: "done")];
        apart.Script["docs/backlog.md"] = [new("Build the login page follow-up", "BL-1")];
        await apart.Run();
        var two = Items(apart);
        Assert.Equal(["docs/backlog-done.md#BL-1", "docs/backlog.md#BL-1"], two.Select(i => i.Key));
        Assert.All(two, i => Assert.Contains("id-in-several-ledgers", i.Flags));

        using var moved = new Rig("verify");
        moved.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog-done.md", "finished-ledger"));
        moved.Write("docs/backlog-done.md", "# Done\n\n- BL-1 Build the login page\n");
        moved.Write("docs/backlog.md", "# Next\n\n- BL-1 Build the login page\n- BL-2 Another thing\n");
        moved.Script["docs/backlog-done.md"] = [new("Build the login page", "BL-1", Status: "done")];
        moved.Script["docs/backlog.md"] = [new("Build the login page", "BL-1"), new("Another thing", "BL-2")];
        await moved.Run();
        var one = Assert.Single(Items(moved), i => i.Key == "BL-1");
        Assert.Contains("in-two-ledgers", one.Flags);
        Assert.Contains(moved.Findings(), f => f.StartsWith("1 item(s) that hold more than one row", StringComparison.Ordinal) && f.Contains("BL-1 (rows of docs/backlog-done.md:3 and docs/backlog.md:3)", StringComparison.Ordinal));
    }

    /// <summary>
    /// N10: under a heading that says finished, a row whose own box or word says it is not finished keeps its state
    /// and a person is asked; the nearest heading that says open or finished decides, so a row under "still open"
    /// inside Done is open, and one under a date inside Done is finished. An answer closes the row.
    /// </summary>
    [Fact]
    public async Task ARowsOwnOpenWordStandsAgainstAFinishedSectionAndThePersonIsAsked()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog.md", "finished-ledger"));
        rig.Write("docs/backlog.md",
            "# Backlog\n\n## Next\n\n- Fix login\n\n## Done\n\n- [x] Shipped the app\n- [ ] Still have to write the guide\n- Add tests: in progress, half way\n- Tidy the names\n\n" +
            "### 2026-10\n\n- Renamed the service\n\n### Follow-ups, still open\n\n- [ ] Clean up the flags\n- Move the docs\n");
        rig.Script["docs/backlog.md"] =
        [
            new("Fix login"), new("Shipped the app", Status: "done", AsWritten: "[x]"), new("Still have to write the guide", Status: "backlog", AsWritten: "[ ]"),
            new("Add tests", Status: "in-progress", AsWritten: "in progress"), new("Tidy the names"), new("Renamed the service"),
            new("Clean up the flags", Status: "backlog", AsWritten: "[ ]"), new("Move the docs"),
        ];
        await rig.Run();

        var items = Items(rig).ToDictionary(i => i.Title, StringComparer.Ordinal);
        Assert.Equal(8, items.Count);
        Assert.Equal("backlog", items["Fix login"].Status);
        Assert.Equal("done", items["Shipped the app"].Status);

        // The box and the word say not finished: the row keeps its own state, is flagged, and a person is asked.
        var guide = items["Still have to write the guide"];
        Assert.Equal("backlog", guide.Status);
        Assert.Contains("open-in-finished-ledger", guide.Flags);
        Assert.DoesNotContain("status-by-ledger", guide.Flags);
        var tests = items["Add tests"];
        Assert.Equal("in-progress", tests.Status);
        Assert.Contains("open-in-finished-ledger", tests.Flags);
        var questions = rig.Questions();
        Assert.Contains("## open-in-finished:docs/backlog.md#still-have-to-write-the-guide", questions, StringComparison.Ordinal);
        Assert.Contains("## open-in-finished:docs/backlog.md#add-tests", questions, StringComparison.Ordinal);
        Assert.Contains("under the heading \"Done\" of docs/backlog.md", questions, StringComparison.Ordinal);
        Assert.Contains("Default if unanswered: open", questions, StringComparison.Ordinal);

        // A row with no word of its own that the reader gives no state is finished by its section; so is one under a date inside Done.
        Assert.Equal(("done", DecidedBy.Rule), (items["Tidy the names"].Status, items["Tidy the names"].StatusBy));
        Assert.Contains("status-by-ledger", items["Tidy the names"].Flags);
        Assert.Equal("done", items["Renamed the service"].Status);

        // The nearest heading says still open: its rows are open, with no flag and no question.
        Assert.Equal("backlog", items["Clean up the flags"].Status);
        Assert.Equal("backlog", items["Move the docs"].Status);
        Assert.DoesNotContain("open-in-finished-ledger", items["Clean up the flags"].Flags);
        Assert.DoesNotContain("clean-up-the-flags", questions, StringComparison.Ordinal);
        Assert.Contains(rig.Findings(), f => f.StartsWith("2 row(s) of a finished ledger or section whose own words say they are not finished", StringComparison.Ordinal));

        // The person says the guide is finished: done by the person, its box kept as the condition; the other still asks.
        rig.Answer(("open-in-finished:docs/backlog.md#still-have-to-write-the-guide", "finished"));
        await rig.Run();
        var answered = Items(rig).Single(i => i.Title == "Still have to write the guide");
        Assert.Equal(("done", DecidedBy.Person, "[ ]"), (answered.Status, answered.StatusBy, answered.Condition));
        Assert.Equal("in-progress", Items(rig).Single(i => i.Title == "Add tests").Status);
    }

    /// <summary>N11: a finished ledger whose rows say OPEN and REOPENED. Each keeps its own state with a question; a word that tells how the work ended is closed by the ledger; one the judge is unsure of is asked, not closed.</summary>
    [Fact]
    public async Task OpenWordsInAFinishedLedgerAskAndOutcomeWordsClose()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog-done.md", "finished-ledger"));
        rig.Write("docs/backlog-done.md", "# Done\n\n| ID | Item | Status |\n|---|---|---|\n| BL-1 | First | DONE |\n| BL-2 | Second | OPEN |\n| BL-3 | Third | REOPENED 2026-11-01 |\n| BL-4 | Fourth | WONTFIX-ISH |\n| BL-5 | Fifth | HALF THERE |\n");
        rig.Script["docs/backlog-done.md"] =
        [
            new("First", "BL-1", "done", "DONE"), new("Second", "BL-2", "backlog", "OPEN"), new("Third", "BL-3", "in-progress", "REOPENED 2026-11-01"),
            new("Fourth", "BL-4", "backlog", "WONTFIX-ISH"), new("Fifth", "BL-5", "in-progress", "HALF THERE"),
        ];
        await rig.Run();

        var items = Items(rig).ToDictionary(i => i.Key, StringComparer.Ordinal);
        Assert.Equal("done", items["BL-1"].Status);
        Assert.Equal("backlog", items["BL-2"].Status);
        Assert.Equal("in-progress", items["BL-3"].Status);
        Assert.All(new[] { "BL-2", "BL-3", "BL-5" }, k =>
        {
            Assert.Contains("open-in-finished-ledger", items[k].Flags);
            Assert.DoesNotContain("status-by-ledger", items[k].Flags);
            Assert.Contains("## open-in-finished:" + k, rig.Questions(), StringComparison.Ordinal);
        });
        Assert.Contains("in docs/backlog-done.md, a ledger of finished work", rig.Questions(), StringComparison.Ordinal);

        // The words tell how the work ended: the ledger closes the row and keeps the words.
        Assert.Equal(("done", "WONTFIX-ISH"), (items["BL-4"].Status, items["BL-4"].Condition));
        Assert.Contains("status-by-ledger", items["BL-4"].Flags);
        Assert.DoesNotContain("## open-in-finished:BL-4", rig.Questions(), StringComparison.Ordinal);

        // The judge was unsure of "HALF THERE": asked, not closed.
        Assert.Equal("in-progress", items["BL-5"].Status);
    }

    /// <summary>N02 (backlog TR-71): three rows alike in one section. No row is dropped without a word: the item that holds two of them is flagged and listed.</summary>
    [Fact]
    public async Task RowsTakenIntoOneItemAreFlaggedAndListed()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Review the copy\n- Fix login\n- Review the copy\n- Add tests\n- Review the copy\n");
        rig.Script["docs/backlog.md"] = [new("Review the copy"), new("Fix login"), new("Review the copy"), new("Add tests"), new("Review the copy")];
        await rig.Run();

        var accounting = rig.Accounting("docs/backlog.md");
        Assert.Equal((5, 5), (accounting.Entries, accounting.WithItem));
        var merged = Assert.Single(accounting.Merged);
        Assert.Contains("(lines 5, 7)", merged, StringComparison.Ordinal);
        Assert.Contains("rows-merged", Assert.Single(Items(rig), i => Line(i) == 5).Flags);
        Assert.Contains(rig.Findings(), f => f.StartsWith("1 item(s) that hold more than one row", StringComparison.Ordinal) && f.Contains("lines 5, 7", StringComparison.Ordinal));
    }

    /// <summary>N21, N04 (backlog TR-72, TR-74): a row the reader words in its own way is not found in the file, so it is flagged, and the entry it stands for is listed: both sides of the miss show.</summary>
    [Fact]
    public async Task ARowNotFoundAsWrittenShowsOnBothSides()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Ship the release\n- Clipboard sync, file transfer, audio\n");
        rig.Script["docs/backlog.md"] = [new("Repair the login"), new("Ship the release"), new("Clipboard sync"), new("file transfer"), new("audio")];
        await rig.Run();

        using var state = RunState.Open(Path.Combine(rig.Output, "state.sqlite"));
        var items = ItemCatalogue.Load(state).Where(i => !i.SourceGone).ToList();
        Assert.Equal(5, items.Count);
        Assert.Equal(["Repair the login", "audio", "file transfer"], items.Where(i => i.Flags.Contains("row-not-located")).Select(i => i.Title).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal([(3, "- Fix login")], rig.Accounting("docs/backlog.md").WithoutItem.Select(e => (e.Line, e.Text)));
    }

    /// <summary>N25, N26, N27 (backlog TR-73, TR-70): an orientation that names no id scheme, a table whose titles hold the first column's words, a table of one row. The keys are not what they should be yet; no row is lost and each stands on its own line.</summary>
    [Fact]
    public async Task TheShapesLeftAsRowsLoseNoRow()
    {
        using var noScheme = new Rig();
        noScheme.Reader.NoSchemes = true;
        noScheme.Ledgers(("docs/backlog.md", "open-ledger"));
        noScheme.Write("docs/backlog.md", "# Next\n\n- BL-1 Do the first thing\n- BL-2 Do the second thing\n- BL-3 Do the third thing\n");
        noScheme.Script["docs/backlog.md"] = [new("Do the first thing", "BL-1"), new("Do the second thing", "BL-2"), new("Do the third thing", "BL-3")];
        await noScheme.Run();
        Assert.Equal([3, 4, 5], Items(noScheme).Select(Line));
        Assert.Empty(noScheme.Accounting("docs/backlog.md").WithoutItem);

        using var table = new Rig();
        table.Ledgers(("docs/backlog.md", "open-ledger"));
        table.Write("docs/backlog.md", "# Next\n\n| Area | Task | Status |\n|---|---|---|\n| Auth | Fix Auth login | OPEN |\n| Auth | Auth MFA | OPEN |\n| UI | UI dark mode | OPEN |\n");
        table.Script["docs/backlog.md"] = [new("Fix Auth login", AsWritten: "OPEN"), new("Auth MFA", AsWritten: "OPEN"), new("UI dark mode", AsWritten: "OPEN")];
        await table.Run();
        Assert.Equal([5, 6, 7], Items(table).Select(Line));
        Assert.Equal((3, 3), (table.Accounting("docs/backlog.md").Entries, table.Accounting("docs/backlog.md").WithItem));

        using var oneRow = new Rig();
        oneRow.Ledgers(("docs/backlog.md", "open-ledger"));
        oneRow.Write("docs/backlog.md", "# Next\n\n| Area | Task | Status |\n|---|---|---|\n| Auth | Fix login | OPEN |\n");
        oneRow.Script["docs/backlog.md"] = [new("Fix login", AsWritten: "OPEN")];
        await oneRow.Run();
        oneRow.Edit("docs/backlog.md", "| Auth | Fix login | OPEN |\n", "| Auth | Fix login | OPEN |\n| UI | Dark mode | OPEN |\n");
        oneRow.Script["docs/backlog.md"] = [new("Fix login", AsWritten: "OPEN"), new("Dark mode", AsWritten: "OPEN")];
        await oneRow.Run();
        Assert.Equal([5, 6], Items(oneRow).Select(Line));
    }

    /// <summary>
    /// D01 (the fourth re-check): under Done, a row that writes no status word and that the reader reads as open is
    /// asked about like a row with an open word; a row the reader gives no state is finished by its section.
    /// </summary>
    [Fact]
    public async Task ARowTheReaderReadsAsOpenWithNoWordIsAskedAboutToo()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog.md", "finished-ledger"));
        rig.Write("docs/backlog.md", "# Backlog\n\n## Next\n\n- Fix login\n\n## Done\n\n- First row. ACCEPTED\n- Third row: still to write the guide\n- Fourth row: wrote the tests\n- Sixth row. CLOSED\n");
        rig.Script["docs/backlog.md"] =
        [
            new("Fix login"), new("First row", Status: "backlog", AsWritten: "ACCEPTED"), new("Third row", Status: "backlog"),
            new("Fourth row"), new("Sixth row", Status: "done", AsWritten: "CLOSED"),
        ];
        await rig.Run();

        var items = Items(rig).ToDictionary(i => i.Title, StringComparer.Ordinal);
        var third = items["Third row"];
        Assert.Equal(("backlog", DecidedBy.Reader), (third.Status, third.StatusBy));
        Assert.Contains("open-in-finished-ledger", third.Flags);
        Assert.DoesNotContain("status-by-ledger", third.Flags);
        Assert.Contains("## open-in-finished:docs/backlog.md#third-row", rig.Questions(), StringComparison.Ordinal);
        Assert.Contains("It writes no status word, and the reader read its text as not finished (backlog)", rig.Questions(), StringComparison.Ordinal);
        Assert.Contains(rig.Findings(), f => f.StartsWith("1 row(s) of a finished ledger or section whose own words say they are not finished", StringComparison.Ordinal) && f.Contains("docs/backlog.md#third-row", StringComparison.Ordinal));

        // No word and no state from the reader: the section says finished. A word that tells how the work ended closes.
        Assert.Equal(("done", DecidedBy.Rule), (items["Fourth row"].Status, items["Fourth row"].StatusBy));
        Assert.DoesNotContain("open-in-finished-ledger", items["Fourth row"].Flags);
        Assert.Equal(("done", "ACCEPTED"), (items["First row"].Status, items["First row"].Condition));
        Assert.Equal("done", items["Sixth row"].Status);
    }

    /// <summary>
    /// A01, A02 (the fourth re-check): the reader returns the rows of one section and says nothing of the others. The
    /// accounting starts from what the file holds: each section with list entries or table rows and no row is one
    /// finding line, with its heading, the count and the lines. No item is made of them.
    /// </summary>
    [Fact]
    public async Task ASectionWithEntriesAndNoRowIsOneFindingLine()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md",
            "# Backlog\n\n- A note before any heading\n\n## Next\n\n- Fix login\n- Add tests\n\n## Later\n\n- Write the guide\n  - with pictures\n- Ship the release\n- Pick a name\n\n## Ideas\n\n1. Dark mode\n2. Export\n\n" +
            "## Table\n\n| Area | Task |\n|---|---|\n| Auth | Add MFA |\n| UI | Dark mode |\n\n## Words\n\nOnly a paragraph here.\n\n> - a quoted line\n");
        rig.Script["docs/backlog.md"] = [new("Fix login"), new("Add tests")];
        await rig.Run();

        Assert.Equal(["docs/backlog.md#fix-login", "docs/backlog.md#add-tests"], Items(rig).Select(i => i.Key));
        var accounting = rig.Accounting("docs/backlog.md");
        Assert.Empty(accounting.WithoutItem);
        Assert.Empty(accounting.Unread);
        Assert.Equal(
            [("Backlog", 1, 3, 3), ("Later", 3, 12, 15), ("Ideas", 2, 19, 20), ("Table", 2, 26, 27)],
            accounting.SectionsWithoutRow.Select(s => (s.Heading, s.Entries, s.FirstLine, s.LastLine)));
        Assert.Contains(rig.Findings(), f => f.StartsWith("4 section(s) of a ledger that hold list entries or table rows and no row", StringComparison.Ordinal)
            && f.Contains("docs/backlog.md under \"Later\" (3 entries, lines 12 to 15)", StringComparison.Ordinal) && f.Contains("under \"Table\" (2 entries, lines 26 to 27)", StringComparison.Ordinal));
        Assert.DoesNotContain("ledger-no-rows", rig.Questions(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A03, A04, A05: the reader returns no row for a whole ledger, the only one or one of two, of list entries or
    /// of plain lines. One finding with the count over the whole ledger, and a question; no item is made by code.
    /// A person who says it lists no work ends the finding.
    /// </summary>
    [Fact]
    public async Task ALedgerTheReaderReturnedNoRowForIsAFindingAndAQuestion()
    {
        using var rig = new Rig("verify");
        rig.Ledgers(("docs/backlog.md", "open-ledger"), ("docs/backlog-done.md", "finished-ledger"), ("docs/backlog.txt", "open-ledger"));
        rig.Write("docs/backlog.md", "# Backlog\n\n## Next\n\n- Fix login\n- Add tests\n- Write the guide\n\n## Later\n\n- Ship the app\n");
        rig.Write("docs/backlog-done.md", "# Done\n\n- Shipped the app\n- Picked a name\n");
        rig.Write("docs/backlog.txt", "TODO\n\nFix login\nAdd tests\nShip the release\n");
        rig.Script["docs/backlog.md"] = [];
        rig.Script["docs/backlog.txt"] = [];
        rig.Script["docs/backlog-done.md"] = [new("Shipped the app"), new("Picked a name")];
        await rig.Run();

        Assert.Equal(["docs/backlog-done.md#shipped-the-app", "docs/backlog-done.md#picked-a-name"], Items(rig).Select(i => i.Key));
        var list = Assert.Single(rig.Accounting("docs/backlog.md").Unread);
        Assert.Equal((0, 4, 5, 11), (list.Chunk, list.Entries, list.FirstLine, list.LastLine));
        Assert.Empty(rig.Accounting("docs/backlog.md").SectionsWithoutRow);
        var plain = Assert.Single(rig.Accounting("docs/backlog.txt").Unread);
        Assert.Equal((0, 4), (plain.Chunk, plain.Entries));
        Assert.Empty(rig.Accounting("docs/backlog-done.md").Unread);
        Assert.Contains(rig.Findings(), f => f.StartsWith("2 ledger(s) or chunk(s) of a ledger the reader returned no row for", StringComparison.Ordinal)
            && f.Contains("docs/backlog.md, the whole ledger (4 entries, lines 5 to 11)", StringComparison.Ordinal) && f.Contains("docs/backlog.txt, the whole ledger (4 entries", StringComparison.Ordinal));
        var questions = rig.Questions();
        Assert.Contains("## ledger-no-rows:docs/backlog.md", questions, StringComparison.Ordinal);
        Assert.Contains("## ledger-no-rows:docs/backlog.txt", questions, StringComparison.Ordinal);
        Assert.Contains("Default if unanswered: leave", questions, StringComparison.Ordinal);

        // The person says the plain file lists no work: no finding of it any more; the other still stands.
        rig.Answer(("ledger-no-rows:docs/backlog.txt", "no-work"));
        await rig.Run();
        Assert.Empty(rig.Accounting("docs/backlog.txt").Unread);
        Assert.Contains(rig.Findings(), f => f.StartsWith("1 ledger(s) or chunk(s) of a ledger the reader returned no row for", StringComparison.Ordinal));
    }

    /// <summary>The person asks for a ledger that came back empty to be read again: the reader is asked once more, told that the first reading returned nothing, and its rows become items.</summary>
    [Fact]
    public async Task ALedgerThatCameBackEmptyIsReadAgainOnAPersonsWord()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Add tests\n");
        rig.Dynamic = user => user.Contains("An earlier reading of this text returned no row", StringComparison.Ordinal) ? [new R("Fix login"), new R("Add tests")] : [];
        await rig.Run();
        Assert.Empty(Items(rig));
        Assert.Equal(2, Assert.Single(rig.Accounting("docs/backlog.md").Unread).Entries);

        rig.Answer(("ledger-no-rows:docs/backlog.md", "read-again"));
        await rig.Run();
        Assert.Equal(["docs/backlog.md#fix-login", "docs/backlog.md#add-tests"], Items(rig).Select(i => i.Key));
        Assert.Empty(rig.Accounting("docs/backlog.md").Unread);

        // The run after that reads nothing again and keeps the rows.
        await rig.Run();
        Assert.Equal(2, Items(rig).Count);
        Assert.Equal(1, rig.Reader.Sent.Count(s => s.Contains("An earlier reading of this text returned no row", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A06, A07: a long ledger whose second chunk comes back with no row, under one heading or with a heading every
    /// twenty rows. The chunk is one finding with the count of every entry in it that has no item, each lost row
    /// counted once, and a question.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChunkTheReaderReturnedNoRowForIsCountedWhole(bool headings)
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        var text = new System.Text.StringBuilder("# Backlog\n\n");
        for (var n = 1; n <= 900; n++)
        {
            if (headings && n % 20 == 1)
            {
                text.Append("\n## Part ").Append(n).Append("\n\n");
            }

            text.Append("- Row number ").Append(n).Append(" of the long list: ").Append(new string('x', 400)).Append(" end ").Append(n).Append('\n');
        }

        rig.Write("docs/backlog.md", text.ToString());
        var silent = new List<int>();
        rig.Dynamic = user =>
        {
            var start = user.IndexOf("=== INPUT TEXT", StringComparison.Ordinal);
            var body = user[user.IndexOf("===\n", start, StringComparison.Ordinal)..];
            var numbers = System.Text.RegularExpressions.Regex.Matches(body, "^- Row number (\\d+) of", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            lock (silent)
            {
                // The chunk that holds row 300 comes back empty, with no word.
                if (numbers.Count > 0 && (numbers.Contains(300) || (silent.Count > 0 && numbers[0] == silent[0])))
                {
                    if (silent.Count == 0)
                    {
                        silent.Add(numbers[0]);
                    }

                    return [];
                }
            }

            return numbers.Select(n => new R($"Row number {n} of the long list")).ToArray();
        };
        await rig.Run();

        var lost = 900 - Items(rig).Count;
        Assert.InRange(lost, 50, 400);
        var accounting = rig.Accounting("docs/backlog.md");
        var chunk = Assert.Single(accounting.Unread);
        Assert.True(chunk.Chunk > 0 && chunk.Chunks > 2, $"chunk {chunk.Chunk} of {chunk.Chunks}");
        Assert.Equal(lost, chunk.Entries);
        Assert.Empty(accounting.WithoutItem);
        Assert.Empty(accounting.SectionsWithoutRow);
        Assert.Contains("## ledger-no-rows:docs/backlog.md", rig.Questions(), StringComparison.Ordinal);
        Assert.Contains($"chunk {chunk.Chunk} of {chunk.Chunks}", rig.Questions(), StringComparison.Ordinal);
    }

    /// <summary>R02 (the last re-check): a person who picks read-again and writes words of their own beside it is heard: the option counts, whatever the text says.</summary>
    [Fact]
    public async Task ReadAgainWithThePersonsOwnWordsBesideItIsHeard()
    {
        using var rig = new Rig();
        rig.Ledgers(("docs/backlog.md", "open-ledger"));
        rig.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Add tests\n");
        rig.Dynamic = user => user.Contains("An earlier reading of this text returned no row", StringComparison.Ordinal) ? [new R("Fix login"), new R("Add tests")] : [];
        await rig.Run();
        Assert.Empty(Items(rig));

        File.WriteAllText(Path.Combine(rig.Output, "answers.json"), "{ \"ledger-no-rows:docs/backlog.md\": { \"text\": \"please look once more, these are real tasks\", \"option\": \"read-again\", \"by\": \"person\" } }");
        await rig.Run();
        Assert.Equal(["docs/backlog.md#fix-login", "docs/backlog.md#add-tests"], Items(rig).Select(i => i.Key));

        // And no-work with words beside it ends the finding of a ledger that stays empty.
        using var quiet = new Rig();
        quiet.Ledgers(("docs/backlog.md", "open-ledger"));
        quiet.Write("docs/backlog.md", "# Next\n\n- Fix login\n- Add tests\n");
        quiet.Script["docs/backlog.md"] = [];
        await quiet.Run();
        Assert.Single(quiet.Accounting("docs/backlog.md").Unread);
        File.WriteAllText(Path.Combine(quiet.Output, "answers.json"), "{ \"ledger-no-rows:docs/backlog.md\": { \"text\": \"these are notes, not tasks\", \"option\": \"no-work\", \"by\": \"person\" } }");
        await quiet.Run();
        Assert.Empty(quiet.Accounting("docs/backlog.md").Unread);
    }
}
