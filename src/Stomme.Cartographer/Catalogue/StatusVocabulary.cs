using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Playbooks;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Cartographer.Catalogue;

/// <summary>What a status word of the repository maps to, and who said so.</summary>
public sealed record StatusMapping(string Word, string Mapped, string By, string? Condition, string? Meaning);

/// <summary>
/// The repository's status words and their mapped states (ADR 0020 rule 12): the map's vocabulary from
/// orientation, overridden by a person's answers (`status:&lt;word&gt;` in answers.md), which are the only
/// confirmed ones. The mapped states themselves come from the playbook (data/statuses.json).
/// </summary>
public sealed class StatusVocabulary
{
    private readonly Dictionary<string, StatusMapping> _mappings;

    private StatusVocabulary(Dictionary<string, StatusMapping> mappings, IReadOnlyList<string> states, string text, string hash)
    {
        _mappings = mappings;
        States = states;
        Text = text;
        Hash = hash;
    }

    /// <summary>The mapped states a row may have, from the playbook.</summary>
    public IReadOnlyList<string> States { get; }

    /// <summary>The block for the prompt.</summary>
    public string Text { get; }

    /// <summary>A hash over the mappings, for unit input hashes.</summary>
    public string Hash { get; }

    public IReadOnlyCollection<StatusMapping> Mappings => _mappings.Values;

    public StatusMapping? Lookup(string? word) =>
        word is not null && _mappings.TryGetValue(word.Trim(), out var mapping) ? mapping : null;

