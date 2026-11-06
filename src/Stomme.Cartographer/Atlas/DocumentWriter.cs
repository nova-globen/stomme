using System.Globalization;
using System.Text;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Atlas;

/// <summary>
/// Writes documents/&lt;source path&gt;.md, one document each (ADR 0005): front matter with the title, the kind, the
/// language, what the document says about itself (ADR 0020 rule 9), the shelf and the reference-only mark for a
/// file written for agents (rule 8), the tags, the references with what code resolved them to, the decisions
/// noted, and the provenance; the source text as the body, copied from the input when the atlas is written.
/// The folder is a rendering of the state: it is cleared and written whole.
/// </summary>
public static class DocumentWriter
{
    public static int WriteAll(SurveyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cards = DocumentCatalogue.Load(context.State);
        var folder = Path.GetFullPath(context.Paths.Documents);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        Directory.CreateDirectory(folder);
        var items = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ItemCatalogue.Load(context.State))
        {
            items[item.Key] = item;
        }

        var written = 0;
        foreach (var card in cards)
        {
            var target = Path.GetFullPath(Path.Combine(folder, DocumentCatalogue.AtlasRelativePath(card.Path).Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                // A source path that would leave the documents folder is not written; the card stays in the state.
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Render(context, card, items), new UTF8Encoding(false));
            written++;
        }

        return written;
    }

    private static string Render(SurveyContext context, DocumentCard card, IReadOnlyDictionary<string, WorkItem> items)
    {
        var (body, sourceFrontMatter, bodyNote) = Body(context, card);
        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"title: {Scalar(card.Title)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"kind: {card.Kind}");
        text.AppendLine(CultureInfo.InvariantCulture, $"kind_by_orientation: {Scalar(card.KindByOrientation)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"language: {Scalar(card.Language)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"status: {card.Status}");
        text.AppendLine(CultureInfo.InvariantCulture, $"status_as_written: {Scalar(card.StatusAsWritten)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"superseded_by: {Scalar(card.SupersededBy)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"self_rating: {Scalar(card.SelfRating)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"audience: {card.Audience}");
        text.AppendLine(CultureInfo.InvariantCulture, $"for_agents: {(card.ForAgents ? "true" : "false")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"shelf: {Scalar(card.Shelf)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"reference_only: {(card.ReferenceOnly ? "true" : "false")}");
        if (card.ReferenceOnly)
        {
            text.AppendLine("owner: git");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"summary: {Scalar(card.Summary)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"topics: [{string.Join(", ", card.Topics.Select(ItemWriter.Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"components: [{string.Join(", ", card.Components.Select(ItemWriter.Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"people: [{string.Join(", ", card.People.Select(ItemWriter.Yaml))}]");
        if (card.Dates.Count == 0)
        {
            text.AppendLine("dates: []");
        }
        else
        {
            text.AppendLine("dates:");
            foreach (var date in card.Dates)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - what: {ItemWriter.Yaml(date.What)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    date: {ItemWriter.Yaml(date.Date)}");
            }
        }

        if (card.References.Count == 0)
        {
            text.AppendLine("references: []");
        }
        else
        {
            text.AppendLine("references:");
            foreach (var reference in card.References)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - kind: {reference.Kind}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    text: {ItemWriter.Yaml(reference.Text)}");
                if (reference.Line is { } line)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    line: {line}");
                }

                if (reference.Item is not null && items.TryGetValue(reference.Item, out var item))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    item: {item.Number}");
                }

                if (reference.Document is not null)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"    document: {ItemWriter.Yaml("documents/" + DocumentCatalogue.AtlasRelativePath(reference.Document))}");
                }
            }
        }

        if (card.Decisions.Count == 0)
        {
            text.AppendLine("decisions: []");
        }
        else
        {
            text.AppendLine("decisions:");
            foreach (var decision in card.Decisions)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - text: {ItemWriter.Yaml(decision.Text)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    line: {(decision.Line is null ? "null" : decision.Line.Value.ToString(CultureInfo.InvariantCulture))}");
                text.AppendLine(CultureInfo.InvariantCulture, $"    quote: {Scalar(decision.Quote)}");
            }
        }

        text.AppendLine("source:");
        text.AppendLine(CultureInfo.InvariantCulture, $"  path: {ItemWriter.Yaml(card.Source.Path)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  sha256: {card.Source.Sha256}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  size: {card.Source.Size}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  format: {card.Source.Format}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  chunks: {card.Source.Chunks}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  gone: {(card.SourceGone ? "true" : "false")}");
        if (bodyNote is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  body_note: {ItemWriter.Yaml(bodyNote)}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"flags: [{string.Join(", ", card.Flags.Select(ItemWriter.Yaml))}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"confidence: {(card.Confidence is null ? "null" : card.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"by: {card.By}");
        text.AppendLine("drafted_by: survey");
        if (sourceFrontMatter is not null)
        {
            text.AppendLine("source_front_matter: |");
            foreach (var line in sourceFrontMatter)
            {
                text.Append("  ").AppendLine(line);
            }
        }

        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine(body.TrimEnd());
        var links = new List<string>();
        foreach (var reference in card.References)
        {
            if (reference.Item is not null && items.TryGetValue(reference.Item, out var item))
            {
                links.Add($"- {reference.Text}: {ItemWriter.WikiLink(item)}");
            }
            else if (reference.Document is not null)
            {
                links.Add($"- {reference.Text}: {DocumentCatalogue.WikiLink(reference.Document)}");
            }
        }

        if (links.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(SurveyMarkers.LinksStart);
            text.AppendLine("## Links (survey)");
            text.AppendLine();
            foreach (var link in links.Distinct(StringComparer.Ordinal))
            {
                text.AppendLine(link);
            }

            text.AppendLine(SurveyMarkers.LinksEnd);
        }

        // The body keeps the source's line breaks as LF, so the whole file is LF whatever the host.
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The body from the input as it is now: the source text, its own front matter lifted out; a note when the file changed since the card was made, or is gone.</summary>
    private static (string Body, IReadOnlyList<string>? SourceFrontMatter, string? Note) Body(SurveyContext context, DocumentCard card)
    {
        var file = context.File(card.Path);
        var decoded = file is null ? null : context.Decode(file);
        if (file is null || decoded is null)
        {
            return ("The source is gone from the input, or no decoder reads it now; the card is kept and marked (ADR 0017). The body is not available.", null, file is null ? "the file is gone from the input" : "no decoder reads the file now");
        }

        string? note = string.Equals(file.Sha256, card.Source.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "the file changed after it was read; the body is the current text, the card describes the earlier one";
        // The source as written, its own front matter included (change 8 of the verdict): a consumer strips the card and keeps the rest.
        return (string.Join('\n', decoded.Lines), null, note);
    }

    private static string Scalar(string? value) => value is null ? "null" : ItemWriter.Yaml(value);
}
