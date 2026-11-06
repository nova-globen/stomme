using System.Text.Json.Nodes;

namespace Stomme.Conductor.Models;

/// <summary>One typed answer from the judge. The probabilities are calibrated; code decides on them.</summary>
public abstract record JudgeAnswer;

/// <summary>A yes/no statement's probability of being true, 0 (no) to 1 (yes).</summary>
public sealed record NoulAnswer(double Noul) : JudgeAnswer;

/// <summary>One option picked, with a probability per option.</summary>
public sealed record ChoiceAnswer(string Choice, double? Confidence, IReadOnlyDictionary<string, double> Probabilities) : JudgeAnswer;

/// <summary>The expected level index over ordered levels, with a probability per level.</summary>
public sealed record ScoreAnswer(
    double Score, double? Confidence, IReadOnlyDictionary<string, double> Probabilities, IReadOnlyDictionary<string, string> Legend) : JudgeAnswer;

/// <summary>The judge's reply: every question answered, the model that answered, and the usage.</summary>
public sealed record JudgeReply(string Model, IReadOnlyDictionary<string, JudgeAnswer> Answers, long InputTokens, long OutputTokens)
{
    public double Noul(string id) => Answers[id] is NoulAnswer n
        ? n.Noul
        : throw new JudgeUnavailableException("answer_not_noul_" + id);
}

/// <summary>
/// There is no judgement to be had: unreachable, refused, late, or an unreadable answer. The reason is short
/// and secret-free; no message ever carries the key or the state.
/// </summary>
public sealed class JudgeUnavailableException(string reason, Exception? inner = null)
    : Exception($"The judge gave no usable answer ({reason}).", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// A System One model answering typed questions about a state (ADR 0004, 0013): the port Fravashi's broker
/// adapter also implements. An adapter sends state and questions as given and answers every question, or
/// throws <see cref="JudgeUnavailableException"/>; it never guesses.
/// </summary>
public interface IJudgeModel
{
    /// <summary>A short, secret-free name for the record, such as typesafe.</summary>
    string Name { get; }

    Task<JudgeReply> AskAsync(JsonNode state, JsonObject questions, CancellationToken cancellationToken);
}
