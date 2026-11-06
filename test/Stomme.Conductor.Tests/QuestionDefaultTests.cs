using System.Text.Json.Nodes;
using Stomme.Conductor.Policies;
using Stomme.Conductor.State;

namespace Stomme.Conductor.Tests;

/// <summary>
/// A question that offers options has one of their values as its default, never a sentence, whoever supplied the
/// default: a phase, a reader's reply passed on, or a run made by an earlier engine and kept in the state.
/// </summary>
public sealed class QuestionDefaultTests
{
    private static readonly QuestionOption[] Kinds =
    [
        new("open-ledger", "open work"), new("ledger", "any ledger"), new("guide", "a guide"), new("other", "anything else"),
    ];

    private static Question Ask(string key, string defaultAnswer, string? reason = null) =>
        new(key, "orientation", "What is it?", Kinds, defaultAnswer) { DefaultReason = reason };

    [Fact]
    public void ADefaultThatIsASentenceBecomesTheOneOptionItNamesAndTheSentenceIsKept()
    {
        // A reader's reply carried on as the default: a sentence that names one option.
        var settled = Ask("kind:a", "other (it is kept as something of kind other)").WithOptionDefault();
        Assert.Equal("other", settled.DefaultIfUnanswered);
        Assert.Contains("other (it is kept as something of kind other)", settled.DefaultReason, StringComparison.Ordinal);
        Assert.True(settled.DefaultIsAnOption);

        // A reason the phase gave stays, with what was first given beside it.
        var withReason = Ask("kind:b", "probably a guide, I think", "the reader's reading").WithOptionDefault();
        Assert.Equal("guide", withReason.DefaultIfUnanswered);
        Assert.StartsWith("the reader's reading", withReason.DefaultReason, StringComparison.Ordinal);
        Assert.Contains("probably a guide, I think", withReason.DefaultReason, StringComparison.Ordinal);

        // An option is named only as a word of its own: "open-ledger" does not name "ledger".
        Assert.Equal("open-ledger", Ask("kind:c", "an open-ledger of work").WithOptionDefault().DefaultIfUnanswered);
    }

    [Fact]
    public void ADefaultThatNamesNoOptionOrSeveralBecomesTheSafestOne()
    {
        // Several named, none named: the host's safest option, else the first.
        Assert.Equal("other", Ask("kind:a", "a guide or a ledger, hard to say").WithOptionDefault("other").DefaultIfUnanswered);
        Assert.Equal("other", Ask("kind:b", "no idea at all").WithOptionDefault("other").DefaultIfUnanswered);
        Assert.Equal("open-ledger", Ask("kind:c", "no idea at all").WithOptionDefault().DefaultIfUnanswered);
        Assert.Equal("open-ledger", Ask("kind:d", "no idea at all").WithOptionDefault("not-an-option").DefaultIfUnanswered);
        Assert.Contains("no idea at all", Ask("kind:e", "no idea at all").WithOptionDefault("other").DefaultReason, StringComparison.Ordinal);

        // A value in another case is the value; a value stays as it is; a question without options is left alone.
        Assert.Equal("guide", Ask("kind:f", "Guide").WithOptionDefault().DefaultIfUnanswered);
        var plain = Ask("kind:g", "guide", "why");
        Assert.Same(plain, plain.WithOptionDefault("other"));
        var free = new Question("history:older", "decisions", "Where is the older history?", [], "nothing is read from anywhere else");
        Assert.Same(free, free.WithOptionDefault("other"));
        Assert.True(free.DefaultIsAnOption);
        Assert.False(Ask("kind:h", "a sentence").DefaultIsAnOption);
    }

    [Fact]
    public async Task EveryQuestionIsWrittenWithAnOptionAsItsDefaultThisRunsAndThoseKeptFromAnEarlierEngine()
    {
        using var temp = new TempFolder();
        using var state = RunState.Open(Path.Combine(temp.Path, "state.sqlite"));

        // As an earlier engine stored it: a sentence as the default, straight into the state.
        state.UpsertQuestion(Ask("kind:stored", "other (it is kept as something of kind other)"), "an-earlier-run", null);
        state.UpsertQuestion(Ask("kind:stored-unclear", "whatever fits best"), "an-earlier-run", null);

        var book = new QuestionBook(state, "this-run") { Safest = q => q.Key.StartsWith("kind:", StringComparison.Ordinal) ? "other" : null };

        // This run's: asked through the policy that asks nobody, with a reader's sentence as its default.
        await new NoneQuestionPolicy(book).AskAsync(Ask("kind:asked", "I would call it a guide"), CancellationToken.None);

        var written = book.Written();
        Assert.Equal(["kind:asked", "kind:stored", "kind:stored-unclear"], written.Select(q => q.Question.Key));
        Assert.All(written, q => Assert.True(q.Question.DefaultIsAnOption));
        Assert.All(book.Open(), q => Assert.True(q.Question.DefaultIsAnOption));

        var path = Path.Combine(temp.Path, "questions.md");
        book.Write(path);
        var json = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")))!["questions"]!.AsArray();
        string Default(string key) => json.Single(q => q!["key"]!.GetValue<string>() == key)!["default"]!.GetValue<string>();
        string Reason(string key) => json.Single(q => q!["key"]!.GetValue<string>() == key)!["defaultReason"]!.GetValue<string>();
        Assert.Equal("guide", Default("kind:asked"));
        Assert.Equal("other", Default("kind:stored"));
        Assert.Equal("other", Default("kind:stored-unclear"));
        Assert.Contains("I would call it a guide", Reason("kind:asked"), StringComparison.Ordinal);
        Assert.Contains("it is kept as something of kind other", Reason("kind:stored"), StringComparison.Ordinal);
        Assert.Contains("whatever fits best", Reason("kind:stored-unclear"), StringComparison.Ordinal);
        foreach (var question in json)
        {
            Assert.Contains(question!["options"]!.AsArray(), o => o!["value"]!.GetValue<string>() == question["default"]!.GetValue<string>());
        }

        Assert.DoesNotContain("Default if unanswered: other (", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("Default if unanswered: other", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
