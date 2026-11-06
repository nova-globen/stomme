using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.State;

namespace Stomme.Conductor.Runner;

/// <summary>Where the engine's own output goes (ADR 0005, 0017); a host keeps its own paths beside these.</summary>
public sealed class OutputPaths
{
    public OutputPaths(string root)
    {
        Root = root;
        Cache = Path.Combine(root, "cache");
        LogJsonl = Path.Combine(root, "log.jsonl");
        LogMarkdown = Path.Combine(root, "log.md");
        Questions = Path.Combine(root, "questions.md");
        Answers = Path.Combine(root, "answers.md");
        Spend = Path.Combine(root, "spend.json");
        StateDatabase = Path.Combine(root, "state.sqlite");
        State = Path.Combine(root, "state.md");
    }

    public string Root { get; }
    public string Cache { get; }
    public string LogJsonl { get; }
    public string LogMarkdown { get; }
    public string Questions { get; }
    public string Answers { get; }
    public string Spend { get; }
    public string StateDatabase { get; }
    public string State { get; }
}

/// <summary>Everything a phase needs from the engine, built once per run; a host wraps it with what its own phases need.</summary>
public sealed class RunContext
{
    private readonly Lock _renderGate = new();
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private string _doingNow = "starting";

    public required ConductorOptions Options { get; init; }
    public required Playbook Playbook { get; init; }
    public required DecisionLog Log { get; init; }
    public required GatedReader Reader { get; init; }
    public required Judge Judge { get; init; }
    public required IQuestionPolicy Questions { get; init; }
    public required QuestionBook Book { get; init; }
    public required AnswerStore Answers { get; init; }
    public required SpendBook Spend { get; init; }
    public required string RunId { get; init; }
    public required OutputPaths Paths { get; init; }
    public required Action<string> Progress { get; init; }
    public required RunState State { get; init; }
    public required Glossary Glossary { get; init; }

    /// <summary>The use of the engine this run serves (ADR 0018): its inputs, phases, backup and output.</summary>
    public required IPlaybookHost Host { get; init; }

    /// <summary>What the host's inputs looked like against the state (ADR 0017); null until the host reconciled them.</summary>
    public InputDiff? Diff { get; private set; }

    /// <summary>The phases completed this run, in order.</summary>
    public List<string> PhasesDone { get; } = [];

    /// <summary>The serializer for the state and the output's JSON: camelCase, nulls left out, readable.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The serializer for what the reader answers: snake_case, forgiving.</summary>
    public static JsonSerializerOptions ReaderJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Records what the host reads on the run's row of the state: a path or a name, a version such as a commit, and a note.</summary>
    public void DescribeSource(string source, string? version, string? note) => State.SetRunSource(RunId, source, version, note);

    /// <summary>
    /// Compares the host's inputs, by key and hash, with what the state knew (ADR 0017) and logs the result:
    /// unchanged inputs keep everything derived from them, changed or new ones have their units redone, gone
    /// ones are marked and never deleted.
    /// </summary>
    public InputDiff ReconcileInputs(IEnumerable<InputRecord> inputs)
    {
        var diff = State.ReconcileInputs(inputs, RunId);
        Diff = diff;
        Log.Record(new DecisionRecord
        {
            Phase = "run",
            Kind = "inputs",
            Subject = "the state against the inputs",
            Decision = diff.IsFirstRun
                ? $"first run into this state: {diff.Added} inputs recorded"
                : $"{diff.Unchanged} unchanged (everything derived from them is kept), {diff.Changed} changed and {diff.Added} added (their units are redone), {diff.Gone} gone (marked, never deleted)",
            By = DecidedBy.Rule,
            Figures = new Dictionary<string, double> { ["unchanged"] = diff.Unchanged, ["changed"] = diff.Changed, ["added"] = diff.Added, ["gone"] = diff.Gone },
            Reasoning = diff.GoneKeys.Count == 0 ? null : "gone: " + string.Join(", ", diff.GoneKeys.Take(20)) + (diff.GoneKeys.Count > 20 ? ", ..." : ""),
        });
        return diff;
    }

    /// <summary>Records a question through the policy and logs it; the answer, if any, comes back.</summary>
    public async Task<Answer?> AskAsync(Question question, string subject, string decisionWithoutAnswer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = await Questions.AskAsync(question, cancellationToken).ConfigureAwait(false);
        Log.Record(new DecisionRecord
        {
            Phase = question.Phase,
            Kind = "question",
            Subject = subject,
            Decision = answer is null ? decisionWithoutAnswer : $"answered: {answer.Text}",
            By = answer is null ? DecidedBy.Rule : DecidedBy.Person,
            Reasoning = question.Text,
            Question = question.Key,
        });
        return answer;
    }

    /// <summary>Re-renders state.md and log.md so a person can look at a run that has not finished (ADR 0017), at most every ten seconds unless forced.</summary>
    public void Touch(string doingNow, bool force = false)
    {
        lock (_renderGate)
        {
            _doingNow = doingNow;
            if (!force && DateTimeOffset.Now - _lastRender < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _lastRender = DateTimeOffset.Now;
            try
            {
                StateWriter.Write(this, _doingNow);
                Log.RenderMarkdown();
            }
            catch (IOException)
            {
                // A reader holding the rendering open wins; the next touch writes it.
            }
        }
    }

    /// <summary>
    /// The system prompt of a reader call: the playbook's rules, the glossary's place (filled by the gated
    /// reader after the gate judged it, ADR 0017), the phase's task with its placeholders filled, and the JSON shape.
    /// </summary>
    public string SystemPrompt(string taskPrompt, string schema, IReadOnlyDictionary<string, string>? values = null)
    {
        var task = values is null ? Playbook.Prompt(taskPrompt) : Playbook.Prompt(taskPrompt, values);
        return Playbook.Prompt("rules") + "\n\n{{glossary}}" + task + "\n\n" + Playbook.Schema(schema);
    }

    /// <summary>A short hash over text, for unit input hashes.</summary>
    public static string HashOf(params string?[] parts)
    {
        var text = string.Join("\u001f", parts.Select(p => p ?? ""));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..24];
    }
}

/// <summary>A run stopped on purpose: the ceiling said no, or a blocking question waits for a person (ADR 0007, 0008).</summary>
public sealed class RunStoppedException(RunOutcome outcome, string message) : Exception(message)
{
    public RunOutcome Outcome { get; } = outcome;
}

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    Completed,
    StoppedAfterPhase,
    StoppedAtCeiling,
    StoppedForQuestion,
    Failed,
}
