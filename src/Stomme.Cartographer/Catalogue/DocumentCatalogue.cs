using System.Text.Json;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// The catalogue of documents as the state keeps it (ADR 0017): one record per document by its source path.
/// The atlas is a rendering of these records over the source text, which is read from the input when the
/// atlas is written, never copied into the state.
/// </summary>
public static class DocumentCatalogue
{
    public const string DocumentKind = "document";

    public static List<DocumentCard> Load(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Records(DocumentKind)
            .Select(r => Deserialize(r.Json))
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ToList();
    }

    public static void Save(SurveyContext context, IReadOnlyList<DocumentCard> cards)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cards);
        foreach (var card in cards)
        {
            SaveOne(context, card);
        }
    }

    public static void SaveOne(SurveyContext context, DocumentCard card)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(card);
        context.State.SaveRecord(DocumentKind, card.Path, JsonSerializer.Serialize(card, RunContext.JsonOptions), context.RunId);
    }

    /// <summary>The atlas path of a document, relative to documents/: the source path, with .md added unless it already ends so (ADR 0005).</summary>
    public static string AtlasRelativePath(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (sourcePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        // A name without an extension (LICENSE, Dockerfile) would meet its .md sibling at one file (Stomme's LICENSE and
        // LICENSE.md: 412 cards, 411 files, the verify session's fourth check), so it keeps a .txt mark of its own.
        var name = sourcePath.Contains('/', StringComparison.Ordinal) ? sourcePath[(sourcePath.LastIndexOf('/') + 1)..] : sourcePath;
        return name.Contains('.', StringComparison.Ordinal) ? sourcePath + ".md" : sourcePath + ".txt.md";
    }

    /// <summary>The wiki link to a document from anywhere in the atlas: its path from the atlas root without the extension (Obsidian resolves a path, ADR 0005).</summary>
    public static string WikiLink(string sourcePath)
    {
        var relative = AtlasRelativePath(sourcePath);
        return "[[documents/" + relative[..^3] + "]]";
    }

    private static DocumentCard? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DocumentCard>(json, RunContext.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
