using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

public sealed class ItemsRunTests
{
    private static (SurveyOptions Options, RunServices Services, ScriptedChatClient Reader, ScriptedJudge Judge) Setup(string input, string output)
    {
        var reader = new ScriptedChatClient();
        var judge = new ScriptedJudge();
        var options = new SurveyOptions
        {
            InputPath = input,
            Conductor = new ConductorOptions
            {
                OutputPath = output,
                PlaybookPath = TestPaths.Playbook,
                StopAfterPhase = "items",
                Reader = new ReaderOptions { Retries = 0 },
            },
        };
        var services = new RunServices
        {
            QuestionPolicy = (store, book) => new WrittenQuestionPolicy(store, book),
            Backup = BackupKind.None,
            ChatClients = _ => reader,
            JudgeModel = judge,
        };
        return (options, services, reader, judge);
    }

    private static List<System.Text.Json.Nodes.JsonNode> Catalogue(string output) =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray().Select(i => i!).ToList();

    /// <summary>
    /// The same raw line gives the same key, title and text whatever the reader replies (the re-check, B3): two readers
    /// that word the same rows differently, over list entries with emphasis, a date in front, a label with a number, a
    /// wrapped line, and rows in Persian and Chinese.
    /// </summary>
    [Fact]
    public async Task TwoReadersWordingsOverOneLedgerGiveTheSameKeysTitlesAndText()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        SyntheticRepository.Write(input);
        File.AppendAllText(Path.Combine(input, "docs", "backlog.md"),
            "\n## Next\n\n" +
            "- **Accessibility is very important** (and of interest to investors): usable at 200 % text, with\n" +
            "  labels read aloud and checked by hand.\n" +
            "- 2026-10-30: supply check: Home shows when the next check is due and lists what is due.\n" +
            "- [ ] **Task 1** \u2014 Write a haiku about software development\n" +
            "- Export the plan and the checklist as a document a person can print and keep in a drawer at home\n" +
            "- \u0637\u0631\u062d \u0627\u0648\u0644: \u0628\u0631\u0631\u0633\u06cc \u062f\u0633\u062a\u0631\u0633\u06cc \u0628\u0631\u0646\u0627\u0645\u0647 \u0628\u0631\u0627\u06cc \u0647\u0645\u0647\n" +
            "- \u68c0\u67e5\u6240\u6709\u7ffb\u8bd1\u7684\u8d28\u91cf\n" +
            "\n1. Sign the co-founder agreement (owner): write down the share\n" +
            "2. Send the grant application before the date\n");

        var outputs = new List<string>();
        foreach (var wording in new[] { 1, 2 })
        {
            var output = Path.Combine(temp.Path, "out" + wording);
            var (options, services, reader, _) = Setup(input, output);
            reader.BulletRows = true;
            reader.Wording = wording;
            var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
            Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
            outputs.Add(output);
        }

        static string Row(System.Text.Json.Nodes.JsonNode i) =>
            $"{i["key"]} | {i["title"]} | {i["bodySha256"]} | {i["rowSha256"]} | {i["sources"]![0]!["line"]} | {i["section"]}";
        var first = Catalogue(outputs[0]);
        var second = Catalogue(outputs[1]);
        Assert.Equal(first.Select(Row).OrderBy(r => r, StringComparer.Ordinal), second.Select(Row).OrderBy(r => r, StringComparer.Ordinal));
        Assert.All(first.Concat(second), i => Assert.DoesNotContain("row-not-located", i["flags"]!.AsArray().Select(f => f!.GetValue<string>())));
        Assert.All(first.Where(i => !i["placeholder"]!.GetValue<bool>()), i => Assert.Matches("^[0-9a-f]{64}$", i["rowSha256"]!.GetValue<string>()));

        // The title and the key are the file's: the bold lead, the words before the colon without the date, the label kept in front.
        System.Text.Json.Nodes.JsonNode Keyed(string key) => Assert.Single(first, i => i["key"]!.GetValue<string>() == key);
        Assert.Equal("Accessibility is very important", Keyed("docs/backlog.md#accessibility-is-very-important")["title"]!.GetValue<string>());
        Assert.Equal("supply check", Keyed("docs/backlog.md#supply-check")["title"]!.GetValue<string>());
        Assert.Equal("Task 1: Write a haiku about software development", Keyed("docs/backlog.md#task-1-write-a-haiku-about-software-development")["title"]!.GetValue<string>());
        Assert.Equal("Sign the co-founder agreement (owner)", Keyed("docs/backlog.md#sign-the-co-founder-agreement-owner")["title"]!.GetValue<string>());
        Assert.Equal("\u0637\u0631\u062d \u0627\u0648\u0644", Keyed("docs/backlog.md#\u0637\u0631\u062d-\u0627\u0648\u0644")["title"]!.GetValue<string>());
        Assert.Equal("\u68c0\u67e5\u6240\u6709\u7ffb\u8bd1\u7684\u8d28\u91cf", Keyed("docs/backlog.md#\u68c0\u67e5\u6240\u6709\u7ffb\u8bd1\u7684\u8d28\u91cf")["title"]!.GetValue<string>());
        Assert.DoesNotContain(first, i => i["key"]!.GetValue<string>() == "Task 1");

        // The text is the row's own lines, the date it opens with and the wrapped line included; never the reader's account.
        string TextOf(string output, string key)
        {
            var file = File.ReadAllText(Path.Combine(output, Catalogue(output).Single(i => i["key"]!.GetValue<string>() == key)["body"]!.GetValue<string>())).Replace("\r\n", "\n", StringComparison.Ordinal);
            var afterCard = file[(file.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..].TrimStart('\n');
            return afterCard[(afterCard.IndexOf('\n', StringComparison.Ordinal) + 1)..].Split("<!-- atlas:generated -->")[0].Trim();
        }

        Assert.Equal("2026-10-30: supply check: Home shows when the next check is due and lists what is due.", TextOf(outputs[0], "docs/backlog.md#supply-check"));
        Assert.Equal("**Accessibility is very important** (and of interest to investors): usable at 200 % text, with\nlabels read aloud and checked by hand.", TextOf(outputs[1], "docs/backlog.md#accessibility-is-very-important"));
        Assert.DoesNotContain("account of the row", TextOf(outputs[0], "docs/backlog.md#supply-check"), StringComparison.Ordinal);
    }

