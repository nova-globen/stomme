using Microsoft.Data.Sqlite;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

public sealed class LinksRunTests
{
    /// <summary>The synthetic repository plus two ADRs (the first superseded by the second), a spec of BL-1, a roadmap that names items and the spec, and a report that decides something.</summary>
    private static void WriteRepository(string input)
    {
        SyntheticRepository.Write(input);
        Directory.CreateDirectory(Path.Combine(input, "docs", "adr"));
        Directory.CreateDirectory(Path.Combine(input, "docs", "reports"));
        File.WriteAllText(Path.Combine(input, "docs", "adr", "0001-old.md"), "# 0001: Old choice\n\n- Status: superseded by 0002\n\nWe decided to use X over Y.\n");
        File.WriteAllText(Path.Combine(input, "docs", "adr", "0002-new.md"), "# 0002: New choice\n\n- Status: accepted\n\nWe decided to use Y after all.\n");
        File.WriteAllText(Path.Combine(input, "docs", "roadmap.md"), "# Roadmap\n\nFirst BL-1, then BL-2. See docs/specs/BL-1-first.md and the image docs/mockup.png.\n");
        File.WriteAllText(Path.Combine(input, "docs", "reports", "handover.md"), "# Handover 2026-10-02\n\nWe decided to keep every timestamp in UTC across the services, see BL-1.\n");
        File.WriteAllBytes(Path.Combine(input, "docs", "mockup.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
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
                StopAfterPhase = "links",
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

    [Fact]
    public async Task LinksRunOfflineBuildsTheGraphWithItsProvenanceAndExports()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);

        using var state = RunState.Open(Path.Combine(output, "state.sqlite"));
        var graph = GraphCatalogue.Load(state);
        Assert.NotNull(graph);
        Assert.Contains(graph.Nodes, n => n.Id == "item:BL-1");
        Assert.Contains(graph.Nodes, n => n.Id == "document:docs/roadmap.md");

        // The spec carries BL-1 in its name: it specifies the item; the roadmap names BL-1 and the spec: references, by the rule.
        Assert.Contains(graph.Edges, e => e.From == "document:docs/specs/BL-1-first.md" && e.To == "item:BL-1" && e.Kind == "specifies" && e.By == DecidedBy.Rule);
        Assert.Contains(graph.Edges, e => e.From == "document:docs/roadmap.md" && e.To == "item:BL-1" && e.Kind == "references");
        Assert.Contains(graph.Edges, e => e.From == "document:docs/roadmap.md" && e.To == "document:docs/specs/BL-1-first.md" && e.Kind == "references");

        // The image the roadmap names is a design node that depicts it; the older ADR is superseded by the newer.
        Assert.Contains(graph.Edges, e => e.From == "design:docs/mockup.png" && e.To == "document:docs/roadmap.md" && e.Kind == "depicts");
        Assert.Contains(graph.Edges, e => e.From == "document:docs/adr/0002-new.md" && e.To == "document:docs/adr/0001-old.md" && e.Kind == "supersedes");

        // The decisions: the handover's is evidenced by the handover and decides BL-1; the ADRs' own are part of their ADR.
        var utc = DecisionCatalogue.Load(state).Single(d => d.Text.Contains("UTC", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, e => e.From == "document:docs/reports/handover.md" && e.To == GraphCatalogue.DecisionId(utc.Key) && e.Kind == "evidences");
        Assert.Contains(graph.Edges, e => e.From == GraphCatalogue.DecisionId(utc.Key) && e.To == "item:BL-1" && e.Kind == "decides");
        Assert.Contains(graph.Edges, e => e.To == "document:docs/adr/0002-new.md" && e.Kind == "part_of" && e.From.StartsWith("decision:", StringComparison.Ordinal));

        // The relation the row states: BL-1 blocks BL-2 (BL-2 is blocked by BL-1), read by the judge.
        Assert.Contains(graph.Edges, e => e.From == "item:BL-1" && e.To == "item:BL-2" && e.Kind == "blocks" && e.By == DecidedBy.Judge);

        // Tags from the reader, and the related_to edges from shared distinctive tags.
        Assert.Contains(graph.Nodes, n => n.Kind == "tag" && n.Key == "topic:demo");
        Assert.Contains(graph.Edges, e => e.Kind == "references" && e.To == "tag:topic:demo" && e.By == DecidedBy.Reader);
        Assert.Contains(graph.Edges, e => e.Kind == "related_to");

        // The files: graph.json, the index, the exports; the summary's section; the item's links.
        Assert.True(File.Exists(Path.Combine(output, "graph.json")));
        Assert.True(File.Exists(Path.Combine(output, "graph.graphml")));
        Assert.Contains("flowchart LR", File.ReadAllText(Path.Combine(output, "graph.mmd")), StringComparison.Ordinal);
        using (var index = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(output, "index.sqlite"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            index.Open();
            using var count = index.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edges WHERE kind = 'specifies'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }

        var summary = File.ReadAllText(Path.Combine(output, "summary.md"));
        Assert.Contains("## Links and the graph", summary, StringComparison.Ordinal);
        Assert.Contains("Bundle: **incomplete**", summary, StringComparison.Ordinal);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        Assert.False(manifest["complete"]!.GetValue<bool>());
        Assert.Contains("verify", manifest["phases"]!["notRun"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotEmpty(manifest["incomplete"]!.AsArray());
        var item = File.ReadAllText(Directory.GetFiles(Path.Combine(output, "items"), "0001-*.md").Single());
        Assert.Contains("## Links (survey)", item, StringComparison.Ordinal);
        Assert.Contains("<!-- atlas:links -->", item, StringComparison.Ordinal);
        Assert.EndsWith("<!-- /atlas:links -->\n\n", item.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("specifies: [[documents/docs/specs/BL-1-first]]", item, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathIsFoldedFromTheDocumentsFolder()
    {
        Assert.Equal("docs/specs/BL-1-first.md", LinksPhase.Fold("docs/plans/../specs/BL-1-first.md"));
        Assert.Equal("specs/a.md", LinksPhase.Fold("./specs/./a.md"));
    }
}
