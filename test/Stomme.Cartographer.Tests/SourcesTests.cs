using System.Text.Json.Nodes;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Playbooks;

namespace Stomme.Cartographer.Tests;

public sealed class FileTableTests
{
    private static FileTable Table() => new(Playbook.Load(TestPaths.Playbook).Data("files"));

    [Fact]
    public void ExtensionsAreClassifiedByTheTableAndLanguagesAreCode()
    {
        var table = Table();
        Assert.Equal(FileCategory.Document, table.CategoryOf(".md"));
        Assert.Equal(FileCategory.Code, table.CategoryOf(".py"));
        Assert.Equal("Python", table.LanguageOf(".py")!.Name);
        Assert.Equal("#", table.LanguageOf(".py")!.LineComment);
        Assert.Equal(FileCategory.Design, table.CategoryOf(".svg"));
        Assert.Equal(FileCategory.Image, table.CategoryOf(".png"));
        Assert.Null(table.CategoryOf(".nothing"));
    }

    [Fact]
    public void GuidesIgnoresAndManifestsComeFromTheTable()
    {
        var table = Table();
        Assert.True(table.IsGuide("CLAUDE.md"));
        Assert.True(table.IsGuide("README.md"));
        Assert.True(table.IsGuide("docs/README.md"));
        Assert.False(table.IsGuide("src/Thing/README.md"));
        Assert.True(table.IsIgnored("src/bin/Debug/a.dll"));
        Assert.False(table.IsIgnored("src/binary/a.cs"));
        Assert.Contains(table.ManifestsOf("src/App/App.csproj"), m => m.Name == ".NET project");
        Assert.Contains(table.ManifestsOf("web/package.json"), m => m.Name == "npm package");
        Assert.Contains(table.ManifestsOf("go.mod"), m => m.Name == "Go module");
    }

    [Fact]
    public void TextSniffingRejectsNulBytes()
    {
        Assert.True(FileTable.LooksLikeText("hello\nworld"u8));
        Assert.False(FileTable.LooksLikeText(new byte[] { 0x4D, 0x5A, 0x00, 0x00 }));
        Assert.True(FileTable.LooksLikeText(Array.Empty<byte>()));
    }
}

public sealed class DecoderTests
{
    [Fact]
    public void MarkdownHeadingsBecomeSectionsWithLineNumbers()
    {
        var text = "# Title\n\nIntro.\n\n## First\n\nBody one.\n\n### Deeper\n\nBody two.\n\n## Second\n\nEnd.\n";
        var file = new SourceFile("docs/a.md", "x", text.Length, ".md", FileCategory.Document, null, true, "h", true);
        var decoded = new MarkdownDecoder().Decode(file, System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Equal("Title", decoded.Title);
        Assert.Equal(4, decoded.Sections.Count);
        Assert.Equal((1, "Title", 1, 4), (decoded.Sections[0].Level, decoded.Sections[0].Heading, decoded.Sections[0].StartLine, decoded.Sections[0].EndLine));
        Assert.Equal((2, "First", 5, 8), (decoded.Sections[1].Level, decoded.Sections[1].Heading, decoded.Sections[1].StartLine, decoded.Sections[1].EndLine));
        Assert.Equal("## Second", decoded.Lines[decoded.Sections[3].StartLine - 1]);
        Assert.Equal("Body one.", decoded.Slice(7, 7));
    }

    [Fact]
    public void FrontMatterTitleWins()
    {
        var text = "---\ntitle: \"From front matter\"\n---\n\n# Heading\n";
        var file = new SourceFile("a.md", "x", text.Length, ".md", FileCategory.Document, null, true, "h", true);
        Assert.Equal("From front matter", new MarkdownDecoder().Decode(file, System.Text.Encoding.UTF8.GetBytes(text)).Title);
    }

    [Fact]
    public void PlainTextHasNoSectionsAndAShortFirstLineAsTitle()
    {
        var file = new SourceFile("notes.txt", "x", 10, ".txt", FileCategory.Document, null, true, "h", true);
        var decoded = new TextDecoder().Decode(file, System.Text.Encoding.UTF8.GetBytes("\nNotes\r\nline two"));
        Assert.Equal("Notes", decoded.Title);
        Assert.Empty(decoded.Sections);
        Assert.Equal(3, decoded.Lines.Count);
    }
}

public sealed class ChunkerTests
{
    private static DecodedDocument Document(string text)
    {
        var file = new SourceFile("big.md", "x", text.Length, ".md", FileCategory.Document, null, true, "h", true);
        return new MarkdownDecoder().Decode(file, System.Text.Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void ASmallDocumentIsOneChunk()
    {
        var chunks = Chunker.Chunk(Document("# A\n\ntext\n"), 1000, 2);
        var chunk = Assert.Single(chunks);
        Assert.Equal((1, 1, 0), (chunk.Count, chunk.StartLine, chunk.OverlapLines));
        Assert.Equal("the whole file", chunk.Position);
    }

    [Fact]
    public void ALargeDocumentIsCutAtHeadingsWithOverlapAndCoversEveryLine()
    {
        var text = new System.Text.StringBuilder();
        for (var section = 1; section <= 12; section++)
        {
            text.Append("## Section ").Append(section).Append('\n').Append('\n');
            for (var line = 1; line <= 20; line++)
            {
                text.Append("Line ").Append(line).Append(" of section ").Append(section).Append(" with some words to fill the budget.\n");
            }

            text.Append('\n');
        }

        var document = Document(text.ToString());
        var chunks = Chunker.Chunk(document, 600, 3);
        Assert.True(chunks.Count > 2, "expected several chunks");
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(document.Lines.Count, chunks[^1].EndLine);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].EndLine + 1, chunks[i].StartLine);
            Assert.Equal(3, chunks[i].OverlapLines);
            Assert.StartsWith(document.Lines[chunks[i].StartLine - 4], chunks[i].Text, StringComparison.Ordinal);
            Assert.True(chunks[i].Text.Length <= 600 * 3.5 + 400, "chunk within budget plus overlap");
        }

