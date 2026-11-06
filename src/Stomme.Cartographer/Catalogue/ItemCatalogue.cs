using System.Globalization;
using System.Text.Json;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// The catalogue of work items as the state keeps it (ADR 0017): one record per item by number, the assigned
/// numbers by key (ADR 0015: an assignment is kept so a re-run reuses it), and the prose lists of work with
/// their answers. The atlas is a rendering of these records.
/// </summary>
public static class ItemCatalogue
{
    public const string ItemKind = "item";
    public const string NumberKind = "item-number";
    public const string ProseKind = "prose-work";
    public const string WordsKind = "status-words";

    public static List<WorkItem> Load(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Records(ItemKind)
            .Select(r => Deserialize<WorkItem>(r.Json))
            .Where(i => i is not null)
            .Select(i => i!)
            .OrderBy(i => i.Number)
            .ToList();
    }

    public static List<ProseWork> LoadProse(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Records(ProseKind).Select(r => Deserialize<ProseWork>(r.Json)).Where(p => p is not null).Select(p => p!).ToList();
    }

    public static Dictionary<string, StatusMapping> LoadWords(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var words = new Dictionary<string, StatusMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in state.Records(WordsKind))
        {
            if (Deserialize<StatusMapping>(json) is { } mapping)
            {
                words[key] = mapping;
            }
        }

        return words;
    }

    public static Dictionary<string, int> LoadNumbers(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in state.Records(NumberKind))
        {
            if (int.TryParse(json, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                numbers[key] = number;
            }
        }

        return numbers;
    }

    public static void Save(SurveyContext context, IReadOnlyList<WorkItem> items, IReadOnlyDictionary<string, int> assigned, IReadOnlyList<ProseWork> prose, IReadOnlyDictionary<string, StatusMapping> words)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(prose);
        ArgumentNullException.ThrowIfNull(words);
        foreach (var item in items)
        {
            context.State.SaveRecord(ItemKind, RecordKey(item.Number), JsonSerializer.Serialize(item, RunContext.JsonOptions), context.RunId);
        }

        foreach (var (key, number) in assigned)
        {
            context.State.SaveRecord(NumberKind, key, number.ToString(CultureInfo.InvariantCulture), context.RunId);
        }

        foreach (var work in prose)
        {
            context.State.SaveRecord(ProseKind, work.QuestionKey, JsonSerializer.Serialize(work, RunContext.JsonOptions), context.RunId);
        }

        // The words are the set in use now: one an earlier run recorded that no row uses any more is dropped, so
        // the summary does not list it with no rows.
        foreach (var (stale, _) in context.State.Records(WordsKind).Where(r => !words.ContainsKey(r.Key)))
        {
            context.State.DeleteRecord(WordsKind, stale);
        }

        foreach (var (word, mapping) in words)
        {
            context.State.SaveRecord(WordsKind, word, JsonSerializer.Serialize(mapping, RunContext.JsonOptions), context.RunId);
        }
    }

    public static string RecordKey(int number) => number.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The item's row text: what its file holds between the generated heading and the generated marker, with line
    /// endings as LF and the ends trimmed. The row's own text for an item of a row; one sentence for a placeholder,
    /// which stands for no row. The file and the hash both come from here, so they cannot disagree.
    /// </summary>
    public static string RowText(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = item.Placeholder
            ? "A placeholder (ADR 0015): rows refer to this id, but no row defines it. " + (item.Reasoning ?? "")
            : item.Detail;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    /// <summary>SHA-256, lower-case hex, of <see cref="RowText"/>: a consumer's row hash (docs/bundle-format.md).</summary>
    public static string BodySha256(WorkItem item) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RowText(item))));

    /// <summary>The file name's slug: the title in lower-case ASCII letters, digits and hyphens, at most 60 characters.</summary>
    public static string Slug(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var folded = title.Normalize(System.Text.NormalizationForm.FormD);
        var slug = new System.Text.StringBuilder();
        var lastHyphen = true;
        foreach (var c in folded)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                // Every script, not ASCII alone: rows titled in Persian or Chinese get slugs of their own (change 3 of the verdict).
                slug.Append(char.ToLowerInvariant(c));
                lastHyphen = false;
            }
            else if (!lastHyphen)
            {
                slug.Append('-');
                lastHyphen = true;
            }

            if (slug.Length >= 60)
            {
                break;
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? "item" : result;
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
