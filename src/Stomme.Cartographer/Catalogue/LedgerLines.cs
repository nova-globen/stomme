using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stomme.Cartographer.Survey;

namespace Stomme.Cartographer.Catalogue;

/// <summary>
/// One row of a ledger as its file writes it, read by code: where it begins, where its own text ends and where the
/// lines that belong to it end (up to the next row), what kind of row it is, its raw lines, its text without the
/// list marker, its title, and its id when its lead or its last bracket is an id of a confirmed scheme.
/// </summary>
/// <param name="Line">The row's first line, 1-based.</param>
/// <param name="EndLine">The last line that belongs to the row, 1-based: the line before the next row, the next section or the table's next row.</param>
/// <param name="Kind">table, list, heading or paragraph.</param>
/// <param name="Raw">The lines from <paramref name="Line"/> to <paramref name="EndLine"/>, each line's end trimmed, LF between them.</param>
/// <param name="Text">The row's own wording: its first line without the list marker and the checkbox, and the lines that continue it.</param>
/// <param name="Title">The title by code.</param>
/// <param name="Id">The row's own id of a confirmed scheme, normalised, or null.</param>
/// <param name="IdAtEnd">True when the id stands in the bracket that ends the row's first line and not at its lead: it gives way to a row that opens with the same id.</param>
public sealed record RowBlock(int Line, int EndLine, string Kind, string Raw, string Text, string Title, string? Id, bool IdAtEnd = false)
{
    /// <summary>SHA-256, lower-case hex, over <see cref="Raw"/>: the same while the file's own lines for the row are the same, whatever a model says of them.</summary>
    public string RowSha256 => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Raw)));
}

