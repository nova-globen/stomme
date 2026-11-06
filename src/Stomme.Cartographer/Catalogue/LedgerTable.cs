using System.Text.RegularExpressions;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Catalogue;

/// <summary>One cell of a ledger row: its column's header, its text as written, and its role in the row.</summary>
public sealed record LedgerCell(string Header, string Text, string Role);

/// <summary>A row of a ledger's table as code reads it: the id cell, the title cell and the status cell, the line within the text shown, every cell with its header, and the line as written.</summary>
public sealed record LedgerRow(string Id, string Title, string? Status, int Line, IReadOnlyList<LedgerCell> Cells, string Text)
{
    /// <summary>
    /// The row's own text as an item's body: the title cell first, then every other cell but the id and the status
    /// under its header. Nothing is cut, so the item carries what the row says (the verify session's check of
    /// 2026-11-04: bodies cut at 1,500 characters with no sign).
    /// </summary>
    public string Body()
    {
        var parts = new List<string>();
        var title = Cells.FirstOrDefault(c => c.Role == "title");
        if (title is not null && title.Text.Length > 0)
        {
            parts.Add(title.Text);
        }

        // The status cell too: a finished ledger closes a row with a note of hundreds of characters in it.
        foreach (var cell in Cells.Where(c => c.Role is "other" or "status" && c.Text.Length > 0).OrderBy(c => c.Role == "status" ? 1 : 0))
        {
            parts.Add(cell.Header.Length > 0 ? $"**{cell.Header}**: {cell.Text}" : cell.Text);
        }

        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// Reads the rows of a markdown table by code, so that the rows the reader returns can be reconciled against the
/// file (a reader that undercounts a chunk is caught, and the missing rows are filled by rule). The id column is
/// the one whose cells carry ids of the item scheme; the title is the longest other cell; the status is the
/// column headed status or state, else the last column.
/// </summary>
public static partial class LedgerTable
{
    public static List<LedgerRow> Parse(string text, IReadOnlyList<IdScheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schemes);
        var lines = text.Split('\n');
        var rows = new List<(int Line, string[] Cells)>();
        string[]? header = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (!line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.All(c => Separator().IsMatch(c)))
            {
                continue;
            }

            if (header is null)
            {
                // A chunk that starts inside the table has no header: a first line whose cell carries an id is a row.
                if (cells.Any(c => IdNormaliser.Normalise(Bare(c), schemes) is not null))
                {
                    header = [];
                }
                else
                {
                    header = cells;
                    continue;
                }
            }

            rows.Add((i + 1, cells));
        }

        if (header is null || rows.Count == 0)
        {
            return [];
        }

        var width = Math.Max(header.Length, rows.Max(r => r.Cells.Length));
        var idColumn = -1;
        var best = 0;
        for (var c = 0; c < width; c++)
        {
            var hits = rows.Count(r => c < r.Cells.Length && IdNormaliser.Normalise(Bare(r.Cells[c]), schemes) is not null);
            if (hits > best && hits * 2 >= rows.Count)
            {
                best = hits;
                idColumn = c;
            }
        }

        if (idColumn < 0)
        {
            return [];
        }

        var statusColumn = Array.FindIndex(header, h => StatusHead().IsMatch(h));
        if (statusColumn < 0)
        {
            statusColumn = width - 1 == idColumn ? -1 : width - 1;
        }

        var titleColumn = -1;
        double longest = 0;
        for (var c = 0; c < width; c++)
        {
            if (c == idColumn || c == statusColumn)
            {
                continue;
            }

            var average = rows.Average(r => c < r.Cells.Length ? r.Cells[c].Length : 0);
            if (average > longest)
            {
                longest = average;
                titleColumn = c;
            }
        }

        var result = new List<LedgerRow>();
        foreach (var (line, cells) in rows)
        {
            if (idColumn >= cells.Length)
            {
                continue;
            }

            var id = Bare(cells[idColumn]);
            if (IdNormaliser.Normalise(id, schemes) is null)
            {
                continue;
            }

            var title = titleColumn >= 0 && titleColumn < cells.Length ? Bare(cells[titleColumn]) : id;
            var status = statusColumn >= 0 && statusColumn < cells.Length ? Bare(cells[statusColumn]) : null;
            var all = new List<LedgerCell>();
            for (var c = 0; c < cells.Length; c++)
            {
                var role = c == idColumn ? "id" : c == titleColumn ? "title" : c == statusColumn ? "status" : "other";
                var cellText = cells[c].Trim();
                if (role == "title" && cellText.StartsWith("**", StringComparison.Ordinal) && cellText.EndsWith("**", StringComparison.Ordinal) && cellText.Length > 4)
                {
                    cellText = cellText[2..^2].Trim();
                }

                all.Add(new LedgerCell(c < header.Length ? Bare(header[c]) : "", cellText, role));
            }

            result.Add(new LedgerRow(id, title.Length == 0 ? id : title, string.IsNullOrWhiteSpace(status) ? null : status, line, all, lines[line - 1].TrimEnd('\r')));
        }

        return result;
    }

    /// <summary>A cell without its markdown: bold, code, a link's text.</summary>
    public static string Bare(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var text = Link().Replace(cell, "$1");
        return text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^\s*(status|state|standing)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StatusHead();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();
}
