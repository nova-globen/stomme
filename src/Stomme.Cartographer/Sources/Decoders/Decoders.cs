using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Stomme.Cartographer.Sources.Decoders;

/// <summary>A heading and the lines under it, until the next heading.</summary>
public sealed record Section(int Level, string Heading, int StartLine, int EndLine);

/// <summary>A file turned into text with its structure kept (ADR 0014). Lines are 1-based in every reference.</summary>
public sealed record DecodedDocument(
    string Path,
    string Text,
    IReadOnlyList<string> Lines,
    string? Title,
    IReadOnlyList<Section> Sections,
    string Format,
    string? Note)
{
    /// <summary>Lines <paramref name="start"/> to <paramref name="end"/>, 1-based and inclusive.</summary>
    public string Slice(int start, int end)
    {
        start = Math.Max(1, start);
        end = Math.Min(Lines.Count, end);
        if (end < start)
        {
            return "";
        }

        return string.Join('\n', Lines.Skip(start - 1).Take(end - start + 1));
    }
}

/// <summary>One decoder per format (ADR 0014). A decoder reads bytes and never touches the file system.</summary>
public interface IDecoder
{
    string Format { get; }

    bool CanDecode(SourceFile file);

    DecodedDocument Decode(SourceFile file, byte[] bytes);
}

/// <summary>The decoders available, asked in order; plain text is the fallback for any text file.</summary>
public sealed class DecoderRegistry
{
    private readonly List<IDecoder> _decoders;

    public DecoderRegistry(IEnumerable<IDecoder> decoders)
    {
        _decoders = decoders.ToList();
    }

    public static DecoderRegistry Default { get; } = new([new MarkdownDecoder(), new TextDecoder()]);

    /// <summary>The decoder that reads the file, or null: none for its format, or the file is marked not to be read.</summary>
    public IDecoder? For(SourceFile file) => file.NotRead is not null ? null : _decoders.FirstOrDefault(d => d.CanDecode(file));

    public bool CanDecode(SourceFile file) => For(file) is not null;

    /// <summary>Decodes a file from disk; null when no decoder reads it.</summary>
    public DecodedDocument? Decode(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var decoder = For(file);
        if (decoder is null)
        {
            return null;
        }

        return decoder.Decode(file, File.ReadAllBytes(file.AbsolutePath));
    }
}

/// <summary>Text helpers shared by the decoders.</summary>
public static class TextBytes
{
    /// <summary>Decodes UTF-8 (with or without a BOM) or UTF-16 with a BOM; invalid bytes become replacement characters.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, false).GetString(bytes, offset, bytes.Length - offset);
    }

    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }
}

/// <summary>Markdown through Markdig: headings become sections, the text is kept as written.</summary>
public sealed class MarkdownDecoder : IDecoder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseYamlFrontMatter().Build();

    public string Format => "markdown";

    public bool CanDecode(SourceFile file) =>
        file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
        || file.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
        || file.Extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);

    public DecodedDocument Decode(SourceFile file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        var text = TextBytes.Decode(bytes);
        var lines = TextBytes.SplitLines(text);
        var document = Markdown.Parse(text, Pipeline);
        var headings = new List<(int Level, string Text, int Line)>();
        foreach (var block in document.Descendants<HeadingBlock>())
        {
            headings.Add((block.Level, InlineText(block.Inline), block.Line + 1));
        }

        headings.Sort((a, b) => a.Line.CompareTo(b.Line));
        var sections = new List<Section>();
        for (var i = 0; i < headings.Count; i++)
        {
            var end = i + 1 < headings.Count ? headings[i + 1].Line - 1 : lines.Length;
            sections.Add(new Section(headings[i].Level, headings[i].Text, headings[i].Line, end));
        }

        var title = FrontMatterTitle(lines) ?? headings.FirstOrDefault(h => h.Level == 1).Text;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = headings.Count > 0 ? headings[0].Text : null;
        }

        return new DecodedDocument(file.Path, text, lines, title, sections, Format, null);
    }

    private static string? FrontMatterTitle(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return null;
        }

        string? name = null;
        for (var i = 1; i < Math.Min(lines.Length, 60); i++)
        {
            if (lines[i].Trim() == "---")
            {
                // No title in the front matter: its name (an agent's, a skill's) stands in for one.
                return name;
            }

            if (lines[i].StartsWith("name:", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                var value = lines[i]["name:".Length..].Trim().Trim('"', '\'');
                name = value.Length == 0 ? null : value;
            }

            if (lines[i].StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                return lines[i][6..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return "";
        }

        var text = new StringBuilder();
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case ContainerInline container:
                    text.Append(InlineText(container));
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Trim();
    }
}

/// <summary>Plain text, and any text file no other decoder reads: lines as they are, no sections.</summary>
public sealed class TextDecoder : IDecoder
{
    public string Format => "text";

    public bool CanDecode(SourceFile file) => file.IsText;

    public DecodedDocument Decode(SourceFile file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        var text = TextBytes.Decode(bytes);
        var lines = TextBytes.SplitLines(text);
        var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        var title = first is { Length: > 0 and <= 120 } ? first : null;
        return new DecodedDocument(file.Path, text, lines, title, [], Format, null);
    }
}
