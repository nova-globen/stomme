namespace Stomme.Conductor.Ledger;

/// <summary>Where a decision's evidence is: a path and line, a quote, or both (ADR 0002).</summary>
public sealed record Evidence(string? Path, int? Line, string? Quote)
{
    public override string ToString()
    {
        var where = Path is null ? "" : Line is null ? Path : $"{Path}:{Line}";
        var quote = string.IsNullOrEmpty(Quote) ? "" : $" \"{Quote}\"";
        return (where + quote).Trim();
    }
}

/// <summary>What one model call cost (ADR 0008). Reasoning tokens are part of the output tokens and are shown for information.</summary>
public sealed record CallCost(
    long InputTokens, long CachedInputTokens, long OutputTokens, long ReasoningTokens, decimal Usd, bool Estimated)
{
    public static CallCost None { get; } = new(0, 0, 0, 0, 0m, false);

    public CallCost Add(CallCost other) => new(
        InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens,
        OutputTokens + other.OutputTokens,
        ReasoningTokens + other.ReasoningTokens,
        Usd + other.Usd,
        Estimated || other.Estimated);
}

/// <summary>Who made a decision (ADR 0002).</summary>
public static class DecidedBy
{
    public const string Rule = "rule";
    public const string Reader = "reader";
    public const string Judge = "judge";
    public const string Person = "person";
}

/// <summary>
/// One record of the decision log (ADR 0002): what was decided, by whom, the evidence, the reasoning kept
/// verbatim when the reader gave one, the confidence, the alternatives, and what it cost. A model call is a
/// record of kind "call" so that the log carries every call's tokens and cost (ADR 0008).
/// </summary>
public sealed record DecisionRecord
{
    public int Seq { get; init; }

    public DateTimeOffset Time { get; init; }

    /// <summary>The run this record belongs to; log.jsonl is append-only across the runs of one output folder.</summary>
    public string Run { get; init; } = "";

    public required string Phase { get; init; }

    /// <summary>A short kind, such as gate-in, gate-out, call, question, or a playbook's own kinds.</summary>
    public required string Kind { get; init; }

    /// <summary>What the decision is about: a path, an id, a call's purpose.</summary>
    public required string Subject { get; init; }

    public required string Decision { get; init; }

    /// <summary>One of <see cref="DecidedBy"/>.</summary>
    public required string By { get; init; }

    /// <summary>The model that decided, when a model did.</summary>
    public string? Model { get; init; }

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    public string? Reasoning { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyList<string> Alternatives { get; init; } = [];

    public CallCost? Cost { get; init; }

    /// <summary>True when the reply came from the cache and nothing was spent.</summary>
    public bool Cached { get; init; }

    /// <summary>Marks such as injection-flagged, discarded, estimated.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>The key of the question recorded for a person, when one was.</summary>
    public string? Question { get; init; }

    /// <summary>The call record this decision came from.</summary>
    public int? Call { get; init; }

    /// <summary>Numbers worth keeping: a judge's probabilities, counts, usage details.</summary>
    public IReadOnlyDictionary<string, double>? Figures { get; init; }
}
