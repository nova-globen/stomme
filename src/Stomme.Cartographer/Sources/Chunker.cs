using Stomme.Conductor.Models;
using Stomme.Cartographer.Sources.Decoders;

namespace Stomme.Cartographer.Sources;

/// <summary>A piece of a document that fits one call (ADR 0014), with the lines it covers and the overlap it carries.</summary>
public sealed record TextChunk(string Path, int Index, int Count, int StartLine, int EndLine, int OverlapLines, string Text)
{
    public string Position => Count == 1
        ? "the whole file"
        : OverlapLines > 0
            ? $"chunk {Index + 1} of {Count}; its first {OverlapLines} lines repeat the end of the previous chunk"
            : $"chunk {Index + 1} of {Count}";
}

/// <summary>
/// Cuts a document at its structure (ADR 0014): headings first, then blank lines, then table rows, then any
/// line, and a line longer than the budget by characters. Each chunk after the first carries the last lines of
/// the previous one so that nothing is read without its neighbourhood.
/// </summary>
public static class Chunker
{
    /// <param name="maxOverlapChars">The most characters the overlap may carry: a table of long rows repeats fewer lines than prose does.</param>
    public static IReadOnlyList<TextChunk> Chunk(DecodedDocument document, int maxTokens, int overlapLines, int maxOverlapChars = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = document.Lines;
        if (lines.Count == 0 || TokenEstimate.Of(document.Text) <= maxTokens)
        {
            return [new TextChunk(document.Path, 0, 1, 1, Math.Max(1, lines.Count), 0, document.Text)];
        }

        var maxChars = (int)(maxTokens * 3.5);
        var strength = new int[lines.Count + 2];
        foreach (var section in document.Sections)
        {
            if (section.StartLine >= 1 && section.StartLine <= lines.Count)
            {
                strength[section.StartLine] = 100 - section.Level;
            }
        }

        for (var i = 1; i <= lines.Count; i++)
        {
            if (strength[i] > 0)
            {
                continue;
            }

            var line = lines[i - 1];
            if (string.IsNullOrWhiteSpace(line))
            {
                strength[i] = 50;
            }
            else if (line.TrimStart().StartsWith('|'))
            {
                strength[i] = 30;
            }
            else
            {
                strength[i] = 1;
            }
        }

        var ranges = new List<(int Start, int End)>();
        var start = 1;
        while (start <= lines.Count)
        {
            // Take lines until the budget is full; a single line beyond the budget is a chunk of its own, cut below.
            var chars = 0;
            var end = start;
            while (end <= lines.Count)
            {
                var lineChars = lines[end - 1].Length + 1;
                if (chars + lineChars > maxChars && end > start)
                {
                    break;
                }

                chars += lineChars;
                end++;
            }

            var last = Math.Max(start, end - 1);
            if (end <= lines.Count)
            {
                // More lines follow: cut at the strongest boundary in the second half of what fits, the latest among
                // equals, so that a chunk is never shorter than half the budget and a table of long rows is not
                // cut at every row.
                var halfway = start + (last - start) / 2;
                var bestCut = -1;
                var bestStrength = 0;
                for (var cut = last + 1; cut > halfway && cut > start; cut--)
                {
                    if (strength[cut] > bestStrength)
                    {
                        bestStrength = strength[cut];
                        bestCut = cut;
                    }
                }

                if (bestCut > start && bestStrength >= 30)
                {
                    last = bestCut - 1;
                }
            }

            ranges.Add((start, last));
            start = last + 1;
        }

        var chunks = new List<TextChunk>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (s, e) = ranges[i];
            var overlap = i == 0 ? 0 : Math.Min(overlapLines, s - 1);
            if (overlap > 0 && maxOverlapChars < int.MaxValue)
            {
                var overlapChars = 0;
                var kept = 0;
                for (var line = s - 1; line >= s - overlap; line--)
                {
                    overlapChars += document.Lines[line - 1].Length + 1;
                    if (overlapChars > maxOverlapChars)
                    {
                        break;
                    }

                    kept++;
                }

                overlap = kept;
            }

            var text = document.Slice(s - overlap, e);
            if (text.Length > maxChars * 2)
            {
                text = text[..(maxChars * 2)] + "\n[... this line was cut by the engine because it exceeds the budget ...]";
            }

            chunks.Add(new TextChunk(document.Path, i, ranges.Count, s, e, overlap, text));
        }

        return chunks;
    }

    /// <summary>The first lines of a document, up to <paramref name="maxChars"/>, cut at a line boundary.</summary>
    public static (string Text, int EndLine) Head(DecodedDocument document, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(document);
        var chars = 0;
        var end = 0;
        foreach (var line in document.Lines)
        {
            if (chars + line.Length + 1 > maxChars && end > 0)
            {
                break;
            }

            chars += line.Length + 1;
            end++;
            if (chars >= maxChars)
            {
                break;
            }
        }

        var text = document.Slice(1, end);
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
        }

        return (text, end);
    }
}
