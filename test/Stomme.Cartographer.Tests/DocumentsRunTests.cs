using System.Text;
using System.Text.Json.Nodes;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

public sealed class DocumentsRunTests
{
    /// <summary>The synthetic repository plus what the documents phase must handle: an agent skill, a superseded decision with front matter, a plan that points at items and a spec, and a long document that chunks.</summary>
    private static void WriteRepository(string input)
    {
        SyntheticRepository.Write(input);
        Directory.CreateDirectory(Path.Combine(input, ".claude", "skills", "demo"));
        Directory.CreateDirectory(Path.Combine(input, "docs", "adr"));
        File.WriteAllText(Path.Combine(input, ".claude", "skills", "demo", "SKILL.md"), "# Demo skill\n\nUse this skill when the demo must run. Written 2026-10-01.\n");
        Directory.CreateDirectory(Path.Combine(input, "src", "app", ".claude", "skills", "ship"));
        File.WriteAllText(Path.Combine(input, "src", "app", ".claude", "skills", "ship", "SKILL.md"), "# Ship skill\n\nShipped with the app for its users' agents.\n");
        File.WriteAllText(Path.Combine(input, "docs", "adr", "0001-old.md"), "---\ntitle: \"Old choice\"\n---\n\n# 0001: Old choice\n\n- Status: superseded by 0002\n\nWe decided to use X over Y.\n");
        File.WriteAllText(Path.Combine(input, "docs", "roadmap.md"), "# Roadmap\n\nFirst BL-1, then BL-2. See docs/specs/BL-1-first.md.\n");
        var text = new StringBuilder("# Long\n\n");
        for (var section = 1; section <= 12; section++)
        {
            text.Append("## Section ").Append(section).Append("\n\n");
            for (var line = 1; line <= 20; line++)
            {
                text.Append("Line ").Append(line).Append(" of section ").Append(section).Append(" with some words to fill the budget.\n");
            }

            text.Append('\n');
        }

        File.WriteAllText(Path.Combine(input, "docs", "long.md"), text.ToString());
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
                StopAfterPhase = "documents",
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
    public async Task DocumentsRunOfflineWritesTheCardsResolvesReferencesAndResumes()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, reader, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        Assert.True(reader.DocumentCalls >= 8, "seven documents, one of them in chunks, one of them read twice because the gate withheld its text");

        var folder = Path.Combine(output, "documents");
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "specs", "BL-1-first.md")));
        Assert.True(File.Exists(Path.Combine(folder, ".claude", "skills", "demo", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "adr", "0001-old.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "roadmap.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "long.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "evil.md")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "backlog.md")), "a ledger is a document too, by rule; its rows are the items");
        Assert.False(File.Exists(Path.Combine(folder, "src", "main.py.md")), "code is not a document");

        List<DocumentCard> cards;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            cards = DocumentCatalogue.Load(state);
            var units = state.UnitsOf("documents");
            Assert.Contains(units, u => u.Key == "document/README.md");
            Assert.True(units.Count(u => u.Key.StartsWith("document/docs/long.md#", StringComparison.Ordinal)) >= 2, "the long document is read in chunks");
        }

        Assert.Equal(10, cards.Count);

        var skill = Assert.Single(cards, c => c.Path == ".claude/skills/demo/SKILL.md");
        Assert.True(skill.ForAgents);
        Assert.True(skill.ReferenceOnly);
        Assert.Equal(("agents", "process", "guide"), (skill.Audience, skill.Shelf, skill.Kind));
        Assert.Contains("kind-revised", skill.Flags);
        Assert.Contains(skill.Dates, d => d.Date == "2026-10-01");

        // A skill inside a module folder ships with it: the product shelf, when the reader gives no shelf of its own.
        var shipped = Assert.Single(cards, c => c.Path == "src/app/.claude/skills/ship/SKILL.md");
        Assert.Equal(("agents", "product", true), (shipped.Audience, shipped.Shelf, shipped.ReferenceOnly));
        Assert.Equal("src/app", DocumentsPhase.AgentGuideFolder(shipped.Path));
        Assert.Equal("", DocumentsPhase.AgentGuideFolder("CLAUDE.md"));
        Assert.Equal("", DocumentsPhase.AgentGuideFolder(".claude/agents/x.md"));
        Assert.Equal("src/Server", DocumentsPhase.AgentGuideFolder("src/Server/CLAUDE.md"));

        var adr = Assert.Single(cards, c => c.Path == "docs/adr/0001-old.md");
        Assert.Equal(("superseded", "0002", "adr", "Status: superseded by 0002"), (adr.Status, adr.SupersededBy, adr.Kind, adr.StatusAsWritten));
        var decision = Assert.Single(adr.Decisions);
        Assert.Equal(9, decision.Line);
        Assert.False(adr.ForAgents);

        var roadmap = Assert.Single(cards, c => c.Path == "docs/roadmap.md");
        Assert.Equal(("plan", "other"), (roadmap.Kind, roadmap.KindByOrientation));
        Assert.Contains("kind-revised", roadmap.Flags);
        Assert.Contains(roadmap.References, r => r.Kind == "id" && r.Text == "BL-1" && r.Item == "BL-1" && r.Line == 3);
        Assert.Contains(roadmap.References, r => r.Kind == "id" && r.Text == "BL-2" && r.Item == "BL-2");
        Assert.Contains(roadmap.References, r => r.Kind == "path" && r.Document == "docs/specs/BL-1-first.md");

        var readme = Assert.Single(cards, c => c.Path == "README.md");
        Assert.Equal(("guide", "people", false), (readme.Kind, readme.Audience, readme.ForAgents));
        Assert.Contains(readme.References, r => r.Kind == "id" && r.Text == "BL-12" && r.Item is null);
        Assert.Contains(readme.References, r => r.Kind == "path" && r.Text == "docs/backlog.md" && r.Document == "docs/backlog.md");

        var longDocument = Assert.Single(cards, c => c.Path == "docs/long.md");
        Assert.True(longDocument.Source.Chunks >= 2);
        Assert.Contains("chunked", longDocument.Flags);
        Assert.Contains("joined by the engine", longDocument.Summary, StringComparison.Ordinal);
        Assert.Contains("Part 1:", longDocument.Summary, StringComparison.Ordinal);

        var evil = Assert.Single(cards, c => c.Path == "docs/evil.md");
        Assert.Contains("read-with-flagged-text", evil.Flags);
        Assert.DoesNotContain("HACKED", evil.Summary, StringComparison.Ordinal);

        var adrFile = File.ReadAllText(Path.Combine(folder, "docs", "adr", "0001-old.md"));
        Assert.Contains("status: superseded", adrFile, StringComparison.Ordinal);
        Assert.Contains("superseded_by: \"0002\"", adrFile, StringComparison.Ordinal);
        // The source's own front matter stays in the body as written, after the card (change 8 of the verdict).
        Assert.DoesNotContain("source_front_matter", adrFile, StringComparison.Ordinal);
        Assert.Contains("drafted_by: survey\n---\n\n---\ntitle: \"Old choice\"\n---\n", adrFile.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\n# 0001: Old choice", adrFile, StringComparison.Ordinal);
        Assert.Contains("drafted_by: survey", adrFile, StringComparison.Ordinal);

        var skillFile = File.ReadAllText(Path.Combine(folder, ".claude", "skills", "demo", "SKILL.md"));
        Assert.Contains("for_agents: true", skillFile, StringComparison.Ordinal);
        Assert.Contains("shelf: \"process\"", skillFile, StringComparison.Ordinal);
        Assert.Contains("reference_only: true", skillFile, StringComparison.Ordinal);
        Assert.Contains("owner: git", skillFile, StringComparison.Ordinal);

        var roadmapFile = File.ReadAllText(Path.Combine(folder, "docs", "roadmap.md"));
        Assert.Contains("    item: 1", roadmapFile, StringComparison.Ordinal);
        Assert.Contains("[[0001-", roadmapFile, StringComparison.Ordinal);
        Assert.Contains("[[documents/docs/specs/BL-1-first]]", roadmapFile, StringComparison.Ordinal);
        Assert.Contains("## Links (survey)", roadmapFile, StringComparison.Ordinal);

        Assert.Contains("## Documents", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);
        Assert.Contains("Documents 10 (1 superseded", File.ReadAllText(Path.Combine(output, "state.md")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "docs", "backlog.md")), "the ledger is a document too, by rule");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        var readmeEntry = manifest["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "README.md");
        Assert.Equal("documents/README.md", readmeEntry!["document"]!.GetValue<string>());
        var backlogEntry = manifest["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "docs/backlog.md");
        Assert.Equal("documents/docs/backlog.md", backlogEntry!["document"]!.GetValue<string>());
        Assert.DoesNotContain(Directory.GetFiles(input, "*", SearchOption.AllDirectories), f => f.Contains("documents", StringComparison.Ordinal));

        // A second run reads nothing again: every document unit is in the state.
        var calls = reader.DocumentCalls;
        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.Equal(calls, reader.DocumentCalls);
        Assert.Contains("resumed from the state (read in run", File.ReadAllText(Path.Combine(output, "log.md")), StringComparison.Ordinal);
        Assert.Equal(10, Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Length);
    }

    /// <summary>The title rule, a template, and backup copies, on shapes a repository of any kind can have.</summary>
    [Fact]
    public async Task ATitleIsTheFilesOwnATemplateIsADocumentAndABackupCopyIsListedAndNeverRead()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        foreach (var folder in new[] { "a", "b", "c" })
        {
            Directory.CreateDirectory(Path.Combine(input, "docs", "guides", folder));
            File.WriteAllText(Path.Combine(input, "docs", "guides", folder, "overview.md"), $"# Overview\n\nWhat part {folder} does.\n");
        }

        File.WriteAllText(Path.Combine(input, "docs", "guides", "a", "README.md"), "Text with no heading at all, only a first line.\n");
        File.WriteAllText(Path.Combine(input, "docs", "notes.txt"), "open\n\nSome plain notes about the demo.\n");
        File.WriteAllText(Path.Combine(input, "docs", "named.md"), "---\nname: deploy-helper\ndescription: how the demo is deployed\n---\n\n## When to use\n\nWhen the demo must go out.\n");
        File.WriteAllText(Path.Combine(input, "docs", "marked.md"), "# **Bold** plan for [the site](https://example.org) `v2`\n\nWords.\n");
        File.WriteAllText(Path.Combine(input, "docs", "persian.md"), "# طرح اول\n\nمتن.\n");
        File.WriteAllText(Path.Combine(input, "docs", "letter-template.md"), "# Letter to a customer\n\nDear {{name}}, thank you for {{reason}}.\n");
        File.WriteAllText(Path.Combine(input, "docs", "roadmap.md.bak"), "# Roadmap before the change\n\nThe words only the backup holds: zebra-crossing-41.\n");
        File.WriteAllText(Path.Combine(input, "docs", "roadmap.md~"), "# Roadmap, an editor's copy\n\nMore words only a copy holds: zebra-crossing-42.\n");
        var (options, services, reader, _) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        List<DocumentCard> cards;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            cards = DocumentCatalogue.Load(state);
        }

        string TitleOf(string path) => Assert.Single(cards, c => c.Path == path).Title!;

        // The file's own title: front matter's title, then its name, then the first level-one heading without its markup.
        Assert.Equal("Old choice", TitleOf("docs/adr/0001-old.md"));
        Assert.Equal("deploy-helper", TitleOf("docs/named.md"));
        Assert.Equal("Bold plan for the site v2", TitleOf("docs/marked.md"));
        Assert.Equal("طرح اول", TitleOf("docs/persian.md"));
        Assert.Equal("Backlog", TitleOf("docs/backlog.md"));

        // Never a raw first line, a section heading or the reader's word: the path names the file, with its folder when every folder has one.
        Assert.Equal("docs/notes", TitleOf("docs/notes.txt"));
        Assert.Equal("a/README", TitleOf("docs/guides/a/README.md"));

        // A heading copied onto three documents names none of them.
        Assert.All(new[] { "a", "b", "c" }, folder =>
        {
            var card = Assert.Single(cards, c => c.Path == $"docs/guides/{folder}/overview.md");
            Assert.Equal($"{folder}/overview", card.Title);
            Assert.Contains("title-by-path", card.Flags);
        });
        Assert.Equal(cards.Count, cards.Select(c => c.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));

        // A template is a document: a letter someone wrote is content, whatever placeholders it holds.
        var template = Assert.Single(cards, c => c.Path == "docs/letter-template.md");
        Assert.Equal("Letter to a customer", template.Title);
        Assert.True(File.Exists(Path.Combine(output, "documents", "docs", "letter-template.md")));
        Assert.DoesNotContain("template", Stomme.Cartographer.Atlas.CatalogueJsonWriter.NotADocumentKinds);
        Assert.False(JsonNode.Parse(File.ReadAllText(Path.Combine(output, "documents.json")))!["documents"]!.AsArray().Single(d => d!["path"]!.GetValue<string>() == "docs/letter-template.md")!["notADocument"]!.GetValue<bool>());

        // A backup copy is listed with its reason and is never read: no card, no body, no word of it sent to the reader.
        Assert.DoesNotContain(cards, c => c.Path.StartsWith("docs/roadmap.md.", StringComparison.Ordinal) || c.Path.EndsWith('~'));
        Assert.DoesNotContain(reader.Sent, text => text.Contains("zebra-crossing", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".sqlite", StringComparison.Ordinal)),
            f => File.ReadAllText(f).Contains("zebra-crossing", StringComparison.Ordinal));
        var files = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!["files"]!.AsArray();
        Assert.Equal("a backup copy (.bak)", files.Single(f => f!["path"]!.GetValue<string>() == "docs/roadmap.md.bak")!["notRead"]!.GetValue<string>());
        Assert.Equal("a backup copy (~)", files.Single(f => f!["path"]!.GetValue<string>() == "docs/roadmap.md~")!["notRead"]!.GetValue<string>());
        Assert.Null(files.Single(f => f!["path"]!.GetValue<string>() == "docs/roadmap.md")!["notRead"]);
        Assert.Contains("2 file(s) not read, a backup copy", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);
    }
}

