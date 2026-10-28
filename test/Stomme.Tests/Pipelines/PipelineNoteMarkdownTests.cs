#nullable enable
using System.Linq;
using Bunit;
using Stomme.Modules.Pipelines.Canvas;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's sticky notes are Markdown: a note's headings, paragraphs (their line breaks kept), lists, bold, italics, code
    /// and links drawn as elements and text - so what an author types is never markup, and a link goes to http, https or mailto
    /// only, in a new tab.
    /// </summary>
    public sealed class PipelineNoteMarkdownTests : BunitContext
    {
        [Fact]
        public void A_notes_markdown_is_drawn_as_headings_paragraphs_lists_and_marks()
        {
            var cut = Render(NoteMarkdown.Render(
                "## Read the orders\nRead **the CSV** and *parse* it,\nthen `log` each - see [the docs](https://example.test/docs).\n\n- one\n- two\n1. first\n2. second\n\n# Big\n### Small"));

            Assert.Equal(("Read the orders", "Big", "Small"), (cut.Find("h5").TextContent, cut.Find("h4").TextContent, cut.Find("h6").TextContent));
            var paragraph = cut.Find("p");
            Assert.Equal("Read the CSV and parse it,\nthen log each - see the docs.", paragraph.TextContent); // its line break kept
            Assert.Equal(("the CSV", "parse", "log"), (paragraph.QuerySelector("strong")!.TextContent, paragraph.QuerySelector("em")!.TextContent, paragraph.QuerySelector("code")!.TextContent));
            var link = paragraph.QuerySelector("a")!;
            Assert.Equal(("https://example.test/docs", "_blank", "noopener noreferrer", "the docs"), (link.GetAttribute("href"), link.GetAttribute("target"), link.GetAttribute("rel"), link.TextContent));
            Assert.Equal(new[] { "one", "two" }, cut.FindAll("ul li").Select(item => item.TextContent));
            Assert.Equal(new[] { "first", "second" }, cut.FindAll("ol li").Select(item => item.TextContent));
        }

        // Nothing typed becomes markup: a tag is its text, a link anywhere but the web or mail is its text as typed, and an
        // underscore inside a word marks nothing.
        [Fact]
        public void What_an_author_types_is_never_markup()
        {
            var cut = Render(NoteMarkdown.Render("<script>alert(1)</script> <b>bold?</b>\n[bad](javascript:alert(1)) [file](file:///etc/passwd) snake_case_name and _well_ **_both_**"));

            Assert.Empty(cut.FindAll("script"));
            Assert.Empty(cut.FindAll("b"));
            Assert.Empty(cut.FindAll("a"));
            var paragraph = cut.Find("p");
            Assert.Contains("<script>alert(1)</script> <b>bold?</b>", paragraph.TextContent);
            Assert.Contains("[bad](javascript:alert(1)) [file](file:///etc/passwd) snake_case_name", paragraph.TextContent);
            Assert.Equal(new[] { "well", "both" }, cut.FindAll("em").Select(em => em.TextContent));
            Assert.Equal("both", cut.Find("strong em").TextContent); // marks inside marks
            Assert.Contains("mailto:ada@example.test", Render(NoteMarkdown.Render("[mail](mailto:ada@example.test)")).Find("a").GetAttribute("href"));
        }
    }
}