        Assert.Contains(chunks.Skip(1), c => document.Lines[c.StartLine - 1].StartsWith("## ", StringComparison.Ordinal));
    }

    [Fact]
    public void ATableOfLongRowsIsCutBetweenRowsNearTheBudgetNotAtEveryRow()
    {
        var text = new System.Text.StringBuilder("# Finished rows\n\n| ID | Item | Status |\n|---|---|---|\n");
        for (var row = 1; row <= 60; row++)
        {
            text.Append("| BL-").Append(row).Append(" | ").Append(new string('x', 300)).Append(" | DONE |\n");
        }

        var document = Document(text.ToString());
        var chunks = Chunker.Chunk(document, 600, 2);
        Assert.InRange(chunks.Count, 8, 16);
        Assert.Equal(document.Lines.Count, chunks[^1].EndLine);
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            Assert.True(chunk.EndLine - chunk.StartLine + 1 >= 3, $"chunk {chunk.Index + 1} holds {chunk.EndLine - chunk.StartLine + 1} line(s); a chunk is at least half the budget");
            Assert.True(chunk.Text.Length <= 600 * 3.5 + 700, "within the budget plus the overlap");
        }
    }

    [Fact]
    public void TheOverlapIsBoundedByCharactersWhenAsked()
    {
        var text = new System.Text.StringBuilder("# Rows\n\n");
        for (var row = 1; row <= 40; row++)
        {
            text.Append("| BL-").Append(row).Append(" | ").Append(new string('y', 300)).Append(" | DONE |\n");
        }

        var document = Document(text.ToString());
        var bounded = Chunker.Chunk(document, 600, 12, 700);
        Assert.True(bounded.Count > 2);
        Assert.All(bounded.Skip(1), c => Assert.InRange(c.OverlapLines, 1, 2));
        var unbounded = Chunker.Chunk(document, 600, 12);
        Assert.All(unbounded.Skip(1), c => Assert.True(c.OverlapLines >= 3, "twelve lines of overlap when nothing bounds it"));
    }

    [Fact]
    public void ProseWithBlankLinesIsCutAtTheLatestBlankLineInTheSecondHalf()
    {
        var text = new System.Text.StringBuilder("# Notes\n\n");
        for (var paragraph = 1; paragraph <= 40; paragraph++)
        {
            text.Append("Paragraph ").Append(paragraph).Append(' ').Append(new string('w', 150)).Append("\n\n");
        }

        var document = Document(text.ToString());
        var chunks = Chunker.Chunk(document, 600, 2);
        Assert.InRange(chunks.Count, 3, 8);
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            Assert.True(chunk.EndLine - chunk.StartLine + 1 >= 10, $"chunk {chunk.Index + 1} holds {chunk.EndLine - chunk.StartLine + 1} line(s)");
            Assert.True(string.IsNullOrWhiteSpace(document.Lines[chunk.EndLine]), "the next chunk starts at a blank line, so no paragraph is cut in two");
        }
    }

    [Fact]
    public void HeadStopsAtALineBoundary()
    {
        var (head, endLine) = Chunker.Head(Document("one\ntwo\nthree\nfour\n"), 9);
        Assert.Equal("one\ntwo", head);
        Assert.Equal(2, endLine);
    }
}