public sealed class DocumentsFailureTests
{
    [Fact]
    public async Task AFailedReaderCallKeepsOrientationsReadingAndTheNextRunRetriesIt()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        var reader = new ScriptedChatClient { FailOnDocument = "docs/specs/BL-1-first.md" };
        var options = new SurveyOptions
        {
            InputPath = input,
            Conductor = new ConductorOptions
            {
                OutputPath = output,
                PlaybookPath = TestPaths.Playbook,
                StopAfterPhase = "documents",
                Reader = new ReaderOptions { Retries = 0 },
            },
        };
        var services = new RunServices
        {
            QuestionPolicy = (store, book) => new WrittenQuestionPolicy(store, book),
            Backup = BackupKind.None,
            ChatClients = _ => reader,
            JudgeModel = new ScriptedJudge(),
        };

        var first = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, first.Outcome);
        List<DocumentCard> cards;
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            cards = DocumentCatalogue.Load(state);
            Assert.DoesNotContain(state.UnitsOf("documents"), u => u.Key == "document/docs/specs/BL-1-first.md");
        }

        var spec = Assert.Single(cards, c => c.Path == "docs/specs/BL-1-first.md");
        Assert.Contains("reader-failed", spec.Flags);
        Assert.Equal(("spec", DecidedBy.Rule), (spec.Kind, spec.By));
        Assert.Contains("reader-failed", File.ReadAllText(Path.Combine(output, "documents", "docs", "specs", "BL-1-first.md")), StringComparison.Ordinal);
        Assert.Contains("1 whose reader or judge call failed", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);

        var calls = reader.DocumentCalls;
        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.Equal(calls + 1, reader.DocumentCalls);
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var retried = Assert.Single(DocumentCatalogue.Load(state), c => c.Path == "docs/specs/BL-1-first.md");
            Assert.DoesNotContain("reader-failed", retried.Flags);
            Assert.Equal(DecidedBy.Reader, retried.By);
            Assert.Contains(state.UnitsOf("documents"), u => u.Key == "document/docs/specs/BL-1-first.md");
        }
    }
}

