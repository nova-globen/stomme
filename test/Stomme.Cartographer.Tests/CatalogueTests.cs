using System.Text;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Tests;

public sealed class StatusWordTests
{
    [Theory]
    [InlineData("DONE", "DONE", false)]
    [InlineData("**OPEN**", "OPEN", false)]
    [InlineData("DONE 2026-10-21", "DONE", false)]
    [InlineData("DONE 2026-10-21/22", "DONE", false)]
    [InlineData("CLOSED (2026-09-20)", "CLOSED", false)]
    [InlineData("FIXED 2026-10-04 at 7fb510c9", "FIXED", false)]
    [InlineData("DONE 2026-10-30 (3d9e476)", "DONE", false)]
    [InlineData("DONE (5 of 5)", "DONE", false)]
    [InlineData("CLOSED as declined", "CLOSED", true)]
    [InlineData("CLOSED BY DECISION", "CLOSED", true)]
    [InlineData("closed BY CONSTRUCTION", "closed", true)]
    [InlineData("CLOSED - SUPERSEDED by D98", "CLOSED", true)]
    [InlineData("DEFERRED (owner, 2026-10-21: the next version)", "DEFERRED", true)]
    [InlineData("DECLINED-until-a-consumer-exists", "DECLINED", true)]
    [InlineData("LANDED except owner-bound", "LANDED", true)]
    [InlineData("Folded into BL-2", "Folded", true)]
    [InlineData("PERMANENTLY UNVERIFIED", "PERMANENTLY UNVERIFIED", false)]
    [InlineData("In progress", "In progress", false)]
    public void TheWordIsTheLeadingWordsAndAQualifiedPhraseIsJudgedOnItsOwn(string phrase, string word, bool qualified)
    {
        Assert.Equal(word, StatusVocabulary.WordOf(phrase));
        Assert.Equal(qualified, StatusVocabulary.IsQualified(phrase, word));
    }
}

public sealed class IdNormaliserTests
{
    private static readonly List<IdScheme> Schemes =
    [
        new() { Name = "backlog id", Regex = @"\bBL-(\d+)\b", NumericGroup = 1, RefersTo = "work-item", Confirmed = true },
        new() { Name = "decision", Regex = @"\bD(\d{1,3})\b", NumericGroup = 1, RefersTo = "decision", Confirmed = true },
    ];

    [Theory]
    [InlineData("BL-068", "BL-68", "BL-68", 68, null)]
    [InlineData("D001", "D1", "D1", 1, null)]
    [InlineData("**BL-12**", "BL-12", "BL-12", 12, null)]
    [InlineData("BL-4b", "BL-4b", "BL-4", 4, "b")]
    [InlineData("BL-26(b)", "BL-26(b)", "BL-26", 26, "(b)")]
    [InlineData("BL-1 (v1)", "BL-1(v1)", "BL-1", 1, "(v1)")]
    [InlineData("BL-78s", "BL-78", "BL-78", 78, null)]
    [InlineData("BL-12,", "BL-12", "BL-12", 12, null)]
    public void PaddingIsStrippedSuffixesAreKeptApartAndAPossessiveIsItsBase(string text, string key, string baseKey, int number, string? suffix)
    {
        var id = IdNormaliser.Normalise(text, Schemes);
        Assert.NotNull(id);
        Assert.Equal((key, baseKey, number, suffix), (id.Key, id.Base, id.Number, id.Suffix));
    }

    [Theory]
    [InlineData("BL-xx")]
    [InlineData("D2020")]
    [InlineData("BL-4bcd")]
    [InlineData("nothing")]
    [InlineData("")]
    public void WhatNoSchemeDefinesIsNotAnId(string text)
    {
        Assert.Null(IdNormaliser.Normalise(text, Schemes));
    }

    [Fact]
    public void AnUnconfirmedSchemeIsNotUsed()
    {
        var schemes = new List<IdScheme> { new() { Name = "ghost", Regex = @"\bZZ-(\d+)\b", NumericGroup = 1, Confirmed = false } };
        Assert.Null(IdNormaliser.Normalise("ZZ-9", schemes));
    }

