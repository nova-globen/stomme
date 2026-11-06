using System.Diagnostics;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

public sealed class DecisionsRunTests
{
    /// <summary>The synthetic repository plus an ADR, a handover that notes four decisions of different standing, and one commit whose message reads like a decision.</summary>
    private static void WriteRepository(string input)
    {
        SyntheticRepository.Write(input);
        Directory.CreateDirectory(Path.Combine(input, "docs", "adr"));
        Directory.CreateDirectory(Path.Combine(input, "docs", "reports"));
        File.WriteAllText(Path.Combine(input, "docs", "adr", "0001-storage.md"),
            "# 0001: Storage\n\n- Status: accepted\n- Date: 2026-09-01\n\nWe decided to use PostgreSQL for storage because it is boring.\n");
        File.WriteAllText(Path.Combine(input, "docs", "reports", "handover.md"),
            "# Handover 2026-10-02\n\nWe decided to use PostgreSQL for storage, as the ADR says.\n" +
            "We decided to keep every timestamp in UTC across the services.\n" +
            "We decided that every timestamp stays in UTC across all the services.\n" +
            "We decided we might move to Kubernetes later.\n" +
            "We decided to rename the button label to Save.\n" +
            "We decided, contrary to 0001, to move storage to SQLite for the tests.\n" +
            "We decided to keep the storage of 0001 and add a cache in front of it.\n");
        Git(input, "init -q");
        Git(input, "add .");
        Git(input, "-c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false commit -q -m \"feat(state): switch to SQLite for the run state instead of JSON files\"");
    }

    private static void Git(string root, string arguments)
    {
        var start = new ProcessStartInfo("git", arguments) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments}: {process.StandardError.ReadToEnd()}");
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
                StopAfterPhase = "decisions",
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

    /// <summary>A repository that ships examples with documents and ADRs of their own (the redo's change 13).</summary>
    [Fact]
    public async Task ADecisionOnlyAnExampleHoldsIsNeitherRecordedNorDraftedAndItsAdrIsNotTheRepositorys()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(Path.Combine(input, "examples", "sample", "docs", "adr"));
        File.WriteAllText(Path.Combine(input, "examples", "sample", "docs", "adr", "0001-queue.md"),
            "# 0001: Queue\n\n- Status: accepted\n\nWe decided to use RabbitMQ for the queue of the sample.\n");
        File.WriteAllText(Path.Combine(input, "examples", "sample", "notes.md"),
            "# Notes of the sample\n\nWe decided to ship a fake clock in every service of the sample.\n");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var cards = DecisionCatalogue.Load(state);
            foreach (var words in new[] { "RabbitMQ", "fake clock" })
            {
                var card = cards.Single(c => c.Text.Contains(words, StringComparison.Ordinal));
                Assert.Equal("record", card.Standing);
                Assert.Contains("from-fixture", card.Flags);
                Assert.Null(card.RecordedIn);
                Assert.Null(card.Draft);
                Assert.Equal(DecidedBy.Rule, card.By);
            }

