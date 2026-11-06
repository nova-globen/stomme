using System.Globalization;
using System.Text;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Atlas;

/// <summary>
/// Writes items/NNNN-&lt;slug&gt;.md, one work item each (ADR 0005): front matter with the number, the source ids,
/// the status as written and as mapped, the tier, the size, the dates, the references and the provenance; the
/// detail as the body, with [[wiki links]] to the items a reference resolves to. The folder is a rendering of
/// the state: it is cleared and written whole.
/// </summary>
public static class ItemWriter
{
    public static int WriteAll(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = ItemCatalogue.Load(context.State);
        var folder = context.Paths.Items;
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.GetFiles(folder, "*.md"))
        {
            File.Delete(stale);
        }

        var byKey = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            byKey[item.Key] = item;
        }

        var graph = GraphCatalogue.Load(context.State);
        var decisionsByKey = DecisionCatalogue.Load(context.State).ToDictionary(d => d.Key, StringComparer.Ordinal);
        foreach (var item in items)
        {
            File.WriteAllText(Path.Combine(folder, FileName(item)), Render(item, byKey, graph, decisionsByKey), new UTF8Encoding(false));
        }

        return items.Count;
    }

    public static string FileName(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Number.ToString("0000", CultureInfo.InvariantCulture)}-{ItemCatalogue.Slug(item.Title)}.md";
    }

    /// <summary>The wiki link to an item, by its file name without the extension (Obsidian resolves it, ADR 0005).</summary>
    public static string WikiLink(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return "[[" + FileName(item)[..^3] + "]]";
    }

    private static string Render(WorkItem item, IReadOnlyDictionary<string, WorkItem> byKey, Graph? graph, IReadOnlyDictionary<string, DecisionCard> decisionsByKey)
    {
        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"number: {item.Number}");
        text.AppendLine(CultureInfo.InvariantCulture, $"key: {Yaml(item.Key)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"aliases: [{string.Join(", ", item.Aliases.Select(Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"title: {Yaml(item.Title)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"status: {item.Status}");
        text.AppendLine(CultureInfo.InvariantCulture, $"status_as_written: {(item.StatusAsWritten is null ? "null" : Yaml(item.StatusAsWritten))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"status_by: {item.StatusBy}");
        text.AppendLine(CultureInfo.InvariantCulture, $"condition: {(item.Condition is null ? "null" : Yaml(item.Condition))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"tier: {(item.Tier is null ? "null" : Yaml(item.Tier))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"size: {(item.Size is null ? "null" : Yaml(item.Size))}");
        if (item.Dates.Count == 0)
        {
            text.AppendLine("dates: []");
        }
        else
        {
            text.AppendLine("dates:");
            foreach (var date in item.Dates)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - what: {Yaml(date.What)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    date: {Yaml(date.Date)}");
            }
        }

        static string Figure(double? value) => value is null ? "null" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);
        text.AppendLine(CultureInfo.InvariantCulture, $"status_confidence: {Figure(item.StatusConfidence)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"type: {(item.Type is null ? "null" : Yaml(item.Type))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"type_as_written: {(item.TypeAsWritten is null ? "null" : Yaml(item.TypeAsWritten))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"type_by: {(item.TypeBy is null ? "null" : item.TypeBy)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"type_confidence: {Figure(item.TypeConfidence)}");
        var parent = item.Parent is { } parentKey && byKey.TryGetValue(parentKey, out var parentItem) ? parentItem : null;
        text.AppendLine(CultureInfo.InvariantCulture, $"parent: {(parent is null ? "null" : "{ item: " + parent.Number.ToString(CultureInfo.InvariantCulture) + ", key: " + Yaml(parent.Key) + " }")}");
        var blockers = item.Blockers.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"blockers: [{string.Join(", ", blockers.Select(b => "{ item: " + b.Number.ToString(CultureInfo.InvariantCulture) + ", key: " + Yaml(b.Key) + " }"))}]");
        if (item.Relations.Count == 0)
        {
            text.AppendLine("relations: []");
        }
        else
        {
            text.AppendLine("relations:");
            foreach (var relation in item.Relations)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - kind: {relation.Kind}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    text: {Yaml(relation.Text)}");
                if (relation.ResolvedKey is not null && byKey.TryGetValue(relation.ResolvedKey, out var target))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    item: {target.Number}");
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"    as_written: {(relation.AsWritten is null ? "null" : Yaml(relation.AsWritten))}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    by: {relation.By}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    confidence: {Figure(relation.Confidence)}");
                if (relation.Flags.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    flags: [{string.Join(", ", relation.Flags.Select(Yaml))}]");
                }
            }
        }

        text.AppendLine("tags: []");
        if (item.References.Count == 0)
        {
            text.AppendLine("references: []");
        }
        else
        {
            text.AppendLine("references:");
            foreach (var reference in item.References)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - kind: {reference.Kind}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    text: {Yaml(reference.Text)}");
                if (reference.ResolvedKey is not null && byKey.TryGetValue(reference.ResolvedKey, out var target))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    item: {target.Number}");
                }
            }
        }

        text.AppendLine("sources:");
        foreach (var source in item.Sources)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - path: {Yaml(source.Path)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"    line: {(source.Line is null ? "null" : source.Line.Value.ToString(CultureInfo.InvariantCulture))}");
            text.AppendLine(CultureInfo.InvariantCulture, $"    sha256: {source.Sha256}");
            text.AppendLine(CultureInfo.InvariantCulture, $"    ledger: {source.LedgerKind}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"placeholder: {(item.Placeholder ? "true" : "false")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"source_gone: {(item.SourceGone ? "true" : "false")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"flags: [{string.Join(", ", item.Flags.Select(Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"confidence: {(item.Confidence is null ? "null" : item.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"by: {item.By}");
        text.AppendLine("drafted_by: survey");
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {(item.Key.Contains('#', StringComparison.Ordinal) ? "" : item.Key + ": ")}{string.Join(' ', item.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");
        text.AppendLine();
        var rowText = ItemCatalogue.RowText(item);
        if (rowText.Length > 0)
        {
            text.AppendLine(rowText);
            text.AppendLine();
        }

        // Where the row's own text ends: everything after this line the survey wrote (docs/bundle-format.md).
        text.AppendLine(SurveyMarkers.Generated);
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(item.Summary) && !string.Equals(item.Summary.Trim(), item.Detail.Trim(), StringComparison.Ordinal))
        {
            text.AppendLine("## Summary (reader)");
            text.AppendLine();
            text.AppendLine(item.Summary.Trim());
            text.AppendLine();
        }

        if (item.References.Count > 0)
        {
            text.AppendLine("## References");
            text.AppendLine();
            foreach (var reference in item.References)
            {
                if (reference.ResolvedKey is not null && byKey.TryGetValue(reference.ResolvedKey, out var target))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"- {reference.Text}: {WikiLink(target)}");
                }
                else
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"- {reference.Kind}: {reference.Text}");
                }
            }

            text.AppendLine();
        }

        if (item.EvidenceQuote is not null || item.Reasoning is not null)
        {
            text.AppendLine("## Evidence");
            text.AppendLine();
            if (item.EvidenceQuote is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {item.Sources[0].Path}{(item.EvidenceLine is null ? "" : ":" + item.EvidenceLine)}: \"{item.EvidenceQuote.Replace('\n', ' ').Trim()}\"");
            }

            if (item.Reasoning is not null && !item.Placeholder)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- Reasoning: {item.Reasoning.Replace('\n', ' ').Trim()}");
            }

            text.AppendLine();
        }

        if (graph is not null)
        {
            var id = GraphCatalogue.ItemId(item.Key);
            var links = graph.Edges.Where(e => e.To == id && e.From != id && !e.From.StartsWith("item:", StringComparison.Ordinal)).ToList();
            if (links.Count > 0)
            {
                text.AppendLine(SurveyMarkers.LinksStart);
                text.AppendLine("## Links (survey)");
                text.AppendLine();
                foreach (var edge in links.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.From, StringComparer.Ordinal))
                {
                    var target = edge.From.StartsWith("document:", StringComparison.Ordinal) ? DocumentCatalogue.WikiLink(edge.From["document:".Length..])
                        : edge.From.StartsWith("decision:", StringComparison.Ordinal) ? (decisionsByKey.TryGetValue(edge.From["decision:".Length..], out var decision) && decision.Number is not null ? DecisionCatalogue.WikiLink(decision) : decision?.Title ?? edge.From)
                        : edge.From;
                    text.AppendLine(CultureInfo.InvariantCulture, $"- {edge.Kind}: {target} (by the {edge.By}, {edge.Confidence:0.00})");
                }

                text.AppendLine(SurveyMarkers.LinksEnd);
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>A double-quoted YAML scalar.</summary>
    internal static string Yaml(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    text.Append("\\\\");
                    break;
                case '"':
                    text.Append("\\\"");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
