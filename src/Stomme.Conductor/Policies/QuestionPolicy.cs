using System.Globalization;
using System.Text;

namespace Stomme.Conductor.Policies;

/// <summary>One option a person may pick.</summary>
public sealed record QuestionOption(string Value, string Meaning);

/// <summary>
/// Something a run could not settle (ADR 0007). The key is stable per input and per thing asked, so an
/// answer given once holds on later runs. Free text is always allowed beside the options.
/// </summary>
public sealed record Question(
    string Key,
    string Phase,
    string Text,
    IReadOnlyList<QuestionOption> Options,
    string DefaultIfUnanswered)
{
    /// <summary>Evidence or context that helps a person answer.</summary>
    public string? Context { get; init; }

    /// <summary>True when the run cannot go on without the answer (the ceiling, ADR 0008).</summary>
    public bool Blocking { get; init; }

    /// <summary>What the question is about, for a consumer's dialog: a kind from the host's closed list, the inputs' paths, the keys of the things concerned, how many.</summary>
    public QuestionAbout? About { get; init; }

    /// <summary>Why the default is what it is.</summary>
    public string? DefaultReason { get; init; }

    /// <summary>True when the default is something a consumer can act on: one of the options' values, or anything at all when the question offers no options.</summary>
    public bool DefaultIsAnOption => Options.Count == 0 || Options.Any(o => string.Equals(o.Value, DefaultIfUnanswered, StringComparison.Ordinal));

    /// <summary>
    /// The question with a default that is one of its options' values, whoever supplied the default (a phase, a
    /// model's reply, an earlier run's state): a default that is already a value stays; one that names exactly one
    /// option, as a word of its own, becomes that option; any other becomes <paramref name="safest"/> when that is
    /// an option, else the first option. What was first given is kept in <see cref="DefaultReason"/>. A question
    /// without options is returned as it is.
    /// </summary>
    public Question WithOptionDefault(string? safest = null)
    {
        if (Options.Count == 0)
        {
            return this;
        }

        var given = (DefaultIfUnanswered ?? "").Trim();
        var exact = Options.FirstOrDefault(o => string.Equals(o.Value, given, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return string.Equals(exact.Value, DefaultIfUnanswered, StringComparison.Ordinal) ? this : this with { DefaultIfUnanswered = exact.Value };
        }

        var named = Options.Select(o => o.Value).Where(v => Names(given, v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var chosen = named.Count == 1
            ? named[0]
            : Options.FirstOrDefault(o => string.Equals(o.Value, safest, StringComparison.OrdinalIgnoreCase))?.Value ?? Options[0].Value;
        var firstGiven = $"as first given: \"{given}\"";
        var reason = given.Length == 0 ? DefaultReason
            : string.IsNullOrWhiteSpace(DefaultReason) ? firstGiven
            : DefaultReason.Contains(given, StringComparison.Ordinal) ? DefaultReason
            : $"{DefaultReason} ({firstGiven})";
        return this with { DefaultIfUnanswered = chosen, DefaultReason = reason };
    }

    /// <summary>True when <paramref name="text"/> holds <paramref name="value"/> as a word of its own: "open-ledger" is not named by "ledger".</summary>
    private static bool Names(string text, string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        static bool Joins(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
        for (var at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = at == 0 || !Joins(text[at - 1]);
            var after = at + value.Length >= text.Length || !Joins(text[at + value.Length]);
            if (before && after)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a question is about: a kind from the host's closed list, the inputs' paths, the keys concerned, and how many.</summary>
public sealed record QuestionAbout(string Kind, IReadOnlyList<string> Paths, IReadOnlyList<string> Keys, int? Rows);

/// <summary>A person's answer: the option it matches, if any, and the text as written.</summary>
public sealed record Answer(string Key, string Text, string? Option, string By)
{
    public static Answer Match(Question question, string text, string by)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var option = question.Options.FirstOrDefault(o => string.Equals(o.Value, trimmed, StringComparison.OrdinalIgnoreCase))?.Value;
        return new Answer(question.Key, trimmed, option, by);
    }
}

/// <summary>How a run asks (ADR 0007): written, interactive, none, or the host's own.</summary>
public interface IQuestionPolicy
{
    /// <summary>The policy's name for the log and the summary.</summary>
    string Name { get; }

    /// <summary>Asks, or records; null means "no answer now, use the default".</summary>
    Task<Answer?> AskAsync(Question question, CancellationToken cancellationToken);
}

/// <summary>
/// The answers a person gave, kept in answers.md in the output folder: one heading per question key, the
/// answer under it. Read at the start of a run, written when the interactive policy gets an answer.
/// </summary>
public sealed class AnswerStore
{
    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public AnswerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        if (File.Exists(path))
        {
            Load(File.ReadAllText(path));
        }

        // answers.json beside it, for a consumer: { "<key>": { "text", "option", "by" } }; an option counts as the text.
        var json = System.IO.Path.ChangeExtension(path, ".json");
        if (File.Exists(json))
        {
            LoadJson(File.ReadAllText(json));
        }
    }

    internal void LoadJson(string json)
    {
        System.Text.Json.Nodes.JsonNode? parsed;
        try
        {
            parsed = System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // A person's answers never become defaults by a typo: the run refuses to start.
            throw new InvalidOperationException($"{System.IO.Path.ChangeExtension(Path, ".json")} is not valid JSON ({ex.Message}); fix it or remove it, so the answers are not silently lost.", ex);
        }

        if (parsed is not System.Text.Json.Nodes.JsonObject root)
        {
            throw new InvalidOperationException($"{System.IO.Path.ChangeExtension(Path, ".json")} must be an object keyed by question key.");
        }

        foreach (var (key, value) in root)
        {
            string? text;
            string? option = null;
            if (value is System.Text.Json.Nodes.JsonObject answer)
            {
                text = Scalar(answer["text"]);
                option = Scalar(answer["option"]);
            }
            else
            {
                text = Scalar(value);
            }

            if (string.IsNullOrWhiteSpace(key) || (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(option)))
            {
                continue;
            }

            // The person's own text is the answer; the option, when given too, is kept beside it.
            _answers[key.Trim()] = (string.IsNullOrWhiteSpace(text) ? option! : text).Trim();
            if (!string.IsNullOrWhiteSpace(option))
            {
                _options[key.Trim()] = option.Trim();
            }
        }

        static string? Scalar(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonValue v ? v.ToString() : node?.ToJsonString();
    }

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    /// <summary>
    /// The answer a person already gave to this question, or null: the text as written, the option it names (or the
    /// one chosen beside the text in answers.json), and where it came from. Every policy reads answers through this,
    /// so an answer given once holds whichever way the run asks (ADR 0007).
    /// </summary>
    public Answer? Stored(Question question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var text = Get(question.Key);
        if (text is null)
        {
            return null;
        }

        var answer = Answer.Match(question, text, "person (answers.md)");
        if (GetOption(question.Key) is { } chosen)
        {
            var option = question.Options.FirstOrDefault(o => string.Equals(o.Value, chosen, StringComparison.OrdinalIgnoreCase))?.Value;
            answer = answer with { Option = option ?? answer.Option, By = "person (answers.json)" };
        }

        return answer;
    }

    /// <summary>The option a person chose beside their text, when the answer came as JSON with both; null otherwise.</summary>
    public string? GetOption(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return _options.TryGetValue(key.Trim(), out var option) ? option : null;
        }
    }

    public string Path { get; }

    public int Count => _answers.Count;

    /// <summary>A hash of the answers, so a checkpoint knows when they changed.</summary>
    public string Hash
    {
        get
        {
            lock (_gate)
            {
                var text = string.Join('\n', _answers.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value));
                return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
            }
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _answers.TryGetValue(key, out var answer) ? answer : null;
        }
    }

    public void Set(string key, string answer)
    {
        lock (_gate)
        {
            _answers[key] = answer.Trim();
            Save();
        }
    }

    internal void Load(string text)
    {
        string? key = null;
        var body = new StringBuilder();
        void Flush()
        {
            if (key is not null && body.ToString().Trim().Length > 0)
            {
                _answers[key] = body.ToString().Trim();
            }

            body.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                key = raw[3..].Trim();
            }
            else if (key is not null && !raw.StartsWith("# ", StringComparison.Ordinal))
            {
                body.AppendLine(raw);
            }
        }

        Flush();
    }

    private void Save()
    {
        var text = new StringBuilder();
        text.AppendLine("# Answers");
        text.AppendLine();
        text.AppendLine("One heading per question key (copied from questions.md), the answer under it: an option's value, or free text.");
        text.AppendLine();
        foreach (var (key, answer) in _answers.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            text.AppendLine("## " + key);
            text.AppendLine(answer);
            text.AppendLine();
        }

        File.WriteAllText(Path, text.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>What was asked and how it went, for questions.md and the log.</summary>
public sealed record AskedQuestion(Question Question, Answer? Answer, DateTimeOffset At);

/// <summary>
/// Every question of a run, in order, mirrored into the run state so questions persist by key across runs
/// (ADR 0017); questions.md lists every question still open in the state.
/// </summary>
public sealed class QuestionBook
{
    private readonly List<AskedQuestion> _asked = [];
    private readonly Lock _gate = new();
    private readonly State.RunState? _state;
    private readonly string _runId;

    /// <summary>The host's account of what a question is about, for one asked without it; null leaves the about empty.</summary>
    public Func<Question, QuestionAbout?>? Describe { get; set; }

    /// <summary>The host's word on which option holds when nobody answers and a default names none of them or several; null means the first option.</summary>
    public Func<Question, string?>? Safest { get; set; }

    /// <summary>The question as it is shown and written: its default is one of its options' values (see <see cref="Question.WithOptionDefault"/>).</summary>
    public Question Settle(Question question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return question.WithOptionDefault(Safest?.Invoke(question));
    }

    /// <summary>
    /// Every question as it is written for a consumer, in key order: this run's and those still open from earlier
    /// runs, each with a default that is one of its options. The one list the writer and a check both read.
    /// </summary>
    public IReadOnlyList<AskedQuestion> Written()
    {
        var asked = Asked;
        return asked.Concat(Open().Where(o => asked.All(a => a.Question.Key != o.Question.Key)))
            .Select(a => a with { Question = Settle(a.Question) })
            .OrderBy(a => a.Question.Key, StringComparer.Ordinal)
            .ToList();
    }

    public QuestionBook()
        : this(null, "")
    {
    }

    public QuestionBook(State.RunState? state, string runId)
    {
        _state = state;
        _runId = runId;
    }

    public IReadOnlyList<AskedQuestion> Asked
    {
        get
        {
            lock (_gate)
            {
                return _asked.ToArray();
            }
        }
    }

    public int Unanswered => Open().Count;

    public void Record(Question question, Answer? answer)
    {
        lock (_gate)
        {
            _asked.RemoveAll(q => q.Question.Key == question.Key);
            _asked.Add(new AskedQuestion(question, answer, DateTimeOffset.Now));
        }

        _state?.UpsertQuestion(question, _runId, answer);
    }

    /// <summary>Withdraws an open question a later judgment settled (ADR 0007): true when one was open.</summary>
    public bool Withdraw(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _asked.RemoveAll(q => q.Question.Key == key && q.Answer is null) > 0;
        }

        if (_state is not null)
        {
            removed = _state.WithdrawQuestion(key) || removed;
        }

        return removed;
    }

    /// <summary>
    /// The questions still open: every one in the state without an answer, else this run's. Each comes settled, so one
    /// an earlier run stored with a sentence as its default reads with an option's value here, in every place that shows it.
    /// </summary>
    public IReadOnlyList<AskedQuestion> Open()
    {
        if (_state is null)
        {
            return Asked.Where(q => q.Answer is null).Select(q => q with { Question = Settle(q.Question) }).ToList();
        }

        return _state.Questions()
            .Where(q => q.Answer is null)
            .Select(q => new AskedQuestion(Settle(q.Question), null, q.Asked))
            .ToList();
    }

    /// <summary>Writes questions.md: the unanswered questions with their options and defaults, and how to answer; and the same list as JSON beside it, for a consumer.</summary>
    public void Write(string path)
    {
        var open = Open();
        var json = new System.Text.Json.Nodes.JsonArray();
        foreach (var asked in Written())
        {
            var q = asked.Question;
            var about = q.About ?? Describe?.Invoke(q);
            json.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = q.Key,
                ["phase"] = q.Phase,
                ["text"] = q.Text.Trim(),
                ["context"] = q.Context,
                ["about"] = about is null ? null : new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = about.Kind,
                    ["paths"] = new System.Text.Json.Nodes.JsonArray(about.Paths.Select(p => (System.Text.Json.Nodes.JsonNode)p).ToArray()),
                    ["itemKeys"] = new System.Text.Json.Nodes.JsonArray(about.Keys.Select(k => (System.Text.Json.Nodes.JsonNode)k).ToArray()),
                    ["rows"] = about.Rows,
                },
                ["options"] = new System.Text.Json.Nodes.JsonArray(q.Options.Select(o => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["value"] = o.Value, ["meaning"] = o.Meaning }).ToArray()),
                ["default"] = q.DefaultIfUnanswered,
                ["defaultReason"] = q.DefaultReason,
                ["blocking"] = q.Blocking,
                ["asked"] = asked.At,
                ["answer"] = asked.Answer is null ? null : new System.Text.Json.Nodes.JsonObject { ["text"] = asked.Answer.Text, ["option"] = asked.Answer.Option, ["by"] = asked.Answer.By },
            });
        }

        var root = new System.Text.Json.Nodes.JsonObject { ["questions"] = json };
        File.WriteAllText(System.IO.Path.ChangeExtension(path, ".json"), root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        var text = new StringBuilder();
        text.AppendLine("# Questions");
        text.AppendLine();
        if (open.Count == 0)
        {
            text.AppendLine("The run settled everything it met; nothing waits on a person.");
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{open.Count} question(s) the run could not settle. To answer: copy a question's heading into `answers.md` and write the answer under it (an option's value, or free text), then run again; the cache makes a re-run nearly free. An unanswered question keeps its default.");
            text.AppendLine();
            foreach (var asked in open)
            {
                var q = asked.Question;
                text.AppendLine("## " + q.Key);
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"Phase: {q.Phase}. Asked: {asked.At:yyyy-MM-dd HH:mm}.{(q.Blocking ? " The run stopped here until this is answered." : "")}");
                text.AppendLine();
                text.AppendLine(q.Text.Trim());
                text.AppendLine();
                if (q.Options.Count > 0)
                {
                    text.AppendLine("Options:");
                    foreach (var option in q.Options)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"- `{option.Value}`: {option.Meaning}");
                    }

                    text.AppendLine();
                }

                text.AppendLine("Default if unanswered: " + q.DefaultIfUnanswered);
                if (!string.IsNullOrWhiteSpace(q.Context))
                {
                    text.AppendLine();
                    text.AppendLine("Context:");
                    text.AppendLine();
                    foreach (var line in q.Context.Trim().Split('\n'))
                    {
                        text.AppendLine("> " + line.TrimEnd());
                    }
                }

                text.AppendLine();
            }
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>The written policy (ADR 0007): questions go to questions.md for a person; answers come from answers.md on the next run.</summary>
public sealed class WrittenQuestionPolicy(AnswerStore store, QuestionBook book) : IQuestionPolicy
{
    public string Name => "written";

    public Task<Answer?> AskAsync(Question question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = store.Stored(question);
        book.Record(question, answer);
        return Task.FromResult(answer);
    }
}

/// <summary>The interactive policy (ADR 0007): a callback the host answers at once (the console's prompt under --ask); answers are kept in answers.md.</summary>
public sealed class InteractiveQuestionPolicy(
    AnswerStore store, QuestionBook book, Func<Question, CancellationToken, Task<string?>> ask) : IQuestionPolicy
{
    public string Name => "interactive";

    public async Task<Answer?> AskAsync(Question question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (store.Stored(question) is { } kept)
        {
            book.Record(question, kept);
            return kept;
        }

        var text = await ask(question, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            book.Record(question, null);
            return null;
        }

        store.Set(question.Key, text);
        var answer = Answer.Match(question, text, "person (console)");
        book.Record(question, answer);
        return answer;
    }
}

/// <summary>
/// The none policy (ADR 0007): nobody is asked; every unsettled point is recorded and the run continues with the
/// safest default. A point a person already answered is not unsettled: with the answers at hand the policy honours
/// them, as the others do, so answers written after a run are never silently dropped by the way the next run asks.
/// </summary>
public sealed class NoneQuestionPolicy(QuestionBook book, AnswerStore? store = null) : IQuestionPolicy
{
    public string Name => "none";

    public Task<Answer?> AskAsync(Question question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = store?.Stored(question);
        book.Record(question, answer);
        return Task.FromResult(answer);
    }
}