    [Fact]
    public void SlugsAreLowerCaseAsciiAndBounded()
    {
        Assert.Equal("first-thing-see-bl-9", ItemCatalogue.Slug("First thing (see BL-9)"));
        Assert.Equal("raksmorgas-till-lunch", ItemCatalogue.Slug("Räksmörgås till lunch!"));
        Assert.Equal("item", ItemCatalogue.Slug("***"));
        Assert.True(ItemCatalogue.Slug(new string('a', 200) + " b").Length <= 60);
    }
}

public sealed class ChunkSummaryTests
{
    [Fact]
    public void AHundredChunksLaterTheSummaryIsStillTheSizeOfTheSecond()
    {
        var text = new StringBuilder("# A long ledger\n\n");
        for (var section = 1; section <= 100; section++)
        {
            text.Append("## Section ").Append(section).Append(" with a heading long enough to matter in the summary\n\n| ID | Item | Status |\n|---|---|---|\n");
            for (var row = 1; row <= 3; row++)
            {
                text.Append("| BL-").Append(section * 10 + row).Append(" | A thing of section ").Append(section).Append(" with words to fill the line and the chunk budget | OPEN |\n");
            }

            text.Append('\n');
        }

        var file = new SourceFile("docs/backlog.md", "x", text.Length, ".md", FileCategory.Document, null, true, "h", true);
        var document = new MarkdownDecoder().Decode(file, Encoding.UTF8.GetBytes(text.ToString()));
        var chunks = Chunker.Chunk(document, 80, 2);
        Assert.True(chunks.Count >= 100, $"expected at least a hundred chunks, got {chunks.Count}");

        var rowsBefore = new List<RowSummary>();
        var summaries = new List<string>();
        foreach (var chunk in chunks)
        {
            summaries.Add(ChunkSummary.Build("docs/backlog.md", "open-ledger", document, chunk, rowsBefore, 2_000));
            for (var row = 1; row <= 3; row++)
            {
                rowsBefore.Add(new RowSummary($"BL-{chunk.Index * 10 + row}", $"A thing of chunk {chunk.Index}", "OPEN"));
            }
        }

        Assert.All(summaries, s => Assert.True(s.Length <= 2_000, "a summary stayed within the cap"));
        Assert.Contains("Nothing came before this chunk", summaries[0], StringComparison.Ordinal);
        Assert.Contains("Rows read in the chunks before: 3.", summaries[1], StringComparison.Ordinal);
        Assert.Contains($"Rows read in the chunks before: {(chunks.Count - 1) * 3}.", summaries[^1], StringComparison.Ordinal);
        Assert.Contains("the last 40 of", summaries[^1], StringComparison.Ordinal);
        // Once both lists have hit their caps the shape is fixed, and the size stops growing however many chunks follow.
        var saturated = summaries.FindIndex(s => s.Contains("the last 12 of", StringComparison.Ordinal) && s.Contains("the last 40 of", StringComparison.Ordinal));
        Assert.True(saturated >= 0 && saturated < summaries.Count - 20, $"the caps were hit at chunk {saturated} of {summaries.Count}");
        Assert.True(summaries[^1].Length <= summaries[saturated].Length + 60, $"the last summary ({summaries[^1].Length}) is the size of the first saturated one ({summaries[saturated].Length}, chunk {saturated})");
    }
}

public sealed class RuleTests
{
    [Fact]
    public void AnIdWrittenWithAnotherRepositorysNameInTheLedgersIsForeign()
    {
        // A name stands in the possessive somewhere, or before three different ids; the other repository's numbers may collide with this one's.
        Assert.Equal("infra", ItemsPhase.QualifierInText("BL-144", ["| BL-12 | waits on infra BL-144 | OPEN |", "| BL-87 | after infra BL-087 and infra BL-134 | DONE |"]));
        Assert.Equal("infra", ItemsPhase.QualifierInText("BL-287", ["see infra's BL-287 for the rest"]));
        Assert.Equal("infra", ItemsPhase.QualifierInText("BL-144", ["| BL-12 | waits on infra BL-144 | OPEN |", "| BL-13 | infra's BL-134 | DONE |"]));
        Assert.Null(ItemsPhase.QualifierInText("BL-9", ["| BL-1 | First thing (see BL-9) | OPEN |"]));
        // A verb before one or two ids is no name.
        Assert.Null(ItemsPhase.QualifierInText("BL-14", ["| BL-1 | closes BL-14 | OPEN |", "| BL-7 | closes BL-7 | DONE |"]));
    }

