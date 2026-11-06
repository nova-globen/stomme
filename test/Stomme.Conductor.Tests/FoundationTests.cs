using System.Text.Json.Nodes;
using Stomme.Conductor.Models;
using Stomme.Conductor.Keys;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Conductor.Tests;

public sealed class JudgeBudgetTests
{
    [Fact]
    public void ADenseTextGetsShorterSlicesThanProse()
    {
        var prose = string.Join(" ", Enumerable.Repeat("plain words of a document", 2_000));
        Assert.Equal(72_000, Stomme.Conductor.Runner.JudgeBudget.SliceChars(prose, 72_000));
        var minified = new string('x', 50_000);
        var slice = Stomme.Conductor.Runner.JudgeBudget.SliceChars(minified, 72_000);
        Assert.InRange(slice, 4_000, 36_000);
        Assert.True(Stomme.Conductor.Runner.JudgeBudget.Weight(slice, slice, 72_000) == 72_000, "a full slice weighs the whole budget");
    }
}

/// <summary>A folder under the test's temp that is deleted when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class KeyReaderTests
{
    [Fact]
    public void AMissingVariableIsNotFoundAndNothingIsPrinted()
    {
        var (key, source) = KeyReader.Read("STOMME_TEST_NO_SUCH_VARIABLE_" + Guid.NewGuid().ToString("N"));
        Assert.Null(key);
        Assert.Equal(KeySource.NotFound, source);
    }

    [Fact]
    public void AProcessVariableIsReadAndTrimmed()
    {
        var name = "STOMME_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "  figures-only-value  ");
        try
        {
            var (key, source) = KeyReader.Read(name);
            Assert.Equal(KeySource.Process, source);
            Assert.Equal(18, key!.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}

public sealed class PriceTableTests
{
    [Fact]
    public void CostCountsCachedInputAtTheCachedPrice()
    {
        var price = new ModelPrice("m", 1m, 0.1m, 2m);
        var usd = price.CostOf(1_000_000, 500_000, 100_000);
        Assert.Equal(0.5m + 0.05m + 0.2m, usd);
    }

    [Fact]
    public void ADatedModelIdFindsItsFamily()
    {
        Assert.Equal("jev-latest", PriceTable.Default.Find("jev-latest")!.Model);
        Assert.Equal("mimo-v2.6-pro", PriceTable.Default.Find("mimo-v2.6-pro-20261101")!.Model);
        Assert.Null(PriceTable.Default.Find("nothing-priced"));
    }
}

public sealed class ReplyCacheTests
{
    [Fact]
    public void APutReplyComesBackByItsHashAndAnotherHashMisses()
    {
        using var temp = new TempFolder();
        var cache = new ReplyCache(temp.Path);
        var hash = ReplyCache.HashOf("request one");
        cache.Put(new CachedReply(hash, "reader", "p", "m", "m-1", DateTimeOffset.Now, "purpose", "{\"ok\":true}", 10, 2, 5, 1, 0.001m));
        Assert.True(cache.TryGet(hash, out var reply));
        Assert.Equal("{\"ok\":true}", reply!.ResponseText);
        Assert.Equal(10, reply.InputTokens);
        Assert.False(cache.TryGet(ReplyCache.HashOf("request two"), out _));
    }

    [Fact]
    public async Task ParallelWritesAndReadsOfOneEntryNeverFail()
    {
        using var temp = new TempFolder();
        var cache = new ReplyCache(temp.Path);
        var hash = ReplyCache.HashOf("the same request");
        var reply = new CachedReply(hash, "judge", "p", "m", "m-1", DateTimeOffset.Now, "purpose", "{\"ok\":true}", 10, 0, 5, 0, 0.001m);

        // Four parallel documents judging the same glossary piece did this on 2026-11-04 and one of them failed on the share.
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            cache.Put(reply);
            cache.TryGet(hash, out _);
        })));

        Assert.True(cache.TryGet(hash, out var got));
        Assert.Equal("{\"ok\":true}", got!.ResponseText);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }
}

