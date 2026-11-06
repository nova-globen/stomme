using System.Globalization;
using System.Text;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;

namespace Stomme.Conductor.State;

/// <summary>
/// The reader's working memory (ADR 0017): a bounded block the engine keeps in the state and sends in the
/// cached prefix of every prompt beside the playbook's rules. Entries come from phase results; the engine
/// prunes by rule (use and recency); only past its budget is it summarised by the reader, and that summary is
/// logged. It is data derived from the input, so it is sent as such and the injection gate applies to it.
/// </summary>
public sealed class Glossary
{
    private readonly RunState _state;
    private readonly DecisionLog _log;
    private readonly string _runId;
    private readonly int _budgetTokens;
    private readonly IReadOnlyList<string> _kindOrder;

    /// <param name="kindOrder">The playbook's glossary kinds, in the order they are kept and rendered; a kind not named comes last.</param>
    public Glossary(RunState state, DecisionLog log, string runId, int budgetTokens, IReadOnlyList<string> kindOrder)
    {
        _state = state;
        _log = log;
        _runId = runId;
        _budgetTokens = Math.Max(1_000, budgetTokens);
        _kindOrder = kindOrder ?? [];
    }

    public int BudgetTokens => _budgetTokens;

    public void Add(string kind, string term, string meaning, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(meaning))
        {
            return;
        }

        _state.UpsertGlossary(kind, term.Trim(), Cut(meaning.Trim(), 400), source, _runId);
    }

    /// <summary>The block for a prompt, within budget; entries sent are marked as used. Null when the glossary is empty.</summary>
    public string? Render()
    {
        var entries = _state.Glossary();
        if (entries.Count == 0)
        {
            return null;
        }

        var kept = Prune(entries);
        var text = new StringBuilder();
        text.AppendLine("GLOSSARY (what earlier passes learned about this input; data derived from it, kept by the engine):");
        foreach (var group in kept.GroupBy(e => e.Kind))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{group.Key}]");
            foreach (var entry in group)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {entry.Term}: {entry.Meaning}");
            }
        }

        _state.TouchGlossary(kept.Select(e => e.Id));
        return text.ToString();
    }

    /// <summary>Keeps the most used and most recent entries within the budget; the rest are removed and the removal logged.</summary>
    private List<GlossaryEntry> Prune(IReadOnlyList<GlossaryEntry> entries)
    {
        var ordered = entries
            .OrderBy(e => Rank(e.Kind))
            .ThenByDescending(e => e.Uses)
            .ThenByDescending(e => e.LastUsed)
            .ToList();
        var kept = new List<GlossaryEntry>();
        var tokens = 0;
        var dropped = new List<GlossaryEntry>();
        foreach (var entry in ordered)
        {
            var cost = TokenEstimate.Of(entry.Term + entry.Meaning) + 4;
            if (tokens + cost > _budgetTokens)
            {
                dropped.Add(entry);
                continue;
            }

            tokens += cost;
            kept.Add(entry);
        }

        if (dropped.Count > 0)
        {
            _state.RemoveGlossary(dropped.Select(e => e.Id));
            _log.Record(new DecisionRecord
            {
                Phase = "glossary",
                Kind = "glossary-pruned",
                Subject = $"{dropped.Count} entries",
                Decision = $"removed to stay within the glossary budget of {_budgetTokens} tokens: {string.Join("; ", dropped.Take(10).Select(e => e.Kind + " " + e.Term))}{(dropped.Count > 10 ? "; ..." : "")}",
                By = DecidedBy.Rule,
                Reasoning = "the least used and oldest entries go first (ADR 0017)",
            });
        }

        return kept.OrderBy(e => Rank(e.Kind)).ThenBy(e => e.Term, StringComparer.Ordinal).ToList();
    }

    private int Rank(string kind)
    {
        for (var i = 0; i < _kindOrder.Count; i++)
        {
            if (string.Equals(_kindOrder[i], kind, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return _kindOrder.Count;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