    [Fact]
    public void ALanguageIsAShortCodeOrUnknown()
    {
        Assert.Equal("en", DocumentsPhase.LanguageCode("en"));
        Assert.Equal("en-US", DocumentsPhase.LanguageCode("en_us"));
        Assert.Equal("sv", DocumentsPhase.LanguageCode("Swedish"));
        Assert.Equal("sv", DocumentsPhase.LanguageCode("Swedish, with English terms"));
        Assert.Equal("unknown", DocumentsPhase.LanguageCode("The document is written in plain prose"));
        Assert.Null(DocumentsPhase.LanguageCode(" "));
    }

    [Fact]
    public void ALedgerRowKeepsEveryCellAndItsBodyIsTheRowsOwnText()
    {
        var schemes = new List<IdScheme> { new() { Name = "BL", Example = "BL-12", Regex = @"BL-\d+", NumericGroup = 0, RefersTo = "work-item", Confirmed = true } };
        var rows = LedgerTable.Parse("| ID | Item | Size | Status |\n|---|---|---|---|\n| **BL-7** | **Seventh thing** with a long tail (see D12) | M | DONE 2026-10-03 |\n", schemes);
        var row = Assert.Single(rows);
        Assert.Equal("BL-7", row.Id);
        Assert.Equal("Seventh thing with a long tail (see D12)", row.Title);
        Assert.Equal("DONE 2026-10-03", row.Status);
        Assert.Equal(4, row.Cells.Count);
        Assert.Equal("**Size**: M", row.Body().Split("\n\n")[1]);
        Assert.StartsWith("**Seventh thing** with a long tail", row.Body(), StringComparison.Ordinal);
        Assert.Equal(["BL-7"], ItemsPhase.IdsByCode(row.Text, schemes));

        // A chunk that starts inside the table: the first line is a row, not the header.
        var headless = LedgerTable.Parse("| **BL-8** | Eighth thing | S | DONE |\n| **BL-9** | Ninth thing | M | OPEN |\n", schemes);
        Assert.Equal(["BL-8", "BL-9"], headless.Select(r => r.Id));
        Assert.Equal("Eighth thing", headless[0].Title);
        Assert.Equal("DONE", headless[0].Status);
    }

    [Fact]
    public void AnIdInAnotherRepositorysListOrANumberedChainIsNoReference()
    {
        var schemes = new List<IdScheme>
        {
            new() { Name = "BL", Example = "BL-12", Regex = @"BL-\d+", RefersTo = "work-item", Confirmed = true },
            new() { Name = "D", Example = "D35", Regex = @"D\d+", RefersTo = "decision", Confirmed = true },
        };
        Assert.Equal(["D35"], ItemsPhase.IdsByCode("CI and the repositories (2026-10-28, infra's rulings C2, C18, D23; the owner's D35).", schemes));
        Assert.Equal(["D12", "D13", "BL-9x"], ItemsPhase.IdsByCode("See D12 and D13, then BL-9x.", schemes));
        Assert.Empty(ItemsPhase.IdsByCode("identity phases D2+D3 and wave D4", schemes));
        Assert.Equal(["D5"], ItemsPhase.IdsByCode("identity phases D2+D3. Decided in D5.", schemes));
        Assert.Equal(["BL-1", "D35"], ItemsPhase.IdsByCode("| BL-1 | after C29, D35 | D35 |", schemes));
        Assert.True(ItemsPhase.IsForeignInLine("infra's rulings C2, C18, D23", "D23", schemes));
        Assert.False(ItemsPhase.IsForeignInLine("the owner's D35", "D35", schemes));
    }