/// <summary>A table of a ledger around one of its rows: where it begins and ends, its header line and cells (empty when it has no header row), the row's cells, and every row's cells.</summary>
public sealed record TableAround(int First, int Last, string HeaderLine, IReadOnlyList<string> Header, IReadOnlyList<string> Cells, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// A ledger's lines read by code (the re-checks of the version 2 bundles). The reader says which rows there are;
/// code says where each one is and what it is: a row is found forward from the row before it, where its title
/// leads a line before where the title is only mentioned; the lines of the file are split at every row that was
/// found, whatever marks the file uses for its entries, so no row's lines are taken into another's; and the title,
/// the id and the text come from the row's own lines. The same raw lines give the same title, key and text on
/// every run, whatever the reader replies.
/// </summary>
public sealed partial class LedgerLines
{
    private readonly IReadOnlyList<string> _lines;
    private readonly string[] _plain;

    public LedgerLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines = lines;
        _plain = lines.Select(Plain).ToArray();
    }

    public int Count => _lines.Count;

    public string Line(int index) => _lines[index];

    /// <summary>
    /// The index (0-based) of the line a row begins on, or -1. Looked for forward from the row before it
    /// (<paramref name="after"/>), then anywhere between <paramref name="from"/> and <paramref name="until"/>; a line
    /// where the title leads (nothing before it but marks, a number, a date or an id) before a line that only
    /// mentions it; never a line another row was found on.
    /// </summary>
    public int FindRow(string title, int after, int from, int until, IReadOnlySet<int> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        var text = Plain(TrimEllipsis(title ?? ""));
        if (text.Length == 0)
        {
            return -1;
        }

        from = Math.Max(0, from);
        until = Math.Min(_plain.Length, until);
        var forward = Math.Max(from, after + 1);
        var tests = new Func<int, bool>[]
        {
            i => Leads(_plain[i], text, whole: true),
            i => Leads(_plain[i], text, whole: false),
            i => HoldsWhole(_plain[i], text, StringComparison.Ordinal),
            i => HoldsWhole(_plain[i], text, StringComparison.OrdinalIgnoreCase),
            i => _plain[i].Contains(text, StringComparison.OrdinalIgnoreCase),
            i => Wrapped(i, until, text),
            i => text.Length > 40 && _plain[i].Contains(text[..40], StringComparison.OrdinalIgnoreCase),
        };
        foreach (var start in forward > from ? new[] { forward, from } : [from])
        {
            foreach (var test in tests)
            {
                for (var i = start; i < until; i++)
                {
                    if (!used.Contains(i) && test(i))
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>The indexes (0-based) where the raw lines of a row stand in the file as whole lines, in order.</summary>
    public IEnumerable<int> FindRaw(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var wanted = raw.Split('\n');
        if (wanted.Length == 0 || wanted[0].Length == 0)
        {
            yield break;
        }

        for (var i = 0; i + wanted.Length <= _lines.Count; i++)
        {
            var same = true;
            for (var j = 0; j < wanted.Length && same; j++)
            {
                same = string.Equals(_lines[i + j].TrimEnd(), wanted[j], StringComparison.Ordinal);
            }

            if (same)
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// The row that begins on line <paramref name="start"/> (0-based), given every line a row was found on
    /// (<paramref name="starts"/>): a table row is its line; any other row runs to the line before the next row, the
    /// next heading, a table, or an entry written like it at its own depth or above, so an entry the reader did not
    /// return is not taken into the row before it. Its text is its own lines; its raw lines are all that belong to it.
    /// </summary>
    public RowBlock Block(int start, IReadOnlySet<int> starts, IReadOnlyList<IdScheme> schemes, int? titleColumn = null)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(schemes);
        if (_lines.Count == 0)
        {
            return new RowBlock(1, 1, "paragraph", "", "", "", null);
        }

        start = Math.Clamp(start, 0, _lines.Count - 1);
        if (IsTableLine(_lines[start]))
        {
            return TableRow(start, schemes, titleColumn);
        }

        var heading = IsHeading(_lines[start]);
        var level = heading ? HeadingLevel(_lines[start]) : 0;
        var indent = Width(Indent().Match(_lines[start]).Value);
        var marked = MarkerOf(_lines[start]) is not null;

        // A row with no mark, in a run of lines where another row begins: the run holds a row a line, so this row is
        // its line alone, whichever of the run's rows the reader returned.
        var alone = !heading && !marked && RunOfLines(start) is var (runFirst, runLast) && Enumerable.Range(runFirst, runLast - runFirst + 1).Any(i => i != start && starts.Contains(i));
        var wide = start;
        for (var j = start + 1; j < _lines.Count && !alone; j++)
        {
            var line = _lines[j];
            if (starts.Contains(j) || IsTableLine(line) || (IsHeading(line) && (!heading || HeadingLevel(line) <= level)))
            {
                break;
            }

            // An entry written like this one, at its depth or above it, is another row, returned or not.
            if (!heading && marked && !IsBlank(line) && Width(Indent().Match(line).Value) <= indent && MarkerOf(line) is not null)
            {
                break;
            }

            wide = j;
        }

        while (wide > start && IsBlank(_lines[wide]))
        {
            wide--;
        }

        // The row's own text: its first line and the lines that continue it, to the first blank line that is not
        // followed by a line set under it. A heading that is a row holds its whole section.
        var tight = start;
        if (heading)
        {
            tight = wide;
        }
        else
        {
            for (var j = start + 1; j <= wide; j++)
            {
                if (IsBlank(_lines[j]))
                {
                    var next = j + 1;
                    while (next <= wide && IsBlank(_lines[next]))
                    {
                        next++;
                    }

                    if (next > wide || Width(Indent().Match(_lines[next]).Value) <= indent)
                    {
                        break;
                    }

                    continue;
                }

                tight = j;
            }
        }

        var firstLine = heading ? _lines[start].Trim().TrimStart('#').Trim().TrimEnd('#').Trim() : WithoutMarker(_lines[start]);
        var column = heading ? 0 : _lines[start].Length - firstLine.Length;
        var body = new List<string> { firstLine.TrimEnd() };
        var paragraph = new StringBuilder(firstLine.Trim());

        // A list entry's title may run over its wrapped lines. A row with no mark has no such sign of where it ends,
        // so its title and its key come from its first line alone, whatever the reader left out below it.
        var inParagraph = !heading && marked;
        for (var j = start + 1; j <= tight; j++)
        {
            var line = _lines[j];
            var strip = 0;
            while (strip < line.Length && strip < column && (line[strip] == ' ' || line[strip] == '\t'))
            {
                strip++;
            }

            body.Add(line[strip..].TrimEnd());
            if (inParagraph && (IsBlank(line) || MarkerOf(line) is not null))
            {
                inParagraph = false;
            }
            else if (inParagraph)
            {
                paragraph.Append(' ').Append(line.Trim());
            }
        }

        var (title, id, atEnd) = TitleParts(paragraph.ToString(), schemes, cut: !heading, firstLine: firstLine);
        return new RowBlock(start + 1, wide + 1, heading ? "heading" : marked ? "list" : "paragraph", Raw(start, wide), string.Join("\n", body).Trim(), title, id, atEnd);
    }

    /// <summary>The run of lines around line <paramref name="index"/> that are neither blank, a heading, a table row nor an entry with a mark: a paragraph, or a list written a row a line with no marks.</summary>
    private (int First, int Last) RunOfLines(int index)
    {
        bool Plain(int i) => !IsBlank(_lines[i]) && !IsHeading(_lines[i]) && !IsTableLine(_lines[i]) && MarkerOf(_lines[i]) is null;
        var first = index;
        while (first > 0 && Plain(first - 1))
        {
            first--;
        }

        var last = index;
        while (last + 1 < _lines.Count && Plain(last + 1))
        {
            last++;
        }

        return (first, last);
    }

    /// <summary>
    /// Every line of the ledger that is written as an entry like the rows found in it (<paramref name="starts"/>), by
    /// code (the third re-check): the final accounting of a ledger sets each of them against an item. A row of a table
    /// that holds a row found; an entry with a mark at the depth of a row found, under the same entry above it and
    /// the same heading; a heading of the level of a row that is a heading, under the same heading above it, when no
    /// row found lies under it; a line of a run of unmarked lines in which two rows were found; and the first line of
    /// a paragraph at the depth of two or more rows found as such under the same heading. Each line a row was found on
    /// is among them. What an entry says is not judged here: one that is no work is a person's to pass over.
    /// </summary>
    public SortedSet<int> Entries(IReadOnlySet<int> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        var entries = new SortedSet<int>(starts.Where(s => s >= 0 && s < _lines.Count));
        if (entries.Count == 0)
        {
            return entries;
        }

        // The tables that hold a row found: every row of theirs.
        var seenTables = new HashSet<int>();
        foreach (var start in starts.Where(s => s >= 0 && s < _lines.Count && IsTableLine(_lines[s])))
        {
            var table = TableAt(start)!;
            if (!seenTables.Add(table.First))
            {
                continue;
            }

            var hasHeader = table.HeaderLine.Length > 0;
            for (var i = table.First + (hasHeader ? 2 : 0); i <= table.Last; i++)
            {
                if (!Cells(_lines[i]).All(c => Separator().IsMatch(c)))
                {
                    entries.Add(i);
                }
            }
        }

        var (headingAbove, entryAbove, depth, kind) = Shape();

        // Entries with a mark: the same depth, the same entry above, the same heading above.
        var places = starts.Where(s => s >= 0 && s < _lines.Count && kind[s] == 'm').Select(s => (headingAbove[s], entryAbove[s], depth[s])).ToHashSet();
        if (places.Count > 0)
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                if (kind[i] == 'm' && places.Contains((headingAbove[i], entryAbove[i], depth[i])))
                {
                    entries.Add(i);
                }
            }
        }

        // Headings: the same level under the same heading above, when no row found lies under them.
        var levels = starts.Where(s => s >= 0 && s < _lines.Count && kind[s] == 'h').Select(s => (headingAbove[s], HeadingLevel(_lines[s]))).ToHashSet();
        if (levels.Count > 0)
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                if (kind[i] != 'h' || !levels.Contains((headingAbove[i], HeadingLevel(_lines[i]))))
                {
                    continue;
                }

                var level = HeadingLevel(_lines[i]);
                var end = i + 1;
                while (end < _lines.Count && !(kind[end] == 'h' && HeadingLevel(_lines[end]) <= level))
                {
                    end++;
                }

                if (!starts.Any(s => s > i && s < end))
                {
                    entries.Add(i);
                }
            }
        }

        // Lines with no mark: every line of a run in which two rows were found, and the first line of every paragraph
        // at the depth of two or more rows found as the first line of theirs under the same heading.
        var plain = starts.Where(s => s >= 0 && s < _lines.Count && kind[s] is 'p' or 'c').ToList();
        foreach (var run in plain.Select(RunOfLines).Distinct().Where(r => plain.Count(s => s >= r.First && s <= r.Last) > 1))
        {
            for (var i = run.First; i <= run.Last; i++)
            {
                entries.Add(i);
            }
        }

        var paragraphs = plain.Where(s => kind[s] == 'p').GroupBy(s => (headingAbove[s], depth[s])).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (paragraphs.Count > 0)
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                if (kind[i] == 'p' && paragraphs.Contains((headingAbove[i], depth[i])))
                {
                    entries.Add(i);
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// What every line of the ledger is, read from the file alone: its depth, the heading above it (for a heading,
    /// the heading it sits under), for an entry with a mark the entry above it, and its kind: blank, h a heading,
    /// t a table line, q a quoted line, m an entry with a mark, p the first line of a run of words, c a line that
    /// continues one.
    /// </summary>
    private (int[] HeadingAbove, int[] EntryAbove, int[] Depth, char[] Kind) Shape()
    {
        if (_shape is { } known)
        {
            return known;
        }

        var headingAbove = new int[_lines.Count];
        var entryAbove = new int[_lines.Count];
        var depth = new int[_lines.Count];
        var kind = new char[_lines.Count];
        var headings = new List<int>();
        var marks = new List<int>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            depth[i] = Width(Indent().Match(line).Value);
            headingAbove[i] = headings.Count == 0 ? -1 : headings[^1];
            entryAbove[i] = -1;
            if (IsBlank(line))
            {
                kind[i] = ' ';
            }
            else if (IsHeading(line))
            {
                kind[i] = 'h';
                var level = HeadingLevel(line);
                headings.RemoveAll(h => HeadingLevel(_lines[h]) >= level);
                headingAbove[i] = headings.Count == 0 ? -1 : headings[^1];
                headings.Add(i);
                marks.Clear();
            }
            else if (IsTableLine(line))
            {
                kind[i] = 't';
                marks.Clear();
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                // A quoted line is a quotation, not an entry, whatever stands in it.
                kind[i] = 'q';
            }
            else if (MarkerOf(line) is not null)
            {
                kind[i] = 'm';
                marks.RemoveAll(m => depth[m] >= depth[i]);
                entryAbove[i] = marks.Count == 0 ? -1 : marks[^1];
                marks.Add(i);
            }
            else
            {
                // A line of words: the first of its run when a blank line, a heading or a table stands before it.
                kind[i] = i == 0 || kind[i - 1] is ' ' or 'h' or 't' ? 'p' : 'c';
            }
        }

        _shape = (headingAbove, entryAbove, depth, kind);
        return _shape.Value;
    }

    private (int[] HeadingAbove, int[] EntryAbove, int[] Depth, char[] Kind)? _shape;

    /// <summary>
    /// The lines of the ledger that are written as entries, read from the file alone, whatever rows were found
    /// (the fourth re-check): every row of every table, and every entry with a mark that stands under no other
    /// entry; a quoted line never. A ledger with neither is counted by its lines of words. The accounting of a
    /// section, a chunk or a ledger the reader returned no row for starts from these.
    /// </summary>
    public SortedSet<int> Census()
    {
        var (_, entryAbove, _, kind) = Shape();
        var found = new SortedSet<int>();
        var table = (TableAround?)null;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (kind[i] == 'm' && entryAbove[i] < 0)
            {
                found.Add(i);
            }
            else if (kind[i] == 't')
            {
                table = table is not null && i <= table.Last ? table : TableAt(i)!;
                if (i >= table.First + (table.HeaderLine.Length > 0 ? 2 : 0) && !Cells(_lines[i]).All(c => Separator().IsMatch(c)))
                {
                    found.Add(i);
                }
            }
        }

        if (found.Count == 0)
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                if (kind[i] is 'p' or 'c')
                {
                    found.Add(i);
                }
            }
        }

        return found;
    }

    /// <summary>The heading whose own lines hold line <paramref name="index"/> (0-based): the line itself when it is a heading, else the nearest heading above it; -1 before the first heading.</summary>
    public int SectionOf(int index)
    {
        var (headingAbove, _, _, kind) = Shape();
        return index < 0 || index >= _lines.Count ? -1 : kind[index] == 'h' ? index : headingAbove[index];
    }

    /// <summary>The headings a section sits under, nearest first: for the line of a heading, the line of each heading above it.</summary>
    public IEnumerable<int> SectionsAbove(int heading)
    {
        var (headingAbove, _, _, kind) = Shape();
        for (var at = heading >= 0 && heading < _lines.Count && kind[heading] == 'h' ? headingAbove[heading] : -1; at >= 0; at = headingAbove[at])
        {
            yield return at;
        }
    }

    /// <summary>The words of the heading on line <paramref name="index"/> (0-based), without its marks; empty for the lines before the first heading.</summary>
    public string HeadingText(int index) =>
        index < 0 || index >= _lines.Count ? "" : _lines[index].Trim().TrimStart('#').Trim().TrimEnd('#').Trim();

    /// <summary>The id of a confirmed scheme that a line written as an entry or a heading opens with, normalised, or null: a row that opens with an id is the row of that id.</summary>
    public string? LeadIdAt(int index, IReadOnlyList<IdScheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        if (index < 0 || index >= _lines.Count || IsTableLine(_lines[index]) || IsBlank(_lines[index]))
        {
            return null;
        }

        var line = _lines[index];
        var text = IsHeading(line) ? line.Trim().TrimStart('#').Trim() : MarkerOf(line) is not null ? WithoutMarker(line) : null;
        if (text is null)
        {
            return null;
        }

        var whole = Spaces().Replace(Link().Replace(text, "$1"), " ").Trim();
        return LeadId(AnyCheckbox().Replace(whole, ""), schemes).Id;
    }

    /// <summary>The table around line <paramref name="index"/> (0-based), or null when the line is no table row.</summary>
    public TableAround? TableAt(int index)
    {
        if (index < 0 || index >= _lines.Count || !IsTableLine(_lines[index]))
        {
            return null;
        }

        var first = index;
        while (first > 0 && IsTableLine(_lines[first - 1]))
        {
            first--;
        }

        var last = index;
        while (last + 1 < _lines.Count && IsTableLine(_lines[last + 1]))
        {
            last++;
        }

        var table = Range(first, last).Select(Cells).ToList();
        var hasHeader = table.Count > 1 && table[1].All(c => Separator().IsMatch(c));
        var header = hasHeader ? table[0].Select(LedgerTable.Bare).ToArray() : [];
        var rows = table.Skip(hasHeader ? 2 : 0).Where(r => !r.All(c => Separator().IsMatch(c))).Select(r => (IReadOnlyList<string>)r).ToList();
        return new TableAround(first, last, hasHeader ? _lines[first].Trim() : "", header, Cells(_lines[index]), rows);
    }

    /// <summary>
    /// A row of a table. The title is the cell of <paramref name="titleColumn"/> when the caller chose one (by what
    /// the reader's titles point at, over the table's rows), else the first cell that is neither the row's number, a
    /// date nor the status; the text is that cell, then every other cell but the number under its header.
    /// </summary>
    private RowBlock TableRow(int index, IReadOnlyList<IdScheme> schemes, int? titleColumn)
    {
        var table = TableAt(index)!;
        var cells = table.Cells;
        var header = table.Header;
        var rows = table.Rows;
        var width = Math.Max(header.Count, rows.Count == 0 ? cells.Count : rows.Max(r => r.Count));

        bool All(int column, Func<string, bool> test)
        {
            var filled = rows.Where(r => column < r.Count && r[column].Length > 0).Select(r => LedgerTable.Bare(r[column])).ToList();
            return filled.Count > 0 && filled.All(test);
        }

        var idColumn = -1;
        var statusColumn = -1;
        var dates = new HashSet<int>();
        for (var c = 0; c < width; c++)
        {
            var head = c < header.Count ? header[c] : "";
            if (c == titleColumn)
            {
                continue;
            }

            if (idColumn < 0 && (NumberHead().IsMatch(head) || All(c, ItemsPhase.IsLocalNumber) || All(c, v => AnyCase(v, schemes) is not null)))
            {
                idColumn = c;
            }
            else if (statusColumn < 0 && StatusHead().IsMatch(head))
            {
                statusColumn = c;
            }
            else if (All(c, v => Date().IsMatch(v)))
            {
                dates.Add(c);
            }
        }

        var chosen = titleColumn is { } given && given >= 0 && given < cells.Count ? given : -1;
        for (var c = 0; c < width && chosen < 0; c++)
        {
            if (c != idColumn && c != statusColumn && !dates.Contains(c) && rows.Any(r => c < r.Count && r[c].Length > 0))
            {
                chosen = c;
            }
        }

        var parts = new List<string>();
        var titleCell = chosen >= 0 && chosen < cells.Count ? cells[chosen] : "";
        if (titleCell.Length > 0)
        {
            parts.Add(titleCell.StartsWith("**", StringComparison.Ordinal) && titleCell.EndsWith("**", StringComparison.Ordinal) && titleCell.Length > 4 && titleCell.IndexOf("**", 2, StringComparison.Ordinal) == titleCell.Length - 2
                ? titleCell[2..^2].Trim()
                : titleCell);
        }

        foreach (var c in Enumerable.Range(0, cells.Count).Where(c => c != idColumn && c != chosen && cells[c].Length > 0).OrderBy(c => c == statusColumn ? 1 : 0))
        {
            parts.Add(c < header.Count && header[c].Length > 0 ? $"**{header[c]}**: {cells[c]}" : cells[c]);
        }

        var idCell = idColumn >= 0 && idColumn < cells.Count ? LedgerTable.Bare(cells[idColumn]) : "";
        var known = AnyCase(idCell, schemes);
        var (title, leadId) = TitleOf(titleCell.Length > 0 ? titleCell : string.Join(" ", cells), schemes, cut: false);
        return new RowBlock(index + 1, index + 1, "table", _lines[index].TrimEnd(), string.Join("\n\n", parts), title, known?.Key ?? leadId);
    }

    /// <summary>
    /// A row's title by code, and its id. The id is the id of a confirmed scheme that the row opens with, whatever
    /// stands around it (emphasis, brackets, a link, a strike, a tick, a checkbox of any kind, upper or lower case),
    /// or the one alone in the bracket that ends the row's first line. The title is the lead of the row without that:
    /// without a date it opens with; the bold span it opens with; else, for a list entry or a paragraph, the words
    /// before the first sentence end, or before a colon, a semicolon or a spaced dash after at least two words; a label
    /// with a number ("Task 1", "UI-2") that is no id of a scheme stays in front of it. Nothing is reworded.
    /// </summary>
    public static (string Title, string? Id) TitleOf(string text, IReadOnlyList<IdScheme> schemes, bool cut, string? firstLine = null)
    {
        var (title, id, _) = TitleParts(text, schemes, cut, firstLine);
        return (title, id);
    }

    /// <summary>As <see cref="TitleOf"/>, and whether the id stands in the bracket that ends the first line and not at the lead.</summary>
    public static (string Title, string? Id, bool IdAtEnd) TitleParts(string text, IReadOnlyList<IdScheme> schemes, bool cut, string? firstLine = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schemes);
        var whole = Spaces().Replace(Link().Replace(text, "$1"), " ").Trim();
        var rest = AnyCheckbox().Replace(whole, "");

        // The id the row opens with: past whatever is no letter and no digit, by rule and not by a list of marks.
        string? id = null;
        var atEnd = false;
        var (leadId, afterId) = LeadId(rest, schemes);
        if (leadId is not null)
        {
            id = leadId;
            rest = afterId;
        }
        else
        {
            // The id alone in the bracket that ends the first line: "Do the first thing (BL-1)".
            var last = FinalBracket().Match(Spaces().Replace(Link().Replace(firstLine ?? text, "$1"), " ").Trim());
            if (last.Success && AnyCase(last.Groups["token"].Value, schemes) is { } final && final.Key.Length >= last.Groups["token"].Value.Trim().Length - 1)
            {
                id = final.Key;
                atEnd = true;
                var bracket = last.Value.TrimEnd();
                var at = rest.IndexOf(bracket, StringComparison.Ordinal);
                if (at > 0)
                {
                    rest = (rest[..at].TrimEnd() + " " + rest[(at + bracket.Length)..].TrimStart()).Trim();
                }
            }
        }

        rest = LeadMarks(rest);
        var dated = DatePrefix().Match(rest);
        if (dated.Success && dated.Length < rest.Length)
        {
            rest = rest[dated.Length..];
        }

        string? label = null;
        if (id is null)
        {
            var labelled = LabelLead().Match(rest);
            if (labelled.Success && (labelled.Groups["wrap"].Success || labelled.Groups["sep"].Success))
            {
                label = labelled.Groups["label"].Value;
                rest = labelled.Groups["rest"].Value;
            }
        }

        var words = Lead(rest, cut || rest.Length > 120);
        var title = label is null ? words : words.Length == 0 ? label : label + ": " + words;
        title = Shown(title).TrimEnd('.', ':', ';', ',').Trim();
        if (title.Length == 0)
        {
            title = Shown(rest.Length > 0 ? rest : whole).TrimEnd('.', ':', ';', ',').Trim();
        }

        if (title.Length == 0 && id is not null)
        {
            title = id;
        }

        if (title.Length > 200)
        {
            var at = title.LastIndexOf(' ', 200);
            title = title[..(at > 80 ? at : 200)].TrimEnd(',', ';', ':', '-', ' ');
        }

        return (title, id, atEnd);
    }

    /// <summary>The id of a confirmed scheme a text opens with, past any marks before it, and the text after it and after any further ids beside it.</summary>
    private static (string? Id, string After) LeadId(string text, IReadOnlyList<IdScheme> schemes)
    {
        string? first = null;
        var rest = text;
        while (true)
        {
            var i = 0;
            while (i < rest.Length && !char.IsLetterOrDigit(rest[i]))
            {
                i++;
            }

            if (i >= rest.Length || rest[..i].Count(c => c == ' ') > 2 || AnyCase(rest[i..], schemes) is not { } known)
            {
                return (first, first is null ? text : rest);
            }

            // The id's own characters, then what closes its wrapping, then what parts it from the row's words.
            var j = i;
            while (j < rest.Length && (char.IsLetterOrDigit(rest[j]) || rest[j] is '-' or '_' or '#' || (rest[j] == '.' && j + 1 < rest.Length && char.IsLetterOrDigit(rest[j + 1]))))
            {
                j++;
            }

            if (known.Suffix is { } suffix && suffix.StartsWith('(') && rest[j..].TrimStart().StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                j = rest.IndexOf(suffix, j, StringComparison.OrdinalIgnoreCase) + suffix.Length;
            }

            while (j < rest.Length && !char.IsLetterOrDigit(rest[j]) && rest[j] != ' ')
            {
                j++;
            }

            while (j < rest.Length && rest[j] is ' ' or ':' or ',' or '-' or '\u2013' or '\u2014' or '.' or '/' or '&' or '|' or ';')
            {
                j++;
            }

            first ??= known.Key;
            rest = rest[j..];
        }
    }

    /// <summary>An id of a confirmed scheme at the start of a text, as written or in the other case: a scheme's ids are the same ids in lower case.</summary>
    internal static NormalisedId? AnyCase(string text, IReadOnlyList<IdScheme> schemes) =>
        IdNormaliser.Normalise(text, schemes) ?? IdNormaliser.Normalise(text.ToUpperInvariant(), schemes) ?? IdNormaliser.Normalise(text.ToLowerInvariant(), schemes);

    /// <summary>The text without the marks it opens with that are no part of its words: a tick, a strike, an arrow, a bullet of any kind.</summary>
    private static string LeadMarks(string text)
    {
        var i = 0;
        while (i < text.Length && !char.IsLetterOrDigit(text[i]) && !OpensWords(text[i]))
        {
            i++;
        }

        return i < text.Length ? text[i..] : text;
    }

    /// <summary>True for a mark a title may open with: emphasis, a code mark, a quote, an opening bracket, a sign before a number or a name.</summary>
    private static bool OpensWords(char c) =>
        c is '*' or '_' or '`' or '"' or '\'' or '(' or '[' or '{' or '<' or '#' or '@' or '$' or '/' or '.'
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.OpenPunctuation;

    private static string Lead(string text, bool cut)
    {
        text = AfterNote(text);
        foreach (var mark in new[] { "**", "__" })
        {
            if (text.StartsWith(mark, StringComparison.Ordinal))
            {
                var close = text.IndexOf(mark, 2, StringComparison.Ordinal);
                if (close > 2)
                {
                    // The bold span the row opens with is its title; a span that runs on for sentences gives its first.
                    var span = text[2..close].Trim();
                    var after = text[(close + 2)..].TrimStart();
                    if (span.StartsWith('[') && span.EndsWith(']') && after.Length >= 3)
                    {
                        // A note in brackets set in bold before the row's own words: the title is what follows it.
                        return Lead(after, cut);
                    }

                    return span.Length > 120 ? FirstWords(span) : span;
                }
            }
        }

        return cut ? FirstWords(text) : text;
    }

    /// <summary>The text after a note in square brackets that it opens with ("[2026-09-24: checked by hand] The title"), when something follows the note; a link is no note.</summary>
    private static string AfterNote(string text)
    {
        if (!text.StartsWith('['))
        {
            return text;
        }

        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                var rest = text[(i + 1)..].TrimStart(' ', ':', '-', '\u2013', '\u2014');
                return i + 1 < text.Length && text[i + 1] != '(' && rest.Length >= 3 ? rest : text;
            }
        }

        return text;
    }

    /// <summary>
    /// The words a row opens with, up to where its account of itself begins: a sentence end, or a colon, a semicolon
    /// or a spaced dash after at least two words (one word before a dash or a colon is a label, and the title goes on).
    /// Never inside brackets or code marks.
    /// </summary>
    private static string FirstWords(string text)
    {
        var depth = 0;
        var code = false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                code = !code;
            }
            else if (!code && c is '(' or '[')
            {
                depth++;
            }
            else if (!code && c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (code || depth > 0 || i < 3 || text[i + 1] != ' ')
            {
                continue;
            }

            if (c is '?' or '!')
            {
                return text[..(i + 1)];
            }

            if (c == '.' && !IsAbbreviation(text, i))
            {
                return text[..i];
            }

            if (c is ':' or ';' && SeveralWords(text[..i]))
            {
                return text[..i];
            }

            if (c is '-' or '\u2013' or '\u2014' && text[i - 1] == ' ' && SeveralWords(text[..(i - 1)]))
            {
                return text[..(i - 1)];
            }
        }

        return text;
    }

    /// <summary>True for two or more words of letters: "supply check" is a title, "Wave 1", "#05" and "MEASURED 2026-08-09" are labels in front of one.</summary>
    private static bool SeveralWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter) && !w.Any(char.IsDigit)) >= 2;

    /// <summary>True when the period at <paramref name="at"/> ends an abbreviation, a number or a name with periods in it, not a sentence.</summary>
    private static bool IsAbbreviation(string text, int at)
    {
        var start = at;
        while (start > 0 && text[start - 1] != ' ')
        {
            start--;
        }

        var word = text[start..at];
        return word.Length == 0 || word.Contains('.', StringComparison.Ordinal) || word.All(char.IsDigit)
            || Abbreviations.Contains(word.TrimStart('(', '"', '\''), StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] Abbreviations = ["e.g", "i.e", "eg", "ie", "vs", "etc", "no", "nr", "ca", "cf", "approx", "incl", "resp", "st", "mr", "mrs", "dr"];

    /// <summary>A title as it is shown: without emphasis, a strike and a link's target, with its code marks kept, since they are how a row writes a name.</summary>
    private static string Shown(string text)
    {
        // What sits between code marks is left as written: a star there is part of a name, not emphasis.
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i += 2)
        {
            var shown = Link().Replace(parts[i], "$1");
            shown = shown.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("~~", "", StringComparison.Ordinal);
            parts[i] = Emphasis().Replace(shown, "");
        }

        return Spaces().Replace(string.Join('`', parts), " ").Trim();
    }

    /// <summary>True for a cell that holds only a date or a number: never a row's title.</summary>
    public static bool IsDateOrNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return trimmed.Length == 0 || Date().IsMatch(trimmed) || trimmed.All(c => char.IsDigit(c) || c is '.' or '#');
    }

    /// <summary>A line or a title without its markdown: emphasis, a strike, code marks, a link's target; runs of spaces made one.</summary>
    public static string Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var plain = Link().Replace(text, "$1");
        plain = plain.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("~~", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        plain = Emphasis().Replace(plain, "");
        return Spaces().Replace(plain, " ").Trim();
    }

    /// <summary>
    /// The mark a line opens with when it is written as an entry of a list: a first token with no letter in it, of four
    /// characters at most (a hyphen, a bullet of any kind, a box, "1.", "2)"), or a checkbox in brackets. Null for a
    /// line that opens with words.
    /// </summary>
    internal static string? MarkerOf(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0)
        {
            return null;
        }

        var box = AnyCheckbox().Match(text);
        if (box.Success)
        {
            return box.Value;
        }

        var end = text.IndexOf(' ', StringComparison.Ordinal);
        var token = end < 0 ? text : text[..end];
        return token.Length is > 0 and <= 4 && end > 0 && !token.Any(char.IsLetter) && !token.StartsWith('|') && !token.StartsWith('#') ? token : null;
    }

    /// <summary>A line without the mark it opens with as an entry, and without a checkbox after that mark.</summary>
    private static string WithoutMarker(string line)
    {
        var text = line.TrimStart();
        var marker = MarkerOf(text);
        if (marker is not null)
        {
            text = text[marker.Length..].TrimStart();
        }

        return AnyCheckbox().Replace(text, "");
    }

    /// <summary>
    /// True when the title leads the line: nothing stands before it, in its own cell, but marks, at most four tokens,
    /// and at most one of them a word (a label such as "Task" before its number). A title after a run of words is a
    /// mention inside another row.
    /// </summary>
    private static bool Leads(string line, string title, bool whole)
    {
        for (var at = line.IndexOf(title, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(title, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + title.Length;
            if (whole && end < line.Length && char.IsLetterOrDigit(line[end]) && char.IsLetterOrDigit(title[^1]))
            {
                continue;
            }

            var before = line[..at];
            var cell = before.LastIndexOf('|');
            var tokens = (cell >= 0 ? before[(cell + 1)..] : before).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 4 && tokens.Count(t => t.Any(char.IsLetter) && !t.Any(char.IsDigit)) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the title begins on line <paramref name="i"/> and runs on over the next line or two.</summary>
    private bool Wrapped(int i, int until, string text)
    {
        if (i + 1 >= until)
        {
            return false;
        }

        var joined = _plain[i] + " " + _plain[i + 1] + (i + 2 < until ? " " + _plain[i + 2] : "");
        var at = joined.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at < _plain[i].Length;
    }

    /// <summary>A reader's title as it is looked for in a line: without markup, and without the ellipsis a reader cuts a long title with.</summary>
    internal static string Needle(string? title) => Plain(TrimEllipsis(title ?? ""));

    private static string TrimEllipsis(string text)
    {
        var trimmed = text.Trim();
        while (trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith((char)0x2026))
        {
            trimmed = trimmed.EndsWith((char)0x2026) ? trimmed[..^1].TrimEnd() : trimmed[..^3].TrimEnd();
        }

        return trimmed;
    }

    /// <summary>True when the line holds the text and the text does not stop inside a word or a number ("Task 1" is not in "Task 10").</summary>
    private static bool HoldsWhole(string line, string text, StringComparison comparison)
    {
        for (var at = line.IndexOf(text, comparison); at >= 0; at = line.IndexOf(text, at + 1, comparison))
        {
            var end = at + text.Length;
            if (end >= line.Length || !char.IsLetterOrDigit(line[end]) || !char.IsLetterOrDigit(text[^1]))
            {
                return true;
            }
        }

        return false;
    }

    private string Raw(int start, int end) => string.Join("\n", Range(start, end).Select(l => l.TrimEnd()));

    private IEnumerable<string> Range(int start, int end) => Enumerable.Range(start, Math.Max(0, end - start + 1)).Select(i => _lines[i]);

    private static string[] Cells(string line) => line.TrimEnd('\r').Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    internal static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool IsHeading(string line) => Heading().IsMatch(line);

    private static int HeadingLevel(string line) => line.TrimStart().TakeWhile(c => c == '#').Count();

    private static int Width(string indent) => indent.Sum(c => c == '\t' ? 4 : 1);

    [GeneratedRegex(@"^[ \t]*")]
    private static partial Regex Indent();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+\S")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^\s*(#|no\.?|nr\.?|id|key)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberHead();

    [GeneratedRegex(@"^\s*(status|state|standing)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StatusHead();

    [GeneratedRegex(@"^\(?\d{4}-\d{2}-\d{2}\)?$")]
    private static partial Regex Date();

    /// <summary>A checkbox of any kind: brackets around nothing, a space or one character.</summary>
    [GeneratedRegex(@"^\[[^\]\s]?\s?\]\s+")]
    private static partial Regex AnyCheckbox();

    [GeneratedRegex(@"^\(?\d{4}-\d{2}-\d{2}\)?\s*[:,\-\u2013\u2014]?\s+")]
    private static partial Regex DatePrefix();

    [GeneratedRegex(@"[\(\[]\s*(?<token>[^\s()\[\]]{2,40})\s*[\)\]]\s*[.;:]?\s*$")]
    private static partial Regex FinalBracket();

    [GeneratedRegex(@"^(?<wrap>\*\*|__|`)?(?<label>\p{L}[\p{L}\p{N}]{0,11}(?:[- ]#?|#)?\d{1,6}[a-zA-Z]?)(?(wrap)\k<wrap>)\s*(?<sep>[:.)\-\u2013\u2014]+)?\s+(?<rest>\S.*)$")]
    private static partial Regex LabelLead();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\*(?=\S)|(?<=\S)\*(?![\p{L}\p{N}])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