public sealed class GeneratedTextTests
{
    [Fact]
    public void MinifiedTextIsGeneratedByRuleAndProseIsNot()
    {
        var file = new SourceFile("x.map", "x", 1, ".map", FileCategory.TextUnknown, null, true, "h", true);
        var minified = new TextDecoder().Decode(file, Encoding.UTF8.GetBytes("{\"version\":3,\"mappings\":\"" + new string('A', 30_000) + "\"}"));
        Assert.True(DocumentsPhase.LooksGenerated(minified));
        var dense = new TextDecoder().Decode(file, Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Repeat(new string('b', 400) + ";" + new string('c', 400), 40))));
        Assert.True(DocumentsPhase.LooksGenerated(dense), "forty long lines with no spaces");
        var prose = new TextDecoder().Decode(file, Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Repeat("These are ordinary words, with spaces between them, as a person writes.", 400))));
        Assert.False(DocumentsPhase.LooksGenerated(prose));
        var unwrapped = new TextDecoder().Decode(file, Encoding.UTF8.GetBytes(string.Join(" ", Enumerable.Repeat("one long paragraph on a single line, as an editor without wrapping writes it,", 200))));
        Assert.False(DocumentsPhase.LooksGenerated(unwrapped), "a 15,000-character line of prose is not machine output");
    }
}