    [Fact]
    public void TheConsumersFieldsAreDerivedByCode()
    {
        Assert.Equal((null, "bug"), Atlas.CatalogueJsonWriter.LevelAndKind("bug"));
        Assert.Equal(("feature", null), Atlas.CatalogueJsonWriter.LevelAndKind("epic"));
        Assert.Equal((null, null), Atlas.CatalogueJsonWriter.LevelAndKind(null));
        Assert.Equal("high", Atlas.CatalogueJsonWriter.Priority("P1"));
        Assert.Equal("low", Atlas.CatalogueJsonWriter.Priority("P3"));
        Assert.Null(Atlas.CatalogueJsonWriter.Priority("Must"));
        Assert.Equal("10.75.0", Atlas.CatalogueJsonWriter.ReleasedIn("DONE 2026-10-29 for 10.75.0"));
        Assert.Null(Atlas.CatalogueJsonWriter.ReleasedIn("DONE 2026-10-29"));
        Assert.Equal(["the owner"], Atlas.CatalogueJsonWriter.PersonBlockers("only with the owner", true));
        Assert.Equal(["team:dev's"], Atlas.CatalogueJsonWriter.PersonBlockers("waiting on `team:dev`'s fixes", true));
        Assert.Empty(Atlas.CatalogueJsonWriter.PersonBlockers("only with the owner", false));
    }