            // The repository's own ADR is still the head 0001: the example's ADR of the same number takes nothing from it.
            var own = cards.Single(c => c.Text.Contains("because it is boring", StringComparison.Ordinal));
            Assert.Equal(("recorded", "docs/adr/0001-storage.md"), (own.Standing, own.RecordedIn));
            Assert.DoesNotContain(cards, c => c.RecordedIn is not null && c.RecordedIn.StartsWith("examples/", StringComparison.Ordinal));
            Assert.DoesNotContain("from-fixture", own.Flags);
        }

        var documents = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "documents.json")))!["documents"]!.AsArray();
        Assert.True(documents.Single(d => d!["path"]!.GetValue<string>() == "examples/sample/notes.md")!["fixture"]!.GetValue<bool>());
        Assert.True(documents.Single(d => d!["path"]!.GetValue<string>() == "examples/sample/docs/adr/0001-queue.md")!["fixture"]!.GetValue<bool>());
        Assert.False(documents.Single(d => d!["path"]!.GetValue<string>() == "docs/adr/0001-storage.md")!["fixture"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DecisionsRunOfflineDraftsTheSignificantOnesAndKeepsTheRest()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, reader, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);

        using var state = RunState.Open(Path.Combine(output, "state.sqlite"));
        var cards = DecisionCatalogue.Load(state);
        var byText = cards.ToDictionary(c => c.Text, StringComparer.Ordinal);

        // The ADR's own decision is recorded by rule; the handover's mention of it is recorded by the judge against that ADR.
        var own = cards.Single(c => c.Text.Contains("because it is boring", StringComparison.Ordinal));
        Assert.Equal("recorded", own.Standing);
        Assert.Equal("docs/adr/0001-storage.md", own.RecordedIn);
        Assert.Equal(DecidedBy.Rule, own.By);
        var mention = cards.Single(c => c.Text.Contains("as the ADR says", StringComparison.Ordinal));
        Assert.Equal("recorded", mention.Standing);
        Assert.Equal("docs/adr/0001-storage.md", mention.RecordedIn);
        Assert.Equal(DecidedBy.Judge, mention.By);

        // A plan is not a decision; a label is routine; the timestamp rule and the commit are drafted.
        Assert.Equal("not-a-decision", cards.Single(c => c.Text.Contains("Kubernetes", StringComparison.Ordinal)).Standing);
        Assert.Equal("record", cards.Single(c => c.Text.Contains("button label", StringComparison.Ordinal)).Standing);
        var utc = cards.Single(c => c.Text.Contains("UTC", StringComparison.Ordinal) && c.Standing == "draft");
        Assert.NotNull(utc.Number);
        Assert.NotNull(utc.Draft);

        // The same rule in other words (alike by half of its words, not four fifths): one draft, the other merged into it with its source (TR-27, TR-36).
        var merged = cards.Single(c => c.Text.Contains("UTC", StringComparison.Ordinal) && c.Standing == "merged");
        Assert.Contains("merged-into:" + utc.Key, merged.Flags);
        Assert.Null(merged.Number);
        Assert.True(utc.Sources.Count >= 2, "the lead carries the merged one's source");
        Assert.Equal("2026-10-02", utc.Date);
        var commit = cards.Single(c => c.Text.Contains("switch to SQLite", StringComparison.Ordinal));
        Assert.Equal("draft", commit.Standing);
        Assert.Equal("commit", commit.Sources[0].Kind);

        // A citation of the repository's ADR: a correction attaches to it; a decision that only refers to it is drafted on its own.
        var correction = cards.Single(c => c.Text.Contains("contrary to 0001", StringComparison.Ordinal));
        Assert.Equal("recorded", correction.Standing);
        Assert.Equal("docs/adr/0001-storage.md", correction.RecordedIn);
        Assert.Contains("correction-candidate", correction.Flags);
        var refers = cards.Single(c => c.Text.Contains("add a cache", StringComparison.Ordinal));
        Assert.Equal("draft", refers.Standing);
        Assert.Contains("cites-adr", refers.Flags);
        Assert.DoesNotContain("correction-candidate", refers.Flags);
        Assert.Equal(3, reader.DecisionCalls);

        var folder = Path.Combine(output, "decisions");
        var first = File.ReadAllText(Path.Combine(folder, DecisionCatalogue.FileName(utc)));
        Assert.Contains("status: proposed", first, StringComparison.Ordinal);
        Assert.Contains("drafted_by: survey", first, StringComparison.Ordinal);
        Assert.Contains("[[documents/docs/reports/handover]]", first, StringComparison.Ordinal);
        Assert.Contains("every timestamp in UTC", first, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "ADR-draft-003.md")));
        Assert.False(File.Exists(Path.Combine(folder, "ADR-draft-004.md")));
        var index = File.ReadAllText(Path.Combine(folder, "index.md"));
        Assert.Contains("| recorded |", index, StringComparison.Ordinal);
        Assert.Contains("| not-a-decision |", index, StringComparison.Ordinal);
        var summary = File.ReadAllText(Path.Combine(output, "summary.md"));
        Assert.Contains("## Decisions", summary, StringComparison.Ordinal);
        Assert.Contains("Drafts in `decisions/`: 3 (ADR-draft-001 to ADR-draft-003)", summary, StringComparison.Ordinal);
        Assert.Contains("no gap seen", summary, StringComparison.Ordinal);

        // A second run finds everything in the state: no draft is written again, and the numbers hold.
        var again = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, again.Outcome);
        Assert.Equal(3, reader.DecisionCalls);
        using var reopened = RunState.Open(Path.Combine(output, "state.sqlite"));
        Assert.Equal(utc.Number, DecisionCatalogue.Load(reopened).Single(c => c.Text.Contains("UTC", StringComparison.Ordinal) && c.Standing == "draft").Number);
    }

    [Fact]
    public void ACommitMessageIsACandidateOnlyWhenItReadsLikeADecision()
    {
        string[] words = ["instead of", "decided", "switch to"];
        Assert.True(DecisionsPhase.LooksLikeDecision("feat: switch to SQLite instead of JSON", words));
        Assert.True(DecisionsPhase.LooksLikeDecision("We decided.", words));
        Assert.False(DecisionsPhase.LooksLikeDecision("fix: a typo in the README", words));
        Assert.False(DecisionsPhase.LooksLikeDecision("undecided yet", words));
    }

    [Fact]
    public void TheSameDecisionNotedTwiceHasOneKey()
    {
        Assert.Equal(DecisionCatalogue.KeyOf("We decided to keep UTC!"), DecisionCatalogue.KeyOf("we decided to keep utc"));
        Assert.NotEqual(DecisionCatalogue.KeyOf("We decided to keep UTC"), DecisionCatalogue.KeyOf("We decided to drop UTC"));
        var a = DecisionCatalogue.Words("Handlers are registered explicitly rather than via AddMediator");
        var b = DecisionCatalogue.Words("Handlers are registered explicitly rather than through AddMediator");
        Assert.True(DecisionCatalogue.Similarity(a, b) > 0.7);
    }
}