public sealed class DecisionLogTests
{
    [Fact]
    public void RecordsAreNumberedAppendedAndRenderedAndSurviveReopening()
    {
        using var temp = new TempFolder();
        using (var log = new DecisionLog(temp.Path, "run-1"))
        {
            var first = log.Record(new DecisionRecord { Phase = "orientation", Kind = "file-kind", Subject = "docs/a.md", Decision = "spec", By = DecidedBy.Reader, Reasoning = "it says so", Confidence = 0.9 });
            var call = log.Record(new DecisionRecord { Phase = "orientation", Kind = "call", Subject = "kinds", Decision = "answered", By = DecidedBy.Reader, Model = "m", Cost = new CallCost(100, 10, 20, 5, 0.01m, false) });
            Assert.Equal(1, first.Seq);
            Assert.Equal(2, call.Seq);
            Assert.Equal(0.01m, log.CostOfRun().Usd);
            log.RenderMarkdown();
        }

        string markdownPath, jsonlPath;
        using (var reopened = new DecisionLog(temp.Path, "run-2"))
        {
        Assert.Equal(2, reopened.Records.Count);
        var third = reopened.Record(new DecisionRecord { Phase = "items", Kind = "item", Subject = "BL-1", Decision = "done", By = DecidedBy.Rule });
        Assert.Equal(3, third.Seq);
        Assert.Equal(0m, reopened.CostOfRun().Usd);
        reopened.RenderMarkdown();
        var markdown = File.ReadAllText(reopened.MarkdownPath);
        Assert.Contains("## Run run-1", markdown, StringComparison.Ordinal);
        Assert.Contains("## Run run-2", markdown, StringComparison.Ordinal);
        Assert.Contains("file-kind: docs/a.md", markdown, StringComparison.Ordinal);
        Assert.Contains("| 2 | kinds | m | 100 | 10 | 20 | 5 | $0.0100 | no |", markdown, StringComparison.Ordinal);
            markdownPath = reopened.MarkdownPath;
            jsonlPath = reopened.JsonlPath;
        }

        Assert.Equal(3, File.ReadAllLines(jsonlPath).Length);
        Assert.Contains("## Run run-2", File.ReadAllText(markdownPath), StringComparison.Ordinal);
    }
}

public sealed class SpendBookTests
{
    [Fact]
    public void SpendAccumulatesAcrossRunsInOneFile()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "spend.json");
        var first = new SpendBook(path, "run-1", "out");
        first.Add(SpendKind.Reader, new CallCost(1, 0, 1, 0, 0.25m, false));
        first.Add(SpendKind.Judge, new CallCost(1, 0, 1, 0, 0.25m, false));
        Assert.Equal(0.5m, first.RunUsd);
        Assert.Equal(0.25m, first.RunUsdOf(SpendKind.Judge));
        var second = new SpendBook(path, "run-2", "out");
        second.Add(SpendKind.Reader, new CallCost(1, 0, 1, 0, 0.1m, false));
        Assert.Equal(0.1m, second.RunUsd);
        Assert.Equal(0.6m, second.AllRunsUsd);
        Assert.Equal(0.35m, second.AllRunsUsdOf(SpendKind.Reader));
        Assert.Equal(0.25m, second.AllRunsUsdOf(SpendKind.Judge));
        Assert.Equal(1, second.RunCalls);
    }

    [Fact]
    public void ALineWrittenBeforeTheSplitCountsAsTheReaders()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "spend.json");
        File.WriteAllText(path, "{\"TotalUsd\": 0.6, \"Runs\": [{\"RunId\": \"old\", \"Input\": \"x\", \"Started\": \"2026-11-03T20:00:00+01:00\", \"Usd\": 0.6, \"Calls\": 3, \"InputTokens\": 10, \"OutputTokens\": 5}]}");
        var book = new SpendBook(path, "run-2", "out");
        Assert.Equal(0.6m, book.AllRunsUsdOf(SpendKind.Reader));
        Assert.Equal(0m, book.AllRunsUsdOf(SpendKind.Judge));
        Assert.Equal(0.6m, book.AllRunsUsd);
    }
}

public sealed class ReaderJsonTests
{
    [Fact]
    public void FencedAndPaddedJsonIsRead()
    {
        Assert.Equal("yes", Reader.ParseJson("```json\n{\"a\": \"yes\"}\n```")!["a"]!.GetValue<string>());
        Assert.Equal("yes", Reader.ParseJson("Here you go: {\"a\": \"yes\"} done")!["a"]!.GetValue<string>());
        Assert.Null(Reader.ParseJson("no json here"));
        Assert.Null(Reader.ParseJson("{\"a\": }"));
    }
}

public sealed class TypeSafeParsingTests
{
    [Fact]
    public void EveryAnswerTypeIsReadStrictly()
    {
        var questions = new JsonObject
        {
            ["n"] = new JsonObject { ["type"] = "noul", ["instructions"] = "q" },
            ["c"] = new JsonObject { ["type"] = "choice", ["instructions"] = "q", ["criteria"] = new JsonObject { ["a"] = "a", ["b"] = "b" } },
            ["s"] = new JsonObject { ["type"] = "score", ["instructions"] = "q", ["criteria"] = new JsonArray("low", "high") },
        };
        var body = """
            {"model":"jev-1.14.0","answers":{
              "n":{"type":"noul","noul":0.91},
              "c":{"type":"choice","choice":"a","confidence":0.6,"probabilities":{"a":0.8,"b":0.2}},
              "s":{"type":"score","score":0.7,"confidence":0.5,"legend":{"0":"low","1":"high"},"probabilities":{"0":0.3,"1":0.7}}
            },"usage":{"input_tokens":120,"output_tokens":12}}
            """;
        var reply = TypeSafeJudgeModel.Parse(body, questions);
        Assert.Equal("jev-1.14.0", reply.Model);
        Assert.Equal(0.91, reply.Noul("n"));
        var choice = Assert.IsType<ChoiceAnswer>(reply.Answers["c"]);
        Assert.Equal("a", choice.Choice);
        Assert.Equal(0.8, choice.Probabilities["a"]);
        var score = Assert.IsType<ScoreAnswer>(reply.Answers["s"]);
        Assert.Equal(0.7, score.Score);
        Assert.Equal("high", score.Legend["1"]);
        Assert.Equal(120, reply.InputTokens);
    }