    /// <summary>
    /// One row of a ledger is edited and the reader reads the ledger again, wording and judging every row afresh: every
    /// other row keeps its key, its text, both hashes, its state and its kind; the edited row alone changes.
    /// </summary>
    [Fact]
    public async Task AfterOneRowIsEditedEveryOtherRowKeepsItsKeyItsTextAndWhatWasReadOfIt()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        var backlog = Path.Combine(input, "docs", "backlog.md");
        File.AppendAllText(backlog,
            "\n## Next\n\n" +
            "- **Pilot outreach** (owner): the one-pager is in the folder; ask the first two groups.\n" +
            "- **Privacy notice**: drafted from what the app does today.\n" +
            "- 2026-10-30: supply check: Home shows when the next check is due.\n" +
            "- \u0637\u0631\u062d \u0627\u0648\u0644: \u0628\u0631\u0631\u0633\u06cc \u0634\u062f\n");
        var (options, services, reader, judge) = Setup(input, output);
        reader.BulletRows = true;
        reader.Wording = 1;
        var first = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, first.Outcome);
        var before = Catalogue(output).ToDictionary(i => i["key"]!.GetValue<string>());

        File.WriteAllText(backlog, File.ReadAllText(backlog).Replace("ask the first two groups.", "ask the first two groups, then two more.", StringComparison.Ordinal));
        reader.Wording = 2;
        reader.JudgesAnew = true;
        judge.Drifts = true;
        var calls = reader.ItemsCalls;
        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.True(reader.ItemsCalls > calls, "the ledger changed, so the reader read it again");
        var after = Catalogue(output).ToDictionary(i => i["key"]!.GetValue<string>());

        // No key came or went.
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        const string edited = "docs/backlog.md#pilot-outreach";
        static string Whole(System.Text.Json.Nodes.JsonNode i) =>
            $"{i["title"]} | {i["bodySha256"]} | {i["rowSha256"]} | {i["status"]} | {i["type"]} | {i["level"]} | {i["kind"]} | {i["statusAsWritten"]} | {i["conditionKind"]} | {i["section"]} | {i["confidence"]!.ToJsonString()}";
        foreach (var key in before.Keys.Where(k => k != edited))
        {
            Assert.Equal(Whole(before[key]), Whole(after[key]));
        }

        // The judge was asked again and its figures drifted; the rows that did not change show the figures of the first run.
        Assert.Contains(before.Values, i => i["confidence"]!["kind"]?["by"]?.GetValue<string>() == "judge");

        // The edited row: the same key and title, another text and another hash of its lines, and this run's reading.
        Assert.Equal(before[edited]["title"]!.GetValue<string>(), after[edited]["title"]!.GetValue<string>());
        Assert.NotEqual(before[edited]["bodySha256"]!.GetValue<string>(), after[edited]["bodySha256"]!.GetValue<string>());
        Assert.NotEqual(before[edited]["rowSha256"]!.GetValue<string>(), after[edited]["rowSha256"]!.GetValue<string>());
        Assert.Equal(("in-progress", "spike"), (after[edited]["status"]!.GetValue<string>(), after[edited]["type"]!.GetValue<string>()));
        Assert.Equal("backlog", before[edited]["status"]!.GetValue<string>());
        Assert.DoesNotContain(after.Values, i => i["sourceGone"]!.GetValue<bool>());

        // A row that moves to another section is read anew too: its section is part of what was read.
        File.WriteAllText(backlog, File.ReadAllText(backlog)
            .Replace("- **Privacy notice**: drafted from what the app does today.\n", "", StringComparison.Ordinal)
            + "\n## Later\n\n- **Privacy notice**: drafted from what the app does today.\n");
        var third = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, third.Outcome);
        var moved = Catalogue(output).Single(i => i["key"]!.GetValue<string>() == "docs/backlog.md#privacy-notice");
        Assert.Equal(("Later", "in-progress"), (moved["section"]!.GetValue<string>(), moved["status"]!.GetValue<string>()));
        Assert.Equal(before["docs/backlog.md#privacy-notice"]["rowSha256"]!.GetValue<string>(), moved["rowSha256"]!.GetValue<string>());
    }

    /// <summary>
    /// One file with open and done sections, listed by the map as an open ledger and as a finished one, with emphasis in a
    /// title and a section in Persian (the re-check, B1): the file is read once, each row is one item, and its section
    /// says whether it is finished.
    /// </summary>
    [Fact]
    public async Task OneFileWithOpenAndDoneSectionsIsReadOnceAndEachRowIsOneItemWithItsSectionsState()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        File.AppendAllText(Path.Combine(input, "docs", "backlog.md"),
            "\n## Next\n\n" +
            "- **Accessibility is very important** (and of interest to investors): still to do, a pass by hand\n" +
            "- Export the plan as a page to print\n" +
            "\n## Done\n\n" +
            "- 2026-10-30: supply check: Home shows when the next check is due\n" +
            "- 2026-10-31: **Swedish and Dutch**: the strings of both, drafts until a native speaker reads them\n" +
            "\n## \u0627\u0646\u062c\u0627\u0645 \u0634\u062f\u0647\n\n" +
            "- \u0637\u0631\u062d \u0627\u0648\u0644: \u0628\u0631\u0631\u0633\u06cc \u0634\u062f\n");
        var (options, services, reader, _) = Setup(input, output);
        reader.BulletRows = true;
        reader.LedgersJson = "[{\"path\": \"docs/backlog.md\", \"kind\": \"open-ledger\", \"reasoning\": \"its open sections\", \"evidence\": null}, {\"path\": \"docs/backlog.md\", \"kind\": \"finished-ledger\", \"reasoning\": \"its Done section\", \"evidence\": null}, {\"path\": \"docs/backlog-done.md\", \"kind\": \"finished-ledger\", \"reasoning\": \"README says so.\", \"evidence\": null}]";

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        Assert.Equal(1, reader.ItemsCalls - 1);  // one call for the file that is both, one for the finished ledger

        var items = Catalogue(output);
        var lines = File.ReadAllLines(Path.Combine(input, "docs", "backlog.md"));
        var listRows = lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)) - 2;  // the two bullets of the prose list aside
        var fromLists = items.Where(i => i["sources"]![0]!["path"]!.GetValue<string>() == "docs/backlog.md" && i["key"]!.GetValue<string>().StartsWith("docs/backlog.md#", StringComparison.Ordinal) && i["section"] is not null && i["section"]!.GetValue<string>() != "Deferred features").ToList();
        Assert.Equal(listRows, fromLists.Count);

        // One line, one item.
        Assert.Equal(items.Count(i => !i["placeholder"]!.GetValue<bool>()), items.Where(i => !i["placeholder"]!.GetValue<bool>()).Select(i => i["sources"]![0]!["path"] + ":" + i["sources"]![0]!["line"]).Distinct().Count());

        System.Text.Json.Nodes.JsonNode Titled(string title) => Assert.Single(items, i => i["title"]!.GetValue<string>() == title);
        var open = Titled("Accessibility is very important");
        Assert.Equal(("backlog", "Next"), (open["status"]!.GetValue<string>(), open["section"]!.GetValue<string>()));
        Assert.DoesNotContain("open-in-finished-ledger", open["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal("backlog", Titled("Export the plan as a page to print")["status"]!.GetValue<string>());
        Assert.Equal(("done", "Done"), (Titled("supply check")["status"]!.GetValue<string>(), Titled("supply check")["section"]!.GetValue<string>()));
        Assert.Equal("done", Titled("Swedish and Dutch")["status"]!.GetValue<string>());
        Assert.Equal("done", Titled("\u0637\u0631\u062d \u0627\u0648\u0644")["status"]!.GetValue<string>());

        // The table's rows above the lists sit under no finished heading and stay as their words say.
        Assert.NotEqual("done", items.Single(i => i["key"]!.GetValue<string>() == "BL-1")["status"]!.GetValue<string>());
    }

    /// <summary>
    /// The same id in several ledgers (the re-check, B2): two ledgers of one kind never merge, even with the same wording;
    /// an open and a finished ledger merge only when the rows agree in title or text; a true move still merges. No row is
    /// lost, and no key depends on which ledger is read first.
    /// </summary>
    [Fact]
    public async Task TheSameIdInSeveralLedgersLosesNoRowAndMergesOnlyARowThatMoved()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        SyntheticRepository.Write(input);
        const string head = "# Tasks\n\n| ID | Item | Status |\n|---|---|---|\n";
        foreach (var feature in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(input, "specs", feature));
        }

        File.WriteAllText(Path.Combine(input, "specs", "alpha", "backlog.md"), head +
            "| BL-21 | Set up the project | OPEN |\n| BL-22 | Write the first test | OPEN |\n| BL-3 | A different piece of work entirely | OPEN |\n| BL-24 | \u0637\u0631\u062d \u0627\u0648\u0644 | OPEN |\n");
        File.WriteAllText(Path.Combine(input, "specs", "beta", "backlog.md"), head +
            "| BL-21 | Set up the project | OPEN |\n| BL-23 | Draw the first screen | OPEN |\n| BL-24 | \u0637\u0631\u062d \u0627\u0648\u0644 | OPEN |\n");

        // A true move: the row of the open ledger is in the finished ledger too, with the same title.
        File.AppendAllText(Path.Combine(input, "docs", "backlog-done.md"), "| BL-4b | Fourth thing, part b | DONE 2026-11-01 |\n");

        string Ledgers(params string[] order) => "[" + string.Join(", ", order.Select(p =>
            $"{{\"path\": \"{p}\", \"kind\": \"{(p.Contains("done", StringComparison.Ordinal) ? "finished-ledger" : "open-ledger")}\", \"reasoning\": \"named\", \"evidence\": null}}")) + "]";
        var listings = new[]
        {
            Ledgers("docs/backlog.md", "docs/backlog-done.md", "specs/alpha/backlog.md", "specs/beta/backlog.md"),
            Ledgers("specs/beta/backlog.md", "specs/alpha/backlog.md", "docs/backlog-done.md", "docs/backlog.md"),
        };
        var catalogues = new List<List<System.Text.Json.Nodes.JsonNode>>();
        foreach (var (listing, n) in listings.Select((l, n) => (l, n)))
        {
            var output = Path.Combine(temp.Path, "out" + n);
            var (options, services, reader, _) = Setup(input, output);
            reader.LedgersJson = listing;
            var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
            Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
            catalogues.Add(Catalogue(output));
        }

        var items = catalogues[0];
        string[] Keys(List<System.Text.Json.Nodes.JsonNode> catalogue) => catalogue.Select(i => i["key"]!.GetValue<string>()).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(Keys(catalogues[0]), Keys(catalogues[1]));

        // Two ledgers of one kind, the same id and the same wording: two rows, two items, each keyed with its ledger.
        foreach (var id in new[] { "BL-21", "BL-24" })
        {
            Assert.DoesNotContain(items, i => i["key"]!.GetValue<string>() == id);
            foreach (var feature in new[] { "alpha", "beta" })
            {
                var kept = Assert.Single(items, i => i["key"]!.GetValue<string>() == $"specs/{feature}/backlog.md#{id}");
                Assert.Contains("id-in-several-ledgers", kept["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
                Assert.False(kept["placeholder"]!.GetValue<bool>());
            }
        }

        // An open and a finished ledger whose rows do not agree: both kept.
        Assert.DoesNotContain(items, i => i["key"]!.GetValue<string>() == "BL-3");
        Assert.Equal("A different piece of work entirely", Assert.Single(items, i => i["key"]!.GetValue<string>() == "specs/alpha/backlog.md#BL-3")["title"]!.GetValue<string>());
        Assert.Equal("Third thing", Assert.Single(items, i => i["key"]!.GetValue<string>() == "docs/backlog-done.md#BL-3")["title"]!.GetValue<string>());

        // A row that moved from the open ledger to the finished one, the same title: one item, done, with both sources.
        var moved = Assert.Single(items, i => i["key"]!.GetValue<string>() == "BL-4b");
        Assert.Equal("done", moved["status"]!.GetValue<string>());
        Assert.Contains("in-two-ledgers", moved["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(2, moved["sources"]!.AsArray().Count);

        // Ids one ledger alone carries are plain keys, and every row of every ledger is an item.
        Assert.Single(items, i => i["key"]!.GetValue<string>() == "BL-22");
        Assert.Single(items, i => i["key"]!.GetValue<string>() == "BL-23");
        var rows = new[] { "docs/backlog.md", "docs/backlog-done.md", "specs/alpha/backlog.md", "specs/beta/backlog.md" }
            .Sum(p => File.ReadAllLines(Path.Combine(input, p.Replace('/', Path.DirectorySeparatorChar))).Count(l => l.StartsWith("| BL-", StringComparison.Ordinal)));
        Assert.Equal(rows - 1, items.Count(i => !i["placeholder"]!.GetValue<bool>() && !i["key"]!.GetValue<string>().Contains("deferred", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ALedgerUnderAnExamplesFolderIsSetAsideUntilAPersonSaysRead()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        Directory.CreateDirectory(Path.Combine(input, "examples"));
        File.WriteAllText(Path.Combine(input, "examples", "backlog.md"), "# Example backlog\n\n| ID | Item | Status |\n|---|---|---|\n| BL-77 | An example row | OPEN |\n");
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var items = ItemCatalogue.Load(state);
            Assert.DoesNotContain(items, i => i.Key == "BL-77");
            Assert.Contains(state.Records(ItemsPhase.SkippedLedgerKind), r => r.Key == "examples/backlog.md");
        }

        var questions = File.ReadAllText(Path.Combine(output, "questions.md"));
        Assert.Contains("## ledger-fixture:examples/backlog.md", questions, StringComparison.Ordinal);

        // The answer, as a consumer writes it: answers.json, the person's own words beside the option. The next run takes the rows in.
        var answers = Path.Combine(output, "answers.json");
        File.WriteAllText(answers, "{ \"ledger-fixture:examples/backlog.md\": { \"text\": \"yes, that one is ours\", \"option\": \"read\", \"by\": \"person\" } }");
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, again.Outcome);
        using (var after = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            Assert.Contains(ItemCatalogue.Load(after), i => i.Key == "BL-77");
            Assert.DoesNotContain(after.Records(ItemsPhase.SkippedLedgerKind), r => r.Key == "examples/backlog.md");
        }

        var asked = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray()
            .Single(q => q!["key"]!.GetValue<string>() == "ledger-fixture:examples/backlog.md");
        Assert.Equal("read", asked!["answer"]!["option"]!.GetValue<string>());
        Assert.Equal("yes, that one is ours", asked["answer"]!["text"]!.GetValue<string>());
        Assert.Contains(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray(), i => i!["key"]!.GetValue<string>() == "BL-77");

        // The answer changes: set aside after all. On the same state the row leaves the bundle whole, with no ghost marked gone.
        File.WriteAllText(answers, "{ \"ledger-fixture:examples/backlog.md\": { \"text\": \"\", \"option\": \"set-aside\", \"by\": \"person\" } }");
        var third = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, third.Outcome);
        using (var last = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            Assert.DoesNotContain(ItemCatalogue.Load(last), i => i.Key == "BL-77");
            Assert.Contains(last.Records(ItemsPhase.SkippedLedgerKind), r => r.Key == "examples/backlog.md");
        }

        var itemsAfter = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray();
        Assert.DoesNotContain(itemsAfter, i => i!["key"]!.GetValue<string>() == "BL-77");
        Assert.DoesNotContain(itemsAfter, i => i!["sourceGone"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(Path.Combine(output, "items"), "0077-*.md"));

        // A malformed answers.json stops the run with its reason: a person's answers never quietly become defaults.
        File.WriteAllText(answers, "{ \"ledger-fixture:examples/backlog.md\": { \"option\": \"read\" ");
        var failure = await Record.ExceptionAsync(() => new RepositorySurvey(options, services).RunAsync(CancellationToken.None));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConditionKindIsTheJudgesReadingOfTheConditionNeverTheBlockersAndAnUnsureOneAsksAPerson()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        File.AppendAllText(Path.Combine(input, "docs", "backlog.md"),
            "\n## More\n\n| ID | Item | Status |\n|---|---|---|\n" +
            "| BL-10 | Tenth thing, blocked by BL-1 | OPEN |\n" +
            "| BL-11 | Eleventh thing [cond: the owner must run the three-PC LAN test] | OPEN |\n" +
            "| BL-12 | Twelfth thing [cond: see the notes in the wiki] | OPEN |\n" +
            "| BL-13 | Thirteenth thing [cond: perhaps after the review] | OPEN |\n" +
            "| BL-14 | Fourteenth thing [cond: 等待所有者批准] | OPEN |\n");
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        System.Text.Json.Nodes.JsonNode Item(string key) => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray().Single(i => i!["key"]!.GetValue<string>() == key)!;

        // Blockers do not make an item blocked: the board derives that from the links, not from a flag.
        var tenth = Item("BL-10");
        Assert.Equal("BL-1", tenth["blockedBy"]!.AsArray().Single()!["key"]!.GetValue<string>());
        Assert.NotEqual("blocked", tenth["conditionKind"]?.GetValue<string>());

        // A wait the judge reads in the condition's own words, in whatever language: blocked, and never ready.
        foreach (var key in new[] { "BL-11", "BL-14" })
        {
            var waiting = Item(key);
            Assert.Equal("blocked", waiting["conditionKind"]!.GetValue<string>());
            Assert.Equal("judge", waiting["confidence"]!["conditionKind"]!["by"]!.GetValue<string>());
            Assert.NotEqual("ready", waiting["status"]!.GetValue<string>());
        }

        // A note is no wait; an unsure reading is a question with a default that is one of its options, not a guess.
        Assert.Null(Item("BL-12")["conditionKind"]);
        Assert.Null(Item("BL-13")["conditionKind"]);
        var question = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray().Single(q => q!["key"]!.GetValue<string>() == "condition:BL-13")!;
        Assert.Equal("condition", question["about"]!["kind"]!.GetValue<string>());
        Assert.Contains(question["default"]!.GetValue<string>(), question["options"]!.AsArray().Select(o => o!["value"]!.GetValue<string>()));
        Assert.Null(question["answer"]);

        // The person answers; the run resumes on the same state and the bundle follows the answer.
        File.WriteAllText(Path.Combine(output, "answers.json"), "{ \"condition:BL-13\": { \"text\": \"it waits on the review\", \"option\": \"waits\", \"by\": \"person\" } }");
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, again.Outcome);
        var thirteenth = Item("BL-13");
        Assert.Equal("blocked", thirteenth["conditionKind"]!.GetValue<string>());
        Assert.Equal("person", thirteenth["confidence"]!["conditionKind"]!["by"]!.GetValue<string>());
    }

    /// <summary>The shape that lost nine open rows on a sample: a numbered list and tables in one ledger that all count from 1.</summary>
    [Fact]
    public async Task AListAndTablesWithTheSameNumbersLoseNoRowMixNoTypeMoveNoReferenceAndAMovedRowKeepsItsKey()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        var backlog = Path.Combine(input, "docs", "backlog.md");
        File.AppendAllText(backlog,
            "\n## Next\n\n" +
            "1. Sign the co-founder agreement (task) see docs/specs/BL-1-first.md\n" +
            "2. Send the grant application (feature), blocked by BL-1\n" +
            "3. Start the pilot outreach\n" +
            "4. Tidy what the first thing left, split from BL-1\n" +
            "5. The theme of the quarter, which holds BL-2\n" +
            "6. Check the release assets exist and\n   that the checksums validate\n" +
            "\n## UX\n\n| ID | Item | Status |\n|---|---|---|\n" +
            "| 1 | Wrong layout on the first screen (bug) | OPEN |\n" +
            "| 2 | Combined items overflow (bug) | OPEN |\n" +
            "| 3 | Tapping the score does nothing (bug) | SUPERVISED |\n" +
            "\n## Done\n\n| ID | Item | Status |\n|---|---|---|\n" +
            "| 1 | Earlier work | DONE 2026-10-01 |\n");
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        string outreachKey;
        int outreachNumber;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var items = ItemCatalogue.Load(state);
            WorkItem Titled(string start) => Assert.Single(items, i => i.Title.StartsWith(start, StringComparison.Ordinal));

            // No row is lost: four of the list, three of one table, one of the other, beside the ledger's own.
            var agreement = Titled("Sign the co-founder agreement");
            var grant = Titled("Send the grant application");
            var outreach = Titled("Start the pilot outreach");
            var tidy = Titled("Tidy what the first thing left");
            var layout = Titled("Wrong layout on the first screen");
            var combined = Titled("Combined items overflow");
            var tapping = Titled("Tapping the score does nothing");
            var earlier = Titled("Earlier work");

            // A number in a list or a table is a position: never a key, never an alias a reference could land on.
            Assert.DoesNotContain(items, i => ItemsPhase.IsLocalNumber(i.Key) || i.Aliases.Any(ItemsPhase.IsLocalNumber));
            Assert.Equal(items.Count, items.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // No kind comes from another row: the judge's figures attach by the row, not by its number.
            Assert.Equal("task", agreement.Type);
            Assert.Equal("feature", grant.Type);
            Assert.Null(outreach.Type);
            Assert.All(new[] { layout, combined, tapping }, i => Assert.Equal("bug", i.Type));
            Assert.Null(earlier.Type);

            // No reference and no relation moves to the row that shares a number.
            Assert.Contains(agreement.References, r => r.Kind == "path" && r.Text == "docs/specs/BL-1-first.md");
            Assert.All(new[] { layout, combined, tapping, earlier, outreach }, i => Assert.DoesNotContain(i.References, r => r.Kind == "path"));
            var blocker = Assert.Single(grant.Relations);
            Assert.Equal("blocker", blocker.Kind);
            Assert.Equal("BL-1", blocker.ResolvedKey);
            Assert.All(new[] { layout, combined, tapping, earlier, outreach, agreement }, i => Assert.Empty(i.Relations));

            // "Split from" is where the row came from, not what contains it: lineage, by the judge, and no parent.
            var lineage = Assert.Single(tidy.Relations);
            Assert.Equal("derived", lineage.Kind);
            Assert.Equal(DecidedBy.Judge, lineage.By);
            Assert.Equal("BL-1", lineage.ResolvedKey);
            Assert.Null(tidy.Parent);

            // The line of every row is its line in the file, by code; the section is the heading it sits under.
            var fileLines = File.ReadAllLines(backlog);
            Assert.All(new[] { agreement, grant, outreach, tidy, layout, combined, tapping, earlier },
                i => Assert.Contains(i.Title, fileLines[i.Sources[0].Line!.Value - 1], StringComparison.Ordinal));
            Assert.Equal("Next", grant.Section);
            Assert.Equal("UX", layout.Section);
            Assert.Equal("Done", earlier.Section);
            outreachKey = outreach.Key;
            outreachNumber = outreach.Number;
        }

        var tidyJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray()
            .Single(i => i!["title"]!.GetValue<string>().StartsWith("Tidy what", StringComparison.Ordinal))!;
        Assert.Null(tidyJson["parent"]);

        // A list entry wrapped over two lines has a title of one line, so its file's heading is one line and the text
        // under it hashes to bodySha256 as a consumer extracts it.
        foreach (var entry in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray())
        {
            Assert.DoesNotContain('\n', entry!["title"]!.GetValue<string>());
            var file = File.ReadAllText(Path.Combine(output, entry["body"]!.GetValue<string>())).Replace("\r\n", "\n", StringComparison.Ordinal);
            var afterCard = file[(file.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..].TrimStart('\n');
            var rowText = afterCard[(afterCard.IndexOf('\n', StringComparison.Ordinal) + 1)..].Split("<!-- atlas:generated -->")[0].Trim();
            Assert.Equal(entry["bodySha256"]!.GetValue<string>(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rowText))));
        }

        // Containment stated from the parent's end (a row that lists its sub-item) gives the sub-item its parent.
        var catalogue = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray();
        var theme = catalogue.Single(i => i!["title"]!.GetValue<string>().StartsWith("The theme of the quarter", StringComparison.Ordinal))!;
        var held = catalogue.Single(i => i!["key"]!.GetValue<string>() == "BL-2")!;
        Assert.Equal(theme["key"]!.GetValue<string>(), held["parent"]!.GetValue<string>());
        Assert.Equal("judge", held["confidence"]!["parent"]!["by"]!.GetValue<string>());
        Assert.Null(theme["parent"]);
        Assert.Equal("BL-1", tidyJson["derivedFrom"]!.AsArray().Single()!["key"]!.GetValue<string>());

        // The row closes: it moves from the list under Next to the table under Done. It is the same item, with its key and number.
        File.WriteAllText(backlog, File.ReadAllText(backlog)
            .Replace("3. Start the pilot outreach\n", "", StringComparison.Ordinal)
            .Replace("| 1 | Earlier work | DONE 2026-10-01 |\n", "| 1 | Earlier work | DONE 2026-10-01 |\n| 2 | Start the pilot outreach | DONE 2026-11-02 |\n", StringComparison.Ordinal));
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, again.Outcome);
        using var after = RunState.Open(Path.Combine(output, "state.sqlite"));
        var second = ItemCatalogue.Load(after);
        var moved = Assert.Single(second, i => i.Title == "Start the pilot outreach");
        Assert.Equal(outreachKey, moved.Key);
        Assert.Equal(outreachNumber, moved.Number);
        Assert.Equal("Done", moved.Section);
        Assert.Equal("done", moved.Status);
        Assert.DoesNotContain(second, i => i.SourceGone);
    }

    [Fact]
    public async Task RowsWithoutAnIdInAnyScriptSameTitledRowsAndRestartedNumbersEachBecomeAnItem()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        File.AppendAllText(Path.Combine(input, "docs", "backlog.md"),
            "\n## More\n\n| ID | Item | Status |\n|---|---|---|\n" +
            "|  | راه‌اندازی سرور پشتیبان | OPEN |\n" +
            "|  | 修复登录页面的错误 | OPEN |\n" +
            "|  | Same title | OPEN |\n" +
            "|  | Same title | SUPERVISED |\n" +
            "| 1 | Numbered one | OPEN |\n" +
            "| 2 | Numbered two | OPEN |\n" +
            "\n## Again\n\n| ID | Item | Status |\n|---|---|---|\n" +
            "| 1 | Second table one | OPEN |\n" +
            "| 2 | Second table two | OPEN |\n");
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        List<string> keys;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var items = ItemCatalogue.Load(state);

            // No row is lost and none is doubled, whatever the chunks' overlap repeated.
            foreach (var title in new[] { "راه‌اندازی سرور پشتیبان", "修复登录页面的错误", "Numbered one", "Numbered two", "Second table one", "Second table two" })
            {
                Assert.Single(items, i => i.Title == title);
            }

            Assert.Equal(2, items.Count(i => i.Title == "Same title"));
            Assert.Equal(["OPEN", "SUPERVISED"], items.Where(i => i.Title == "Same title").Select(i => i.StatusAsWritten).Order());
            Assert.Equal(items.Count, items.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // A title in another script keeps a slug of its own; every item has a row text.
            var persian = items.Single(i => i.Title == "راه‌اندازی سرور پشتیبان");
            var chinese = items.Single(i => i.Title == "修复登录页面的错误");
            Assert.NotEqual(persian.Key, chinese.Key);
            Assert.Contains("修复登录页面的错误", chinese.Key, StringComparison.Ordinal);
            Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(ItemCatalogue.RowText(i)), i.Key + " has an empty row text"));
            keys = items.Select(i => i.Key).Order(StringComparer.Ordinal).ToList();
        }

        // The keys hold on a second run of the same state: none drifts, none is left behind as a ghost.
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, again.Outcome);
        using var after = RunState.Open(Path.Combine(output, "state.sqlite"));
        var second = ItemCatalogue.Load(after);
        Assert.Equal(keys, second.Select(i => i.Key).Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(second, i => i.SourceGone);
    }

    [Fact]
    public async Task ItemsRunOfflineMakesTheCatalogueAndTheAtlasAndResumes()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        var (options, services, reader, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);

        // The status words, the questions and the items as JSON, for a consumer (docs/bundle-format.md, version 2).
        var words = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "status-words.json")))!;
        Assert.Contains(words["words"]!.AsArray(), m => m!["word"]!.GetValue<string>() == "DONE" && m["mapped"]!.GetValue<string>() == "done" && m["questionKey"] is null);
        Assert.Contains(words["words"]!.AsArray(), m => m!["word"]!.GetValue<string>() == "SUPERVISED" && m["questionKey"]!.GetValue<string>() == "status:SUPERVISED" && m["itemKeys"]!.AsArray().Count == 1);
        var questionsJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray();
        var statusQuestion = Assert.Single(questionsJson, q => q!["key"]!.GetValue<string>() == "status:SUPERVISED");
        Assert.Equal("status-word", statusQuestion!["about"]!["kind"]!.GetValue<string>());
        Assert.Contains("BL-2", statusQuestion["about"]!["itemKeys"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Null(statusQuestion["answer"]);
        var itemsJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray();
        var bugJson = Assert.Single(itemsJson, i => i!["key"]!.GetValue<string>() == "BL-2");
        Assert.Equal("bug", bugJson!["kind"]!.GetValue<string>());
        Assert.Null(bugJson["level"]);
        Assert.Equal("BL-1", bugJson["blockedBy"]!.AsArray().Single()!["key"]!.GetValue<string>());
        Assert.Empty(bugJson["personBlockers"]!.AsArray());
        Assert.Contains("<!-- atlas:generated -->", secondFileText(output), StringComparison.Ordinal);
        foreach (var entry in itemsJson)
        {
            // Never an empty row text: what sits between the heading and the generated marker is hashed and is not blank (change 1 of the verdict).
            var body = File.ReadAllText(Path.Combine(output, entry!["body"]!.GetValue<string>())).Replace("\r\n", "\n", StringComparison.Ordinal);
            var afterCard = body[(body.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..].TrimStart('\n');
            Assert.StartsWith("# ", afterCard, StringComparison.Ordinal);
            var rowText = afterCard[(afterCard.IndexOf('\n', StringComparison.Ordinal) + 1)..];
            rowText = rowText[..rowText.IndexOf("<!-- atlas:generated -->", StringComparison.Ordinal)].Trim();
            Assert.False(string.IsNullOrWhiteSpace(rowText), entry["key"]!.GetValue<string>() + " has an empty row text");
            Assert.Equal(entry["bodySha256"]!.GetValue<string>(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rowText))));
        }

        static string secondFileText(string folder) => File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "items"), "0002-*.md").Single());
        Assert.Equal(64, bugJson["bodySha256"]!.GetValue<string>().Length);
        Assert.Equal("judge", bugJson["confidence"]!["kind"]!["by"]!.GetValue<string>());
        var doneJson = Assert.Single(itemsJson, i => i!["key"]!.GetValue<string>() == "BL-3");
        Assert.Equal("completed", doneJson!["resolution"]!.GetValue<string>());
        Assert.Null(doneJson["conditionKind"]);
        Assert.Equal(2, reader.ItemsCalls);

        var itemsFolder = Path.Combine(output, "items");
        Assert.Equal(5, Directory.GetFiles(itemsFolder, "*.md").Length);
        Assert.Single(Directory.GetFiles(itemsFolder, "0001-*.md"));
        Assert.Single(Directory.GetFiles(itemsFolder, "0002-second-thing-*.md"));
        Assert.True(File.Exists(Path.Combine(itemsFolder, "0003-third-thing.md")));
        Assert.Single(Directory.GetFiles(itemsFolder, "0009-*.md"));
        Assert.True(File.Exists(Path.Combine(itemsFolder, "0010-fourth-thing-part-b.md")));

        List<WorkItem> items;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            items = ItemCatalogue.Load(state);
            Assert.Equal(2, state.UnitsOf("items").Count(u => u.Key.StartsWith("rows/", StringComparison.Ordinal)));
            Assert.True(state.UnitsOf("items").Count(u => u.Key.StartsWith("status-check/", StringComparison.Ordinal)) >= 1, "the chunk with SUPERVISED needs the judge; DONE is known by rule");
        }

        Assert.Equal(5, items.Count);
        var first = Assert.Single(items, i => i.Key == "BL-1");
        Assert.Equal((1, "backlog", DecidedBy.Reader, "OPEN"), (first.Number, first.Status, first.StatusBy, first.StatusAsWritten));
        Assert.Contains(first.References, r => r.Kind == "id" && r.Text == "BL-9" && r.ResolvedKey == "BL-9");
        Assert.Contains(first.References, r => r.Kind == "path" && r.Text == "docs/specs/BL-1-first.md");
        Assert.Equal("docs/backlog.md", first.Sources[0].Path);
        Assert.Equal(9, first.Sources[0].Line);

        var second = Assert.Single(items, i => i.Key == "BL-2");
        Assert.Equal(("backlog", DecidedBy.Judge), (second.Status, second.StatusBy));
        Assert.Contains("status-by-judge", second.Flags);

        var third = Assert.Single(items, i => i.Key == "BL-3");
        Assert.Equal(("done", DecidedBy.Rule, "finished-ledger", "DONE 2026-10-01"), (third.Status, third.StatusBy, third.Sources[0].LedgerKind, third.StatusAsWritten));

        var ninth = Assert.Single(items, i => i.Key == "BL-9");
        Assert.True(ninth.Placeholder);
        Assert.Equal((9, "unknown"), (ninth.Number, ninth.Status));

        // The kind and the relations from the row's own words, confirmed by the judge, with their figures.
        var bugItem = Assert.Single(items, i => i.Key == "BL-2");
        Assert.Equal("bug", bugItem.Type);
        Assert.Equal(DecidedBy.Judge, bugItem.TypeBy);
        Assert.Equal(0.9, bugItem.TypeConfidence);
        var blocker = Assert.Single(bugItem.Relations);
        Assert.Equal("blocker", blocker.Kind);
        Assert.Equal("BL-1", blocker.ResolvedKey);
        Assert.Equal(DecidedBy.Judge, blocker.By);
        Assert.Contains("BL-1", bugItem.Blockers);
        Assert.Null(bugItem.Parent);
        var secondFile = File.ReadAllText(Directory.GetFiles(Path.Combine(output, "items"), "0002-*.md").Single());
        Assert.Contains("type: \"bug\"", secondFile, StringComparison.Ordinal);
        Assert.Contains("blockers: [{ item: 1, key: \"BL-1\" }]", secondFile, StringComparison.Ordinal);

        var suffixed = Assert.Single(items, i => i.Key == "BL-4b");
        Assert.Equal(10, suffixed.Number);
        Assert.Contains("BL-4b", suffixed.Aliases);

        var firstFile = File.ReadAllText(Directory.GetFiles(itemsFolder, "0001-*.md").Single());
        Assert.Contains("status: backlog", firstFile, StringComparison.Ordinal);
        Assert.Contains("[[0009-", firstFile, StringComparison.Ordinal);
        Assert.Contains("# BL-1: First thing", firstFile, StringComparison.Ordinal);

        var questions = File.ReadAllText(Path.Combine(output, "questions.md"));
        Assert.Contains("## status:SUPERVISED", questions, StringComparison.Ordinal);
        Assert.DoesNotContain("## status:DONE", questions, StringComparison.Ordinal); // a plain word the engine's own table maps asks no question
        Assert.DoesNotContain("## status:DONE 2026", questions, StringComparison.Ordinal);
        Assert.Contains("## prose-work:docs/backlog.md#deferred-features", questions, StringComparison.Ordinal);
        Assert.Contains("## placeholders:backlog id", questions, StringComparison.Ordinal);
        var summary = File.ReadAllText(Path.Combine(output, "summary.md"));
        Assert.Contains("## Items", summary, StringComparison.Ordinal);

        // The summary names each status word once, with its rows and who mapped it; the phrases stay in the log.
        Assert.Contains("- Status words (", summary, StringComparison.Ordinal);
        Assert.Contains("DONE to done", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DONE 2026-10-01 to", summary, StringComparison.Ordinal);
        Assert.Contains("Items 5 (1 placeholders", File.ReadAllText(Path.Combine(output, "state.md")), StringComparison.Ordinal);

        // A person answers: the status word is confirmed with its condition, and the prose list becomes items.
        File.WriteAllText(Path.Combine(output, "answers.md"),
            "# Answers\n\n## status:SUPERVISED\ndeferred, only with the owner\n\n## prose-work:docs/backlog.md#deferred-features\nitems\n");
        var second2 = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second2.Outcome);
        Assert.Equal(2, reader.ItemsCalls);

        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            items = ItemCatalogue.Load(state);
        }

        Assert.Equal(7, items.Count);
        var supervised = Assert.Single(items, i => i.Key == "BL-2");
        Assert.Equal(("deferred", DecidedBy.Person, "only with the owner"), (supervised.Status, supervised.StatusBy, supervised.Condition));
        var fromProse = items.Where(i => i.Flags.Contains("from-prose")).OrderBy(i => i.Number).ToList();
        Assert.Equal(2, fromProse.Count);
        Assert.Equal([11, 12], fromProse.Select(i => i.Number));
        Assert.All(fromProse, i => Assert.Equal("deferred", i.Status));
        Assert.Equal(7, Directory.GetFiles(itemsFolder, "*.md").Length);
        var questionsAfter = File.ReadAllText(Path.Combine(output, "questions.md"));
        Assert.DoesNotContain("## status:SUPERVISED", questionsAfter, StringComparison.Ordinal);
        Assert.Contains("resumed from the state", File.ReadAllText(Path.Combine(output, "log.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowsTheReaderMissedAreFilledAndTheLedgersWordsRule()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        File.AppendAllText(Path.Combine(input, "docs", "backlog.md"), "| BL-8 | Eighth thing | DEFERRED |\n");
        File.AppendAllText(Path.Combine(input, "docs", "backlog-done.md"), "| BL-9 | Ninth thing | ACCEPTED |\n");
        ScriptedChatClient.SkipRowsWith = "Third";
        try
        {
            var (options, services, _, _) = Setup(input, output);
            var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
            Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);

            using var state = RunState.Open(Path.Combine(output, "state.sqlite"));
            var items = ItemCatalogue.Load(state).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

            // The reader left the third row out; code found it in the table and filled it by rule.
            Assert.True(items.ContainsKey("BL-3"), "the missed row is filled from the table");
            Assert.Contains("row-by-rule", items["BL-3"].Flags);
            Assert.Equal("done", items["BL-3"].Status);

            // DEFERRED is deferred by the engine's own word table; ACCEPTED in the finished ledger is closed by the ledger.
            Assert.Equal("deferred", items["BL-8"].Status);
            Assert.Equal("done", items["BL-9"].Status);
            Assert.Contains("status-by-ledger", items["BL-9"].Flags);
            Assert.Equal("ACCEPTED", items["BL-9"].Condition);
            Assert.Contains("rows-filled-by-rule", File.ReadAllText(Path.Combine(output, "log.md")), StringComparison.Ordinal);
        }
        finally
        {
            ScriptedChatClient.SkipRowsWith = null;
        }
    }

    [Fact]
    public void AnIdAnotherNameQualifiesBelongsElsewhere()
    {
        Assert.True(ItemsPhase.IsQualifiedByAnotherName("infra BL-144"));
        Assert.True(ItemsPhase.IsQualifiedByAnotherName("infra's D35"));
        Assert.False(ItemsPhase.IsQualifiedByAnotherName("see BL-12"));
        Assert.False(ItemsPhase.IsQualifiedByAnotherName("BL-12"));
        Assert.False(ItemsPhase.IsQualifiedByAnotherName("D061"));
    }
}