public sealed class DocumentSummaryTests
{
    [Fact]
    public void TheBlockBeforeAChunkKeepsItsShapeOverAHundredChunks()
    {
        var text = new StringBuilder("# Big\n\n");
        for (var s = 1; s <= 100; s++)
        {
            text.Append("## Heading ").Append(s).Append("\n\nline\n\n");
        }

        var file = new SourceFile("docs/big.md", "x", text.Length, ".md", FileCategory.Document, null, true, "h", true);
        var decoded = new MarkdownDecoder().Decode(file, Encoding.UTF8.GetBytes(text.ToString()));
        var mapped = new MappedFile { Path = file.Path, Kind = "report", Confidence = 0.8, Reasoning = "a report" };
        var before = new List<DocumentsPhase.DocumentChunkResult>();
        var lengths = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            var chunk = new TextChunk(file.Path, i, 100, i * 4 + 1, i * 4 + 4, i == 0 ? 0 : 2, "text");
            var block = DocumentSummary.Build(file, mapped, decoded, chunk, before, false, 2000);
            lengths.Add(block.Length);
            Assert.True(block.Length <= 2000, "within the cap");
            before.Add(new DocumentsPhase.DocumentChunkResult
            {
                Index = i,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                Kind = "report",
                Status = "unstated",
                Audience = "people",
                Summary = new string('s', 900),
                Topics = [$"topic {i}", $"other {i}"],
                Components = [$"component {i}"],
                People = [$"person {i}"],
            });
        }

        var last = DocumentSummary.Build(file, mapped, decoded, new TextChunk(file.Path, 99, 100, 397, 400, 2, "text"), before, false, 2000);
        Assert.Contains("the last 12 of", last, StringComparison.Ordinal);
        Assert.Contains("the last 20 of", last, StringComparison.Ordinal);
        Assert.True(lengths[99] <= lengths[30] + 60, "the last chunk's block is the size of an early one");
        Assert.Contains("This is the whole file", DocumentSummary.Build(file, mapped, decoded, new TextChunk(file.Path, 0, 1, 1, 400, 0, "text"), [], true, 2000), StringComparison.Ordinal);
        Assert.Contains("written for AI agents", DocumentSummary.Build(file, mapped, decoded, new TextChunk(file.Path, 0, 1, 1, 400, 0, "text"), [], true, 2000), StringComparison.Ordinal);
    }
}