    [Fact]
    public void AnAnswerInJsonIsReadLikeOneInMarkdown()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "answers.md");
        File.WriteAllText(Path.ChangeExtension(path, ".json"), "{ \"status:OPEN\": { \"text\": \"\", \"option\": \"ready\", \"by\": \"person\" }, \"tier:Must\": { \"text\": \"critical\", \"option\": null, \"by\": \"person\" } }");
        var store = new Stomme.Conductor.Policies.AnswerStore(path);
        Assert.Equal("ready", store.Get("status:OPEN"));
        Assert.Equal("critical", store.Get("tier:Must"));
    }

    [Fact]
    public void AnswersInJsonKeepTheTextBesideTheOptionReadAnyValueAndFailOnBadJson()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "answers.md");
        var json = Path.ChangeExtension(path, ".json");
        File.WriteAllText(json, "{ \"q:1\": { \"text\": \"because the owner said so\", \"option\": \"keep\", \"by\": \"person\" }, \"q:2\": 7, \"q:3\": true, \"q:4\": { \"text\": null, \"option\": \"drop\" } }");
        var store = new Stomme.Conductor.Policies.AnswerStore(path);
        Assert.Equal("because the owner said so", store.Get("q:1"));
        Assert.Equal("keep", store.GetOption("q:1"));
        Assert.Equal("7", store.Get("q:2"));
        Assert.Equal("true", store.Get("q:3"));
        Assert.Equal("drop", store.Get("q:4"));

        var question = new Stomme.Conductor.Policies.Question("q:1", "items", "Keep them?",
            [new Stomme.Conductor.Policies.QuestionOption("keep", "keep"), new Stomme.Conductor.Policies.QuestionOption("drop", "drop")], "drop");
        var answer = store.Stored(question);
        Assert.NotNull(answer);
        Assert.Equal("keep", answer.Option);
        Assert.Equal("because the owner said so", answer.Text);

        File.WriteAllText(json, "{ \"q:1\": { \"option\": \"keep\" ");
        var failure = Assert.Throws<InvalidOperationException>(() => new Stomme.Conductor.Policies.AnswerStore(path));
        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        File.WriteAllText(json, "[ \"keep\" ]");
        Assert.Throws<InvalidOperationException>(() => new Stomme.Conductor.Policies.AnswerStore(path));
    }

    [Fact]
    public async Task EveryPolicyHonoursAnAnswerAPersonAlreadyGave()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "answers.md");
        File.WriteAllText(path, "## q:1\n\nkeep\n");
        var store = new Stomme.Conductor.Policies.AnswerStore(path);
        var question = new Stomme.Conductor.Policies.Question("q:1", "items", "Keep them?",
            [new Stomme.Conductor.Policies.QuestionOption("keep", "keep"), new Stomme.Conductor.Policies.QuestionOption("drop", "drop")], "drop");

        // Nobody is asked under the none policy, yet what a person already said holds: an answer is never dropped by the way a run asks.
        var none = await new Stomme.Conductor.Policies.NoneQuestionPolicy(new Stomme.Conductor.Policies.QuestionBook(null, "run"), store).AskAsync(question, CancellationToken.None);
        Assert.Equal("keep", none?.Option);
        var written = await new Stomme.Conductor.Policies.WrittenQuestionPolicy(store, new Stomme.Conductor.Policies.QuestionBook(null, "run")).AskAsync(question, CancellationToken.None);
        Assert.Equal("keep", written?.Option);
        var unanswered = await new Stomme.Conductor.Policies.NoneQuestionPolicy(new Stomme.Conductor.Policies.QuestionBook(null, "run"), store)
            .AskAsync(question with { Key = "q:2" }, CancellationToken.None);
        Assert.Null(unanswered);
    }

    [Fact]
    public void ASlugKeepsTheLettersOfEveryScript()
    {
        Assert.Equal("cafe-au-lait", ItemCatalogue.Slug("Café au lait"));
        Assert.NotEmpty(ItemCatalogue.Slug("修复登录页面的错误"));
        Assert.NotEmpty(ItemCatalogue.Slug("راه‌اندازی سرور پشتیبان"));
        Assert.NotEqual(ItemCatalogue.Slug("راه‌اندازی سرور پشتیبان"), ItemCatalogue.Slug("修复登录页面的错误"));
    }

    [Fact]
    public void AWaitingConditionIsNotReady()
    {
        Assert.True(ItemsPhase.IsWaitingCondition("waiting on team:dev's fixes"));
        Assert.True(ItemsPhase.IsWaitingCondition("after 12.0.0"));
        Assert.True(ItemsPhase.IsWaitingCondition("blocked by BL-12"));
        Assert.False(ItemsPhase.IsWaitingCondition("only with the owner"));
        Assert.False(ItemsPhase.IsWaitingCondition(null));
    }

    [Fact]
    public void ADocumentFileNeverMeetsItsMdSibling()
    {
        Assert.Equal("docs/a.md", DocumentCatalogue.AtlasRelativePath("docs/a.md"));
        Assert.Equal("docs/notes.txt.md", DocumentCatalogue.AtlasRelativePath("docs/notes.txt"));
        Assert.Equal("LICENSE.txt.md", DocumentCatalogue.AtlasRelativePath("LICENSE"));
        Assert.Equal("LICENSE.md", DocumentCatalogue.AtlasRelativePath("LICENSE.md"));
        Assert.Equal("src/Dockerfile.txt.md", DocumentCatalogue.AtlasRelativePath("src/Dockerfile"));
    }

    [Fact]
    public void ALineIsSetByCodeWhereTheCitedTextIsFound()
    {
        var lines = new[] { "# Title", "", "See BL-12 for the rest.", "Nothing here.", "D92 was accepted." };
        var decoded = new DecodedDocument("a.md", string.Join('\n', lines), lines, "Title", [], "markdown", null);
        Assert.Equal(3, DocumentsPhase.LineByCode(decoded, "BL-12", 3));
        Assert.Equal(3, DocumentsPhase.LineByCode(decoded, "BL-12", 4));
        Assert.Equal(5, DocumentsPhase.LineByCode(decoded, "D92", 1));
        Assert.Equal(2, DocumentsPhase.LineByCode(decoded, "BL-99", 2));
        Assert.Null(DocumentsPhase.LineByCode(decoded, "BL-99", null));
    }

    [Fact]
    public void DatesInALicenceAreNotProjectHistory()
    {
        var commits = new List<Commit> { new("abc123", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "Test", "Initial commit", "") };
        var legal = Enumerable.Range(0, 12).Select(i => new DocumentCard { Path = $"LICENSE-{i}.md", Kind = "legal", Dates = [new DocumentDate { What = "copyright", Date = "2019-01-01" }] }).ToList();
        Assert.False(DecisionsPhase.DetectGap(commits, legal).Detected);
        var reports = Enumerable.Range(0, 12).Select(i => new DocumentCard { Path = $"docs/reports/{i}.md", Kind = "report", Dates = [new DocumentDate { What = "written", Date = "2025-06-01" }] }).ToList();
        Assert.True(DecisionsPhase.DetectGap(commits, reports).Detected);
    }
}
