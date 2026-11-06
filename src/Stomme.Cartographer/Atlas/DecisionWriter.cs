using System.Globalization;
using System.Text;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Atlas;

/// <summary>
/// Writes decisions/: one ADR-draft-NNN.md per decision that gets a draft (ADR 0006: status proposed, drafted by the
/// survey, with its evidence and its links), and index.md listing every decision found with its standing.
/// </summary>
public static class DecisionWriter
{
    public static void WriteAll(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cards = DecisionCatalogue.Load(context.State);
        var items = ItemCatalogue.Load(context.State).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var folder = context.Paths.Decisions;
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "ADR-draft-*.md"))
        {
            File.Delete(old);
        }

        foreach (var card in cards.Where(c => c.Number is not null))
        {
            File.WriteAllText(Path.Combine(folder, DecisionCatalogue.FileName(card)), Render(card, items), new UTF8Encoding(false));
        }

        File.WriteAllText(Path.Combine(folder, "index.md"), Index(cards), new UTF8Encoding(false));
    }

    public static string Render(DecisionCard card, IReadOnlyDictionary<string, WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(items);
        var text = new StringBuilder();
        var title = card.Draft?.Title is { Length: > 0 } drafted ? drafted : Cut(card.Text, 80);
        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"title: {ItemWriter.Yaml(title)}");
        text.AppendLine("status: proposed");
        text.AppendLine("drafted_by: survey");
        text.AppendLine(CultureInfo.InvariantCulture, $"number: {card.Number ?? 0}");
        text.AppendLine(CultureInfo.InvariantCulture, $"key: {card.Key}");
        text.AppendLine(CultureInfo.InvariantCulture, $"date: {(card.Draft?.Date ?? card.Date) ?? "null"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"standing: {card.Standing}");
        text.AppendLine(CultureInfo.InvariantCulture, $"made: {Figure(card.Made)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"significant: {Figure(card.Significant)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"recorded_in: {(card.RecordedIn is null ? "null" : ItemWriter.Yaml(card.RecordedIn))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"confidence: {Figure(card.Draft?.Confidence)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"by: {card.By}");
        text.AppendLine("sources:");
        foreach (var source in card.Sources)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - kind: {source.Kind}");
            text.AppendLine(CultureInfo.InvariantCulture, $"    path: {ItemWriter.Yaml(source.Path)}");
            if (source.Line is { } line)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    line: {line}");
            }

            if (source.Date is { } date)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    date: {date}");
            }

            if (source.Title is { Length: > 0 } sourceTitle)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    title: {ItemWriter.Yaml(sourceTitle)}");
            }

            if (source.Quote is { Length: > 0 } quote)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    quote: {ItemWriter.Yaml(Cut(quote, 300))}");
            }
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"items: [{string.Join(", ", card.Items.Select(ItemWriter.Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"flags: [{string.Join(", ", card.Flags.Select(ItemWriter.Yaml))}]");
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"# ADR-draft-{(card.Number ?? 0).ToString("000", CultureInfo.InvariantCulture)}: {title}");
        text.AppendLine();
        text.AppendLine("A draft proposed by the survey from the evidence below (ADR 0006); nothing is recorded until a person accepts it.");
        text.AppendLine();
        text.AppendLine("## Context");
        text.AppendLine();
        text.AppendLine(card.Draft?.Context is { Length: > 0 } context ? context : "(the evidence gives no context beyond the decision itself)");
        text.AppendLine();
        text.AppendLine("## Decision");
        text.AppendLine();
        text.AppendLine(card.Draft?.Decision is { Length: > 0 } decision ? decision : card.Text);
        text.AppendLine();
        text.AppendLine("## Consequences");
        text.AppendLine();
        text.AppendLine(card.Draft?.Consequences is { Length: > 0 } consequences ? consequences : "(none stated in the evidence)");
        text.AppendLine();
        text.AppendLine("## Evidence");
        text.AppendLine();
        foreach (var source in card.Sources)
        {
            text.AppendLine(Evidence(source));
        }

        text.AppendLine();
        text.AppendLine("## Links (survey)");
        text.AppendLine();
        var any = false;
        foreach (var key in card.Items)
        {
            if (items.TryGetValue(key, out var item))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {ItemWriter.WikiLink(item)} ({key})");
                any = true;
            }
        }

        foreach (var source in card.Sources.Where(s => s.Kind == "document").Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {DocumentCatalogue.WikiLink(source)}");
            any = true;
        }

        if (!any)
        {
            text.AppendLine("- (none)");
        }

        if (card.Draft?.Reasoning is { Length: > 0 } reasoning)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Reader's reasoning: {reasoning}");
        }

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Index(IReadOnlyList<DecisionCard> cards)
    {
        var text = new StringBuilder();
        text.AppendLine("# Decisions found by the survey");
        text.AppendLine();
        text.AppendLine("Every decision the survey found in the documents and the commit messages (ADR 0006), merged where the same one was noted in several places. A draft is a file beside this index; a record is kept for the graph only; recorded means an ADR of the repository already holds it.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- {cards.Count} decisions: {string.Join(", ", cards.GroupBy(c => c.Standing).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}.");
        text.AppendLine();
        text.AppendLine("| Draft | Standing | Decision | Sources | Made | Significant | Recorded in |");
        text.AppendLine("|---|---|---|---|---:|---:|---|");
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["draft"] = 0, ["recorded"] = 1, ["record"] = 2, ["unsure"] = 3, ["not-a-decision"] = 4 };
        foreach (var card in cards.OrderBy(c => order.GetValueOrDefault(c.Standing, 5)).ThenBy(c => c.Number ?? int.MaxValue).ThenBy(c => c.Date ?? "9999", StringComparer.Ordinal))
        {
            var draft = card.Number is { } n ? $"[[decisions/ADR-draft-{n.ToString("000", CultureInfo.InvariantCulture)}]]" : "";
            var recorded = card.RecordedIn is { } path ? DocumentCatalogue.WikiLink(path) : "";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {draft} | {card.Standing} | {Cell(Cut(card.Draft?.Title ?? card.Text, 120))} | {card.Sources.Count} | {Figure(card.Made)} | {Figure(card.Significant)} | {recorded} |");
        }

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Evidence(DecisionSource source)
    {
        if (source.Kind == "document")
        {
            var where = source.Line is { } line ? $" line {line}" : "";
            var quote = source.Quote is { Length: > 0 } q ? $": \"{Cut(q, 300)}\"" : "";
            return $"- {DocumentCatalogue.WikiLink(source.Path)}{where}{quote}";
        }

        var hash = source.Path.Length > 12 ? source.Path[..12] : source.Path;
        var date = source.Date is { } d ? $" ({d})" : "";
        var older = source.Kind == "history-commit" ? " of the older history" : "";
        return $"- commit {hash}{older}{date}: {Cut(source.Title ?? source.Quote ?? "", 200)}";
    }

    private static string Figure(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "null";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