    public bool IsState(string? value) => value is not null && States.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "as", "at", "by", "into", "until", "except", "since", "on", "with", "for", "after", "before", "in", "to", "of", "and", "or", "the", "a", "an", "but", "pending", "once", "when", "if", "unless",
    };

    private static readonly Regex DatesAndHashes = new(@"\b\d{4}-\d{2}-\d{2}(?:/\d+)?\b|\b[0-9a-f]{7,40}\b|\b(?:at|on)\b|\(\d+ of \d+\)|\b\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The status word of a phrase as a ledger writes it (ADR 0020 rule 12): the leading words before a date, a
    /// hash, a parenthesis, a dash or a connector, so that "DONE 2026-10-21", "CLOSED (2026-09-20)" and
    /// "FIXED at 7fb510c9" share the words DONE, CLOSED and FIXED and one question each; the phrase itself
    /// stays on the item as written.
    /// </summary>
    public static string WordOf(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var words = new List<string>();
        foreach (var raw in phrase.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('*', '`', '_', '"', '\'');
            var letters = new string(token.TakeWhile(char.IsLetter).ToArray());
            if (letters.Length == 0 || (words.Count > 0 && (letters.Length != token.Length || Connectors.Contains(letters))))
            {
                break;
            }

            words.Add(letters);
            if (letters.Length != token.Length)
            {
                break;
            }
        }

        return words.Count == 0 ? phrase.Trim().Trim('*', '`', '_') : string.Join(' ', words);
    }

    /// <summary>True when the phrase says more than its word plus a date or a hash ("CLOSED as declined" is not plain CLOSED), so the row's own words decide its state through the judge.</summary>
    public static bool IsQualified(string phrase, string word)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(word);
        var text = phrase.Trim().Trim('*', '`', '_', '"', '\'');
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        var rest = index < 0 ? text : text[(index + word.Length)..];
        return DatesAndHashes.Replace(rest, " ").Any(char.IsLetter);
    }

    public static StatusVocabulary Build(RepositoryMap map, AnswerStore answers, Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(playbook);
        var states = MappedStates(playbook);
        var mappings = new Dictionary<string, StatusMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in map.StatusVocabulary)
        {
            if (string.IsNullOrWhiteSpace(entry.Word))
            {
                continue;
            }

            var mapped = entry.Mapped is not null && states.Contains(entry.Mapped, StringComparer.OrdinalIgnoreCase) ? entry.Mapped.ToLowerInvariant() : null;
            if (mapped is not null)
            {
                mappings[entry.Word.Trim()] = new StatusMapping(entry.Word.Trim(), mapped, DecidedBy.Reader, null, entry.Meaning);
            }
        }

        // A person's answer wins and is the only confirmed mapping (ADR 0007, ADR 0020 rule 12).
        foreach (var word in mappings.Keys.ToList())
        {
            Apply(answers.Get("status:" + word), word, states, mappings);
        }

        // The engine's own word table (data/statuses.json): a word the legend and the answers leave unmapped is mapped by
        // rule, so DEFERRED is deferred and CLOSED is done without a judge; the text the reader sees keeps to the
        // repository's own words, so the units' hashes do not move.
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (playbook.Data("statuses")["words"] is JsonObject table)
        {
            foreach (var (word, node) in table)
            {
                if (word.StartsWith('_') || node is not JsonValue value || !value.TryGetValue<string>(out var mapped))
                {
                    continue;
                }

                if (!states.Contains(mapped, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A word that is a state's own name (DEFERRED, CANCELLED, DONE) means that state whatever the reader made of the
                // legend (it read Stomme's DEFERRED as backlog); only a person's answer beats it. Any other word fills a gap.
                var isStateName = string.Equals(word, mapped, StringComparison.OrdinalIgnoreCase);
                if (!mappings.TryGetValue(word, out var existing) || (isStateName && existing.By != DecidedBy.Person))
                {
                    mappings[word] = new StatusMapping(word, mapped.ToLowerInvariant(), DecidedBy.Rule, null, existing?.Meaning);
                    builtIn.Add(word);
                }
            }
        }

        var text = new StringBuilder();
        foreach (var mapping in mappings.Values.Where(m => !builtIn.Contains(m.Word)).OrderBy(m => m.Word, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {mapping.Word}: {mapping.Mapped}{(mapping.Condition is null ? "" : " (" + mapping.Condition + ")")}{(mapping.By == DecidedBy.Person ? ", confirmed by a person" : "")}{(mapping.Meaning is null ? "" : "; the guides say: " + mapping.Meaning)}");
        }

        var block = text.Length == 0 ? "(none known yet; map each row's status word yourself)" : text.ToString().TrimEnd();
        return new StatusVocabulary(mappings, states, block, RunContext.HashOf(block));
    }

    /// <summary>Applies a person's answer for a word not in the map's vocabulary (seen in a row); true when there was one.</summary>
    public bool ApplyAnswer(string word, string? answer)
    {
        if (string.IsNullOrWhiteSpace(word) || answer is null)
        {
            return false;
        }

        return Apply(answer, word.Trim(), States, _mappings);
    }

    private static bool Apply(string? answer, string word, IReadOnlyList<string> states, Dictionary<string, StatusMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        // "deferred" or "deferred, only with the owner" or "deferred: after 12.0.0".
        var trimmed = answer.Trim();
        var state = states.FirstOrDefault(s => trimmed.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            return false;
        }

        var rest = trimmed[state.Length..].TrimStart(' ', ',', ':', ';', '-');
        mappings[word] = new StatusMapping(word, state, DecidedBy.Person, rest.Length == 0 ? null : rest, mappings.TryGetValue(word, out var old) ? old.Meaning : null);
        return true;
    }

    public static IReadOnlyList<string> MappedStates(Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        return (playbook.Data("statuses")["mapped"] as JsonObject ?? []).Select(s => s.Key).ToList();
    }

    /// <summary>The mapped states with their meanings, for the prompt.</summary>
    public static string MappedStatesText(Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        var text = new StringBuilder();
        foreach (var (state, meaning) in playbook.Data("statuses")["mapped"] as JsonObject ?? [])
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {state}: {meaning?.GetValue<string>()}");
        }

        return text.ToString().TrimEnd();
    }

    public static List<QuestionOption> Options(Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        return (playbook.Data("statuses")["mapped"] as JsonObject ?? [])
            .Select(s => new QuestionOption(s.Key, s.Value?.GetValue<string>() ?? "")).ToList();
    }
}