    [Fact]
    public void AMissingOrUnreadableAnswerIsAnErrorNeverAnAnswer()
    {
        var questions = new JsonObject { ["n"] = new JsonObject { ["type"] = "noul", ["instructions"] = "q" } };
        var missing = Assert.Throws<JudgeUnavailableException>(() => TypeSafeJudgeModel.Parse("{\"model\":\"jev\",\"answers\":{}}", questions));
        Assert.Equal("answer_missing_n", missing.Reason);
        var unreadable = Assert.Throws<JudgeUnavailableException>(() => TypeSafeJudgeModel.Parse("{\"model\":\"jev\",\"answers\":{\"n\":{\"noul\":\"yes\"}}}", questions));
        Assert.Equal("answer_unreadable_n", unreadable.Reason);
        Assert.Throws<JudgeUnavailableException>(() => TypeSafeJudgeModel.Parse("not json", questions));
    }

    [Fact]
    public void PlaceholdersAreFilledInACopyAndThePackCopyIsUntouched()
    {
        var set = new JsonObject
        {
            ["q"] = new JsonObject { ["type"] = "noul", ["instructions"] = new JsonObject { ["question"] = "Is `{{field}}` a ledger?", ["focus"] = "look" } },
        };
        var filled = JudgeQuestions.Fill(set, new Dictionary<string, string> { ["field"] = "inputs.f1.text" });
        Assert.Equal("Is `inputs.f1.text` a ledger?", filled["q"]!["instructions"]!["question"]!.GetValue<string>());
        Assert.Equal("Is `{{field}}` a ledger?", set["q"]!["instructions"]!["question"]!.GetValue<string>());
    }
}

public sealed class AnswerStoreTests
{
    [Fact]
    public void AnswersAreReadByHeadingAndKeptOnSet()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "answers.md");
        File.WriteAllText(path, "# Answers\n\nIntro line.\n\n## kind:docs/a.md\nspec\n\n## ceiling:1\ncontinue, up to $5 more\n");
        var store = new AnswerStore(path);
        Assert.Equal("spec", store.Get("kind:docs/a.md"));
        Assert.Equal("continue, up to $5 more", store.Get("ceiling:1"));
        Assert.Null(store.Get("nothing"));
        store.Set("kind:docs/b.md", "plan");
        var reloaded = new AnswerStore(path);
        Assert.Equal("plan", reloaded.Get("kind:docs/b.md"));
        Assert.Equal("spec", reloaded.Get("kind:docs/a.md"));
        Assert.NotEqual(store.Hash, new AnswerStore(Path.Combine(temp.Path, "empty.md")).Hash);
    }

    [Fact]
    public void AnAnswerMatchesAnOptionCaseInsensitivelyOrStaysFreeText()
    {
        var question = new Question("k", "p", "which?", [new QuestionOption("spec", "a spec"), new QuestionOption("plan", "a plan")], "other");
        Assert.Equal("spec", Answer.Match(question, " SPEC ", "person").Option);
        Assert.Null(Answer.Match(question, "it is really a report", "person").Option);
    }

    [Fact]
    public async Task TheFilesPolicyAnswersFromTheStoreAndRecordsTheRest()
    {
        using var temp = new TempFolder();
        var store = new AnswerStore(Path.Combine(temp.Path, "answers.md"));
        store.Set("k1", "plan");
        var book = new QuestionBook();
        var policy = new WrittenQuestionPolicy(store, book);
        var q1 = new Question("k1", "p", "which?", [new QuestionOption("spec", "a spec"), new QuestionOption("plan", "a plan")], "other");
        var q2 = new Question("k2", "p", "and this?", [new QuestionOption("yes", "y")], "no");
        Assert.Equal("plan", (await policy.AskAsync(q1, CancellationToken.None))!.Option);
        Assert.Null(await policy.AskAsync(q2, CancellationToken.None));
        Assert.Equal(1, book.Unanswered);
        var questionsPath = Path.Combine(temp.Path, "questions.md");
        book.Write(questionsPath);
        var text = File.ReadAllText(questionsPath);
        Assert.Contains("## k2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## k1", text, StringComparison.Ordinal);
    }
}
