using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

public sealed class VerifyRunTests
{
    /// <summary>The synthetic repository plus a spec, a roadmap naming items and a path that names no file.</summary>
    private static void WriteRepository(string input)
    {
        SyntheticRepository.Write(input);
        File.WriteAllText(Path.Combine(input, "docs", "roadmap.md"), "# Roadmap\n\nFirst BL-1, then BL-2. See docs/specs/BL-1-first.md and docs/specs/missing.md.\n");

        // A docx is a zip: bytes no decoder reads yet. It is never a document and its bytes are never a body (change 6 of the verdict).
        File.WriteAllBytes(Path.Combine(input, "docs", "old-report.docx"), [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x21, 0x00]);
    }

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
                StopAfterPhase = "verify",
                Reader = new ReaderOptions { Retries = 0 },
            },
            Budgets = new SurveyBudgets { ReaderChunkTokens = 600, ChunkOverlapLines = 3, DocumentChunkTokens = 600, DocumentOverlapChars = 400 },
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

    /// <summary>What a repository names and this copy does not hold is told to a person (the redo's change 12), on shapes any repository can have.</summary>
    [Fact]
    public async Task ANamedLedgerThatIsMissingAndATrackerOutsideEachBecomeAQuestionAndAFinding()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        File.AppendAllText(Path.Combine(input, "README.md"),
            "\nOlder work is recorded in docs/archive/work-2025.md. Report bugs at https://github.com/example/demo/issues, and see https://github.com/other/project/issues/41 for the fix upstream.\n");
        var (options, services, reader, _) = Setup(input, output);
        reader.MissingLedger = "docs/archive/work-2025.md";
        reader.NatureNote = "The commits say the remaining work is tracked in the backlog of another repository.";

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var questions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray();
        var missing = Assert.Single(questions, q => q!["key"]!.GetValue<string>() == "ledger-missing:docs/archive/work-2025.md")!;
        Assert.Contains("README.md:", missing["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("not-in-this-copy", missing["default"]!.GetValue<string>());
        Assert.Equal("ledger", missing["about"]!["kind"]!.GetValue<string>());
        var elsewhere = Assert.Single(questions, q => q!["key"]!.GetValue<string>() == "work-elsewhere")!["text"]!.GetValue<string>();
        Assert.Contains("https://github.com/example/demo/issues", elsewhere, StringComparison.Ordinal);
        Assert.Contains("tracked in the backlog of another repository", elsewhere, StringComparison.Ordinal);
        Assert.DoesNotContain("issues/41", elsewhere, StringComparison.Ordinal);

        // A path only the reader named, in no guide, is its guess: no question, no finding.
        Assert.DoesNotContain(questions, q => q!["key"]!.GetValue<string>().Contains("docs/missing.md", StringComparison.Ordinal));

        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var verified = VerifyPhase.Load(state)!;
            Assert.Contains(verified.Findings, f => f.StartsWith("1 ledger(s) the guides name that are not in the input", StringComparison.Ordinal) && f.Contains("docs/archive/work-2025.md", StringComparison.Ordinal));
            Assert.Contains(verified.Findings, f => f.StartsWith("2 place(s) outside the repository", StringComparison.Ordinal));
        }

        var summary = File.ReadAllText(Path.Combine(output, "summary.md"));
        Assert.Contains("docs/archive/work-2025.md", summary, StringComparison.Ordinal);

        // A person answers: the guide is out of date, and nothing kept there belongs here. Neither is a finding then.
        File.WriteAllText(Path.Combine(output, "answers.json"),
            "{ \"ledger-missing:docs/archive/work-2025.md\": { \"text\": \"that file was folded into the backlog\", \"option\": \"guide-outdated\", \"by\": \"person\" }, \"work-elsewhere\": { \"text\": \"only users' reports\", \"option\": \"nothing-there\", \"by\": \"person\" } }");
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, again.Outcome);
        using var after = RunState.Open(Path.Combine(output, "state.sqlite"));
        var second = VerifyPhase.Load(after)!;
        Assert.DoesNotContain(second.Findings, f => f.Contains("ledger(s) the guides name", StringComparison.Ordinal) || f.Contains("outside the repository", StringComparison.Ordinal));
    }

    /// <summary>
    /// A default is an option's value, never a sentence (change 9 of the verdict), for a question an earlier engine
    /// left in the state too: it is written with the option it names, the sentence is kept as the reason, and a
    /// default that is no option would be a finding of the verify phase.
    /// </summary>
    [Fact]
    public async Task AQuestionKeptFromAnEarlierRunWithASentenceAsItsDefaultIsWrittenWithTheOptionItNames()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, output);
        var first = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, first.Outcome);

        // What a run of an earlier engine left behind: the reader's sentence as the default of a question that offers kinds.
        const string sentence = "other (the file is kept as a document of kind other)";
        QuestionOption[] kinds = [new("guide", "a guide"), new("plan", "a plan"), new("other", "any other document")];
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            state.UpsertQuestion(new Question("kind:notes/kept-from-before.txt", "orientation", "What kind of document is notes/kept-from-before.txt?", kinds, sentence), "an-earlier-run", null);
            state.UpsertQuestion(new Question("kind:notes/another-from-before.txt", "orientation", "What kind of document is notes/another-from-before.txt?", kinds, "it could be a guide or a plan"), "an-earlier-run", null);
        }

        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, second.Outcome);
        var questions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray();
        var kept = Assert.Single(questions, q => q!["key"]!.GetValue<string>() == "kind:notes/kept-from-before.txt")!;
        Assert.Equal("other", kept["default"]!.GetValue<string>());
        Assert.Contains(sentence, kept["defaultReason"]!.GetValue<string>(), StringComparison.Ordinal);
        var unclear = Assert.Single(questions, q => q!["key"]!.GetValue<string>() == "kind:notes/another-from-before.txt")!;
        Assert.Equal("other", unclear["default"]!.GetValue<string>());
        Assert.Contains("it could be a guide or a plan", unclear["defaultReason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(questions, q => Assert.True(q!["options"]!.AsArray().Count == 0 || q["options"]!.AsArray().Any(o => o!["value"]!.GetValue<string>() == q["default"]!.GetValue<string>())));
        Assert.DoesNotContain("Default if unanswered: " + sentence, File.ReadAllText(Path.Combine(output, "questions.md")), StringComparison.Ordinal);

        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            Assert.Empty(VerifyPhase.Load(state)!.DefaultsNotOptions);
            Assert.DoesNotContain(VerifyPhase.Load(state)!.Findings, f => f.Contains("default is not one of their options", StringComparison.Ordinal));
        }

        // The check itself: one that got through would be named, with what it holds.
        var through = VerifyPhase.DefaultsThatAreNoOption([new Question("kind:x", "orientation", "?", kinds, sentence), new Question("kind:y", "orientation", "?", kinds, "plan"), new Question("free", "decisions", "?", [], "a sentence is fine where no option is offered")]);
        Assert.Equal("kind:x (\"other (the file is kept as a document of kind other)\")", Assert.Single(through));
        Assert.Equal("other", RepositorySurvey.SafestOption(new Question("kind:x", "orientation", "?", kinds, sentence)));
    }

    [Fact]
    public async Task VerifyRunOfflineAccountsForEveryFileRowAndReferenceAndJudgesASample()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, _, judge) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, result.Outcome);

        // The complete mark: verify is the last phase with code, so the bundle is whole and says so.
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        Assert.True(manifest["complete"]!.GetValue<bool>());
        Assert.Equal(2, manifest["atlasVersion"]!.GetValue<int>());
        Assert.Empty(manifest["incomplete"]!.AsArray());
        Assert.Contains("code-survey", manifest["phases"]!["notBuilt"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("verify", manifest["phases"]!["done"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("items.json", manifest["catalogues"]!["items"]!.GetValue<string>());
        Assert.Contains("Bundle: **complete**", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);

        using var state = RunState.Open(Path.Combine(output, "state.sqlite"));
        var verified = VerifyPhase.Load(state);
        Assert.NotNull(verified);

        // Every file in exactly one account: the two ledgers, the documents, the code file, the image.
        var files = Directory.GetFiles(input, "*", SearchOption.AllDirectories).Length;
        Assert.Equal(files, verified.FilesByAccount.Values.Sum());
        Assert.Equal(2, verified.FilesByAccount.Single(a => a.Key.StartsWith("ledger", StringComparison.Ordinal)).Value);
        Assert.Equal(1, verified.FilesByAccount.Single(a => a.Key.StartsWith("code", StringComparison.Ordinal)).Value);
        Assert.Empty(verified.FilesUnaccounted);
        Assert.Empty(verified.DocumentsMissing);
        Assert.Empty(verified.DocumentsConflicting);
        Assert.Equal(1, verified.FilesByAccount.Single(a => a.Key.StartsWith("document-like, unreadable", StringComparison.Ordinal)).Value);
        var documentsJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "documents.json")))!["documents"]!.AsArray();
        Assert.DoesNotContain(documentsJson, d => d!["path"]!.GetValue<string>().EndsWith(".docx", StringComparison.Ordinal));
        Assert.All(documentsJson, d => Assert.False(string.IsNullOrWhiteSpace(d!["title"]?.GetValue<string>()), d["path"]!.GetValue<string>() + " has no title"));
        Assert.False(File.Exists(Path.Combine(output, "documents", "docs", "old-report.docx.md")));

        // Every ledger row one item: BL-1, BL-2, BL-4b in the open ledger, BL-3 in the finished one; the placeholder BL-9 is no row.
        Assert.Equal(4, verified.Rows);
        Assert.Equal(4, verified.RowsWithOneItem);
        Assert.Empty(verified.RowsWithoutItem);
        Assert.Empty(verified.RowsWithTwoItems);
        Assert.Empty(verified.ItemsWithoutRow);
        Assert.Empty(verified.RowsTextMissing);

        // References: ids to items, the spec's path to a document, the missing path counted with its reason.
        Assert.True(verified.ReferencesResolved.GetValueOrDefault("id to an item") >= 2, "ids resolved: " + string.Join(", ", verified.ReferencesResolved));
        Assert.True(verified.ReferencesResolved.GetValueOrDefault("path to a document") >= 1);
        Assert.True(verified.ReferencesUnresolved.GetValueOrDefault("a path naming no file in the repository") >= 1, "unresolved: " + string.Join(", ", verified.ReferencesUnresolved));

        // The contract of questions.json: a question that offers options has one of them as its default, never a sentence.
        var questionsJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "questions.json")))!["questions"]!.AsArray();
        Assert.NotEmpty(questionsJson);
        Assert.All(questionsJson.Where(q => q!["options"]!.AsArray().Count > 0), q =>
            Assert.Contains(q!["default"]!.GetValue<string>(), q["options"]!.AsArray().Select(o => o!["value"]!.GetValue<string>())));
        Assert.All(questionsJson, q => Assert.NotNull(q!["about"]));

        // The counts reconcile; the judged samples were asked and answered.
        Assert.DoesNotContain(verified.Counts, c => c.EndsWith("DIFFER", StringComparison.Ordinal));
        Assert.Equal(4, verified.Items.Asked);
        Assert.True(verified.Items.Judged);
        Assert.True(verified.Items.Yes >= 6, "item answers: " + string.Join("; ", verified.Items.Doubts));
        Assert.True(verified.Links.Asked >= 1);
        Assert.True(verified.Links.Judged);
        Assert.Equal(0, verified.Links.No);
        Assert.True(verified.Passed, string.Join("; ", verified.Findings));

        // The result is in the summary and in state.md; the units are in the state.
        var summary = File.ReadAllText(Path.Combine(output, "summary.md"));
        Assert.Contains("## Verification", summary, StringComparison.Ordinal);
        Assert.Contains("**Passed**", summary, StringComparison.Ordinal);
        Assert.Contains("Verified (run ", File.ReadAllText(Path.Combine(output, "state.md")), StringComparison.Ordinal);
        Assert.Equal(2, state.UnitsOf("verify").Count);

        // A second run answers the samples from the state: no judge call for them.
        var calls = judge.Calls;
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, again.Outcome);
        Assert.Equal(calls, judge.Calls);
    }

    [Fact]
    public async Task VerifyFindsARowThatBecameNoItem()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, output);
        await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);

        // A row added to the finished ledger after the items phase ran, with the items phase told to stand: the verify phase reads the file again.
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            state.ForgetUnits("verify");
        }

        File.AppendAllText(Path.Combine(input, "docs", "backlog-done.md"), "| BL-7 | Seventh thing | DONE 2026-10-03 |\n");
        var rerun = new SurveyOptions
        {
            InputPath = input,
            Conductor = new ConductorOptions
            {
                OutputPath = output,
                PlaybookPath = TestPaths.Playbook,
                StopAfterPhase = "verify",
                Reader = new ReaderOptions { Retries = 0 },
            },
            Budgets = new SurveyBudgets { ReaderChunkTokens = 600, ChunkOverlapLines = 3, DocumentChunkTokens = 600, DocumentOverlapChars = 400, VerifySampleItems = 0, VerifySampleLinks = 0 },
        };
        var result = await new RepositorySurvey(rerun, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        using var after = RunState.Open(Path.Combine(output, "state.sqlite"));
        var verified = VerifyPhase.Load(after);
        Assert.NotNull(verified);
        Assert.Equal(5, verified.Rows);
        Assert.Equal(0, verified.Items.Asked);

        // Either the items phase read the changed ledger and the row is an item, or it did not and the verify phase says so; never silence.
        Assert.True(verified.RowsWithOneItem == 5 || verified.RowsWithoutItem.Any(r => r.Contains("BL-7", StringComparison.Ordinal)), string.Join("; ", verified.RowsWithoutItem));
    }
}
