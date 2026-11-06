using System.Globalization;
using System.Text;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Sources.Decoders;

namespace Stomme.Cartographer.Catalogue;

/// <summary>One row as the running summary carries it: enough to say what came before, nothing more.</summary>
public sealed record RowSummary(string? Key, string Title, string? Status);

/// <summary>
/// The fixed-shape summary carried from chunk to chunk of a ledger (ADR 0019): what the ledger is, the headings
/// before this chunk, how many rows came before and which was the last, the ids seen so far. Rebuilt by code
/// from the state at each chunk, never accumulated by a model, and cut to a size cap, so the last chunk of a
/// hundred sees a summary the size of the second's.
/// </summary>
public static class ChunkSummary
{
    private const int HeadingsKept = 12;
    private const int IdsKept = 40;

    public static string Build(string path, string ledgerKind, DecodedDocument document, TextChunk chunk, IReadOnlyList<RowSummary> rowsBefore, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(rowsBefore);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Ledger: {path} ({ledgerKind}){(document.Title is null ? "" : ", titled \"" + Cut(document.Title, 120) + "\"")}; this is chunk {chunk.Index + 1} of {chunk.Count}, lines {chunk.StartLine - chunk.OverlapLines} to {chunk.EndLine}{(chunk.OverlapLines > 0 ? $", the first {chunk.OverlapLines} of them (lines {chunk.StartLine - chunk.OverlapLines} to {chunk.StartLine - 1}) repeated from the chunk before" : "")}.");
        if (chunk.Index == 0)
        {
            text.AppendLine("Nothing came before this chunk.");
            return Cap(text.ToString(), maxChars);
        }

        var before = document.Sections.Where(s => s.StartLine < chunk.StartLine).ToList();
        var current = before.LastOrDefault();
        if (current is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"This chunk starts under the heading \"{Cut(current.Heading, 120)}\" (line {current.StartLine}), whose text continues from the chunk before.");
        }

        if (before.Count > 1)
        {
            var headings = before.Take(before.Count - 1).Select(s => s.Heading).ToList();
            var shown = headings.Count <= HeadingsKept ? headings : headings.Skip(headings.Count - HeadingsKept).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"Headings before it{(headings.Count > shown.Count ? $" (the last {shown.Count} of {headings.Count})" : "")}: {string.Join(" | ", shown.Select(h => Cut(h, 80)))}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Rows read in the chunks before: {rowsBefore.Count}.");
        if (rowsBefore.Count > 0)
        {
            var last = rowsBefore[^1];
            text.AppendLine(CultureInfo.InvariantCulture, $"The last row before this chunk: {last.Key ?? "(no id)"} \"{Cut(last.Title, 100)}\"{(last.Status is null ? "" : " with status " + last.Status)}; a row cut by the chunk edge continues from it.");
            var ids = rowsBefore.Where(r => r.Key is not null).Select(r => r.Key!).ToList();
            if (ids.Count > 0)
            {
                var shown = ids.Count <= IdsKept ? ids : ids.Skip(ids.Count - IdsKept).ToList();
                text.AppendLine(CultureInfo.InvariantCulture, $"Ids of the rows so far{(ids.Count > shown.Count ? $" (the last {shown.Count} of {ids.Count})" : "")}: {string.Join(", ", shown)}");
            }
        }

        text.AppendLine("Do not repeat a row that came before unless this chunk holds it again in full.");
        return Cap(text.ToString(), maxChars);
    }

    private static string Cut(string value, int max)
    {
        var line = value.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return line.Length <= max ? line : line[..max] + "...";
    }

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 22)] + "\n[cut by the engine]\n";
}
