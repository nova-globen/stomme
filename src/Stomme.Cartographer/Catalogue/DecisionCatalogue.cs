using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Catalogue;

/// <summary>The decisions in the state (ADR 0006): one record per decision by its key, the numbers of the drafts (ADR 0015), and what the history gave (ADR 0021).</summary>
public static partial class DecisionCatalogue
{
    public const string DecisionKind = "decision";
    public const string NumberKind = "decision-number";
    public const string HistoryKind = "decision-history";

    public static List<DecisionCard> Load(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var cards = new List<DecisionCard>();
        foreach (var (_, json) in state.Records(DecisionKind))
        {
            try
            {
                if (JsonSerializer.Deserialize<DecisionCard>(json, RunContext.JsonOptions) is { } card)
                {
                    cards.Add(card);
                }
            }
            catch (JsonException)
            {
                // A record of an older shape is left behind; the phase writes the current one again.
            }
        }

        return cards.OrderBy(c => c.Number ?? int.MaxValue).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
    }

    public static Dictionary<string, int> LoadNumbers(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, json) in state.Records(NumberKind))
        {
            if (int.TryParse(json, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                numbers[key] = number;
            }
        }

        return numbers;
    }

    public static void SaveOne(SurveyContext context, DecisionCard card)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(card);
        context.State.SaveRecord(DecisionKind, card.Key, JsonSerializer.Serialize(card, RunContext.JsonOptions), context.RunId);
    }

    public static void SaveNumber(SurveyContext context, string key, int number)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.State.SaveRecord(NumberKind, key, number.ToString(CultureInfo.InvariantCulture), context.RunId);
    }

    /// <summary>The words of a decision, lower case, letters and digits only, so that the same decision noted twice has the same key.</summary>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = NotWord().Replace(text.ToLowerInvariant(), " ");
        return Spaces().Replace(cleaned, " ").Trim();
    }

    public static string KeyOf(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(text)));
        return "d-" + Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>The words of three letters or more, as a set, for the likeness of two decisions.</summary>
    public static HashSet<string> Words(string text) =>
        new(Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3), StringComparer.Ordinal);

    /// <summary>Jaccard likeness of two word sets: 1 when they are the same, 0 when they share nothing or either is empty.</summary>
    public static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    public static string FileName(DecisionCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return $"ADR-draft-{(card.Number ?? 0).ToString("000", CultureInfo.InvariantCulture)}.md";
    }

    public static string WikiLink(DecisionCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return $"[[decisions/ADR-draft-{(card.Number ?? 0).ToString("000", CultureInfo.InvariantCulture)}]]";
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
