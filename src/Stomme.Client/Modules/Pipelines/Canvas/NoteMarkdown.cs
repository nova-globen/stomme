using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// BL-229 - n8n's sticky notes are Markdown: a note's text drawn as the little of it a note needs - headings (#, ##, ###),
    /// paragraphs with their line breaks, lists (- or *, and 1.), and in a line **bold**, *italic* or _italic_, `code` and
    /// [links](https://...). It is built as elements and text, never as HTML, so nothing an author types becomes markup (the
    /// project's Markdown documents are the server's, through Markdig and an allowlist; a note needs none of that). A link goes
    /// to http, https or mailto only, in a new tab, and a press on it is the link's, not the canvas's; any other target is left
    /// as typed.
    /// </summary>
    public static class NoteMarkdown
    {
        private static readonly Regex Heading = new(@"^(#{1,3})\s+(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Bullet = new(@"^\s*[-*]\s+(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+(.+)$", RegexOptions.CultureInvariant);

        // In a line: code first (nothing inside it is read), then bold, a link, italics - an underscore only around a word, never
        // inside one (snake_case stays as typed).
        private static readonly Regex Inline = new(
            @"(?<code>`[^`\n]+`)|(?<bold>\*\*[^*\n]+\*\*)|(?<link>\[(?<text>[^\]\n]+)\]\((?<url>[^)\s]+)\))|(?<em>\*[^*\s][^*\n]*\*|(?<![\w])_[^_\s][^_\n]*_(?![\w]))",
            RegexOptions.CultureInvariant);

        public static RenderFragment Render(string text) => builder =>
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var paragraph = new List<string>();
            string list = null; // "ul" or "ol" while one is open

            void Flush()
            {
                if (paragraph.Count == 0)
                {
                    return;
                }

                builder.OpenElement(0, "p");
                Words(builder, string.Join("\n", paragraph));
                builder.CloseElement();
                paragraph.Clear();
            }

            void CloseList()
            {
                if (list is not null)
                {
                    builder.CloseElement();
                    list = null;
                }
            }

            void Item(string kind, string content)
            {
                Flush();
                if (list != kind)
                {
                    CloseList();
                    builder.OpenElement(1, kind);
                    list = kind;
                }

                builder.OpenElement(2, "li");
                Words(builder, content);
                builder.CloseElement();
            }

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length == 0)
                {
                    Flush();
                    CloseList();
                }
                else if (Heading.Match(trimmed) is { Success: true } heading)
                {
                    Flush();
                    CloseList();
                    builder.OpenElement(3, "h" + (heading.Groups[1].Length + 3)); // h4 to h6: a note's, under the page's own
                    Words(builder, heading.Groups[2].Value);
                    builder.CloseElement();
                }
                else if (Bullet.Match(trimmed) is { Success: true } bullet)
                {
                    Item("ul", bullet.Groups[1].Value);
                }
                else if (Numbered.Match(trimmed) is { Success: true } numbered)
                {
                    Item("ol", numbered.Groups[1].Value);
                }
                else
                {
                    CloseList();
                    paragraph.Add(trimmed);
                }
            }

            Flush();
            CloseList();
        };

        // A line's words, its marks drawn, the rest as text.
        private static void Words(RenderTreeBuilder builder, string text)
        {
            var at = 0;
            foreach (Match match in Inline.Matches(text))
            {
                if (match.Index > at)
                {
                    builder.AddContent(10, text[at..match.Index]);
                }

                if (match.Groups["code"].Success)
                {
                    builder.OpenElement(11, "code");
                    builder.AddContent(12, match.Value[1..^1]);
                    builder.CloseElement();
                }
                else if (match.Groups["bold"].Success)
                {
                    builder.OpenElement(13, "strong");
                    Words(builder, match.Value[2..^2]);
                    builder.CloseElement();
                }
                else if (match.Groups["link"].Success)
                {
                    var url = match.Groups["url"].Value;
                    if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    {
                        builder.OpenElement(14, "a");
                        builder.AddAttribute(15, "href", uri.AbsoluteUri);
                        builder.AddAttribute(16, "target", "_blank");
                        builder.AddAttribute(17, "rel", "noopener noreferrer");
                        builder.AddEventStopPropagationAttribute(18, "onpointerdown", true);
                        builder.AddContent(19, match.Groups["text"].Value);
                        builder.CloseElement();
                    }
                    else
                    {
                        builder.AddContent(20, match.Value);
                    }
                }
                else
                {
                    builder.OpenElement(21, "em");
                    Words(builder, match.Value[1..^1]);
                    builder.CloseElement();
                }

                at = match.Index + match.Length;
            }

            if (at < text.Length)
            {
                builder.AddContent(22, text[at..]);
            }
        }
    }
}