public sealed class RepositoryScannerTests
{
    [Fact]
    public void AWalkedFolderListsHashesAndClassifiesFilesAndSkipsIgnoredFolders()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        File.WriteAllText(Path.Combine(temp.Path, "README.md"), "# Hello\n");
        File.WriteAllText(Path.Combine(temp.Path, "docs", "plan.md"), "# Plan\n");
        File.WriteAllText(Path.Combine(temp.Path, "src", "main.go"), "package main\n");
        File.WriteAllText(Path.Combine(temp.Path, "src", "notes.weird"), "just text\n");
        File.WriteAllBytes(Path.Combine(temp.Path, "src", "blob.weird"), [0x00, 0x01, 0x02]);
        File.WriteAllText(Path.Combine(temp.Path, "bin", "out.txt"), "ignored\n");
        var table = new FileTable(Playbook.Load(TestPaths.Playbook).Data("files"));
        var snapshot = RepositoryScanner.Scan(temp.Path, [], table);
        Assert.Equal("walk", snapshot.ListedBy);
        Assert.Equal(5, snapshot.Files.Count);
        Assert.Equal(FileCategory.Code, snapshot.Files.Single(f => f.Path == "src/main.go").Category);
        Assert.Equal("Go", snapshot.Files.Single(f => f.Path == "src/main.go").Language);
        Assert.Equal(FileCategory.TextUnknown, snapshot.Files.Single(f => f.Path == "src/notes.weird").Category);
        Assert.Equal(FileCategory.Binary, snapshot.Files.Single(f => f.Path == "src/blob.weird").Category);
        Assert.Equal(64, snapshot.Files[0].Sha256.Length);
        Assert.DoesNotContain(snapshot.Files, f => f.Path.StartsWith("bin/", StringComparison.Ordinal));
    }
}

public sealed class SurveyPlaybookTests
{
    [Fact]
    public void TheSurveyPlaybookLoadsItsPromptsSchemasJudgeAndData()
    {
        var playbook = Playbook.Load(TestPaths.Playbook);
        Assert.Equal("survey-repository", playbook.Name);
        Assert.Contains("orientation", playbook.Phases);
        Assert.Contains("INPUT TEXT", playbook.Prompt("rules"), StringComparison.Ordinal);
        Assert.Contains("{{kinds}}", playbook.Prompt("orientation-kinds"), StringComparison.Ordinal);
        Assert.DoesNotContain("{{kinds}}", playbook.Prompt("orientation-kinds", new Dictionary<string, string> { ["kinds"] = "- spec" }), StringComparison.Ordinal);
        Assert.StartsWith("{", playbook.Schema("repository-map").Trim(), StringComparison.Ordinal);
        Assert.NotNull(playbook.JudgeSet("gate_in")["instructions_to_ai"]);
        Assert.NotNull(playbook.JudgeSet("gate_out")["follows_input_instruction"]);
        Assert.NotNull(playbook.Data("kinds")["document"]);
        Assert.Equal("repository", playbook.GlossaryKinds[0]);
        Assert.Equal(16, playbook.Hash.Length);
        Assert.IsType<JsonObject>(playbook.JudgeSet("gate_out"));
    }

    [Fact]
    public void TheEmbeddedPlaybookIsTheFolderToTheByte()
    {
        var embedded = SurveyPlaybook.Embedded();
        var folder = Playbook.Load(TestPaths.Playbook);
        Assert.Equal("embedded", embedded.Root);
        Assert.Equal(folder.Hash, embedded.Hash);
        Assert.Equal(folder.Phases, embedded.Phases);
        Assert.Equal(folder.Prompt("rules"), embedded.Prompt("rules"));
    }
}
