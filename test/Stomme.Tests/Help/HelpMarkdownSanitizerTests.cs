#nullable enable
using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Stomme.Help;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// Help text reaches ANONYMOUS readers, so nothing in it may run (BL-84 increment 4, D103 item 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every DIRTY case is judged by PARSING the output and asking what a browser would build - no script or frame
    /// element, no <c>on*</c> attribute, no <c>href</c>/<c>src</c> with a refused scheme - never by searching the
    /// markup for a substring, which a harmless escaped <c>&amp;lt;script&amp;gt;</c> would trip and an encoded
    /// payload would slip past.
    /// </para>
    /// <para>
    /// <b>The <c>DisableHtml()</c> question is DRIVEN both ways.</b> Without it Markdig passes raw HTML through; with
    /// <c>UseGenericAttributes</c> it writes an event handler EVEN WITH <c>DisableHtml()</c> - the reason
    /// <c>UseAdvancedExtensions()</c> is banned. Each is shown to happen on the raw Markdig output, and shown to be
    /// neutralised by the sanitiser, so the allowlist is not relying on the pipeline and the pipeline is not relying on
    /// the allowlist.
    /// </para>
    /// </remarks>
    public class HelpMarkdownSanitizerTests
    {
        private readonly ITestOutputHelper _output;
        private readonly HelpMarkdownRenderer _renderer = new(new HelpHtmlSanitizer());
        private readonly HelpHtmlSanitizer _sanitizer = new();

        public HelpMarkdownSanitizerTests(ITestOutputHelper output) => _output = output;

        // ---- DIRTY Markdown, through the real renderer ---------------------------------------------------------------

        [Theory]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("<img src=x onerror=alert(1)>")]
        [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
        [InlineData("[click](javascript:alert(1))")]
        [InlineData("[click](JAVASCRIPT:alert(1))")]
        [InlineData("[click](  javascript:alert(1) )")]
        [InlineData("<data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==>")]
        [InlineData("<javascript:alert(1)>")]
        [InlineData("![x](javascript:alert(1))")]
        [InlineData("![x](data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=)")]
        [InlineData("# Heading {onclick=\"alert(1)\"}")]
        [InlineData("<a href=\"https://x\" onclick=\"alert(1)\">x</a>")]
        [InlineData("<svg><script>alert(1)</script></svg>")]
        [InlineData("<style>body{display:none}</style>")]
        public void Dirty_markdown_renders_to_NOTHING_that_runs(string markdown)
        {
            var html = _renderer.Render(markdown);
            _output.WriteLine($"{markdown}\n  -> {html}");
            AssertInert(html);
        }

        // ---- CLEAN Markdown keeps its meaning ----------------------------------------------------------------------

        [Fact]
        public void Bold_emphasis_and_strikethrough_survive()
        {
            var body = Parse(_renderer.Render("**bold** *soft* ~~gone~~"));
            Assert.Equal("bold", body.QuerySelector("strong")?.TextContent);
            Assert.Equal("soft", body.QuerySelector("em")?.TextContent);
            Assert.Equal("gone", body.QuerySelector("del")?.TextContent);
        }

        [Fact]
        public void A_pipe_table_survives_WITH_its_alignment()
        {
            var html = _renderer.Render("| Left | Right |\n|:-----|------:|\n| a | b |");
            _output.WriteLine(html);
            var body = Parse(html);

            Assert.NotNull(body.QuerySelector("table"));
            Assert.Equal(2, body.QuerySelectorAll("th").Length);
            var right = body.QuerySelectorAll("td")[1];
            Assert.Equal("b", right.TextContent);
            Assert.Contains("right", right.GetAttribute("style") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_task_list_survives_and_its_boxes_cannot_be_operated()
        {
            var html = _renderer.Render("- [x] done\n- [ ] todo");
            _output.WriteLine(html);
            var boxes = Parse(html).QuerySelectorAll("input");

            Assert.Equal(2, boxes.Length);
            Assert.All(boxes, box =>
            {
                Assert.Equal("checkbox", box.GetAttribute("type"));
                Assert.True(box.HasAttribute("disabled"));
            });
            Assert.True(boxes[0].HasAttribute("checked"));
            Assert.False(boxes[1].HasAttribute("checked"));
        }

        [Fact]
        public void An_external_link_opens_apart_and_lends_nothing()
        {
            var link = Parse(_renderer.Render("[docs](https://example.com/docs)")).QuerySelector("a")!;
            Assert.Equal("https://example.com/docs", link.GetAttribute("href"));
            Assert.Equal(HelpHtmlSanitizer.ExternalRel, link.GetAttribute("rel"));
            Assert.Equal("_blank", link.GetAttribute("target"));
        }

        [Fact]
        public void A_bare_url_is_autolinked_as_an_external_link()
        {
            var link = Parse(_renderer.Render("See https://example.com for more.")).QuerySelector("a")!;
            Assert.Equal("https://example.com", link.GetAttribute("href"));
            Assert.Equal(HelpHtmlSanitizer.ExternalRel, link.GetAttribute("rel"));
        }

        [Theory]
        [InlineData("[users](/admin/users)", "/admin/users")]
        [InlineData("[section](#settings)", "#settings")]
        [InlineData("[sibling](roles)", "roles")]
        [InlineData("[write](mailto:help@example.com)", "mailto:help@example.com")]
        public void A_site_relative_or_mail_link_is_kept_and_does_NOT_open_apart(string markdown, string href)
        {
            var link = Parse(_renderer.Render(markdown)).QuerySelector("a")!;
            Assert.Equal(href, link.GetAttribute("href"));
            Assert.Null(link.GetAttribute("target"));
            Assert.Null(link.GetAttribute("rel"));
        }

        [Fact]
        public void Raw_HTML_in_a_topic_is_shown_as_TEXT_not_dropped()
        {
            // DisableHtml() escapes rather than deletes: an author who typed <b> sees what they typed.
            var body = Parse(_renderer.Render("Press <kbd>Ctrl</kbd> now."));
            Assert.Null(body.QuerySelector("kbd"));
            Assert.Contains("<kbd>Ctrl</kbd>", body.TextContent, StringComparison.Ordinal);
        }

        [Fact]
        public void A_body_over_the_domains_bound_is_REFUSED_not_rendered()
        {
            Assert.Throws<ArgumentException>(() => _renderer.Render(new string('x', HelpTopicContribution.MaximumMarkdownCharacters + 1)));
            Assert.NotEmpty(_renderer.Render(new string('x', HelpTopicContribution.MaximumMarkdownCharacters)));
            Assert.Equal(string.Empty, _renderer.Render(null));
        }

        // ---- the pipeline: DisableHtml() driven BOTH ways ------------------------------------------------------------

        [Fact]
        public void WITHOUT_DisableHtml_Markdig_passes_a_script_through_and_the_SANITISER_alone_stops_it()
        {
            var permissive = new MarkdownPipelineBuilder().UsePipeTables().Build();
            var raw = Markdown.ToHtml("<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>", permissive);
            _output.WriteLine("raw: " + raw);

            // The premise, measured: without DisableHtml the raw output IS live...
            Assert.NotEmpty(Parse(raw).QuerySelectorAll("script"));
            // ...and the allowlist neutralises it on its own.
            AssertInert(_sanitizer.Sanitize(raw));
        }

        [Fact]
        public void WITH_DisableHtml_the_real_pipelines_RAW_output_has_no_live_markup_before_the_sanitiser_runs()
        {
            // The other direction: the pipeline is not relying on the allowlist either.
            var raw = Markdown.ToHtml("<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n# H {onclick=\"alert(1)\"}", HelpMarkdownRenderer.CreatePipeline());
            _output.WriteLine("raw: " + raw);
            Assert.Empty(Parse(raw).QuerySelectorAll("script, img"));
            Assert.DoesNotContain(Parse(raw).QuerySelectorAll("*"), element => element.Attributes.Any(IsEventHandler));
        }

        [Fact]
        public void GenericAttributes_writes_an_event_handler_EVEN_WITH_DisableHtml_which_is_why_it_is_banned()
        {
            // D103's claim about UseAdvancedExtensions, measured rather than quoted.
            var advanced = new MarkdownPipelineBuilder().UseGenericAttributes().DisableHtml().Build();
            var raw = Markdown.ToHtml("# Heading {onclick=\"alert(1)\"}", advanced);
            _output.WriteLine("raw: " + raw);

            Assert.Equal("alert(1)", Parse(raw).QuerySelector("h1")?.GetAttribute("onclick"));
            AssertInert(_sanitizer.Sanitize(raw));
        }

        [Fact]
        public void The_real_pipeline_carries_NO_generic_attributes_extension()
        {
            var extensions = HelpMarkdownRenderer.CreatePipeline().Extensions.Select(extension => extension.GetType()).ToList();
            _output.WriteLine(string.Join(", ", extensions.Select(type => type.Name)));
            Assert.DoesNotContain(typeof(GenericAttributesExtension), extensions);

            // And the checker is not blind: the advanced pipeline DOES carry it.
            Assert.Contains(typeof(GenericAttributesExtension),
                new MarkdownPipelineBuilder().UseAdvancedExtensions().Build().Extensions.Select(extension => extension.GetType()));
        }

        // ---- the allowlist on HTML directly --------------------------------------------------------------------------

        [Theory]
        [InlineData("<a href=\"java&#9;script:alert(1)\">x</a>")]
        [InlineData("<a href=\"java\nscript:alert(1)\">x</a>")]
        [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
        [InlineData("<a href=\"&#x6A;avascript&colon;alert(1)\">x</a>")]
        [InlineData("<a href=\" \u0001javascript:alert(1)\">x</a>")]
        [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>")]
        [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>")]
        [InlineData("<img src=\"javascript:alert(1)\">")]
        [InlineData("<img src=x onerror=alert(1)>")]
        [InlineData("<div onclick=\"alert(1)\">x</div>")]
        [InlineData("<p><b onmouseover=\"alert(1)\">x</b></p>")]
        [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>")]
        [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>")]
        [InlineData("<svg><a xlink:href=\"javascript:alert(1)\"><text>x</text></a></svg>")]
        [InlineData("<form action=\"https://evil.example\"><input type=\"password\"></form>")]
        [InlineData("<input type=\"text\" autofocus onfocus=\"alert(1)\">")]
        [InlineData("<input type=\"checkbox\" checked>")]
        [InlineData("<table><tr><td style=\"position:fixed;top:0;left:0;width:100%;height:100%\">Sign in again</td></tr></table>")]
        [InlineData("<td style=\"background:url(javascript:alert(1))\">x</td>")]
        [InlineData("<table><tr><td style=\"text-align:left;background:url(javascript:alert(1))\">x</td></tr></table>")]
        [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">")]
        [InlineData("<base href=\"https://evil.example/\">")]
        [InlineData("<object data=\"x.swf\"></object><embed src=\"x.swf\">")]
        [InlineData("<a href=\"https://x\"><img src=\"x\" onload=\"alert(1)\"></a>")]
        [InlineData("<!-- <script>alert(1)</script> -->")]
        public void Dirty_html_is_reduced_to_nothing_that_runs(string html)
        {
            var clean = _sanitizer.Sanitize(html);
            _output.WriteLine($"{html}\n  -> {clean}");
            AssertInert(clean);
        }

        [Fact]
        public void An_unknown_element_loses_its_TAG_and_keeps_its_TEXT()
        {
            var body = Parse(_sanitizer.Sanitize("<div class=\"x\"><span>kept <b>bold</b></span></div>"));
            Assert.Null(body.QuerySelector("div, span"));
            Assert.Equal("kept bold", body.TextContent);
            Assert.NotNull(body.QuerySelector("b"));
        }

        [Fact]
        public void A_refused_link_keeps_its_TEXT()
        {
            var body = Parse(_sanitizer.Sanitize("<p>Go <a href=\"javascript:alert(1)\">here</a>.</p>"));
            Assert.Null(body.QuerySelector("a"));
            Assert.Equal("Go here.", body.TextContent);
        }

        [Fact]
        public void A_script_and_a_style_lose_their_CONTENT_too()
        {
            // Unwrapping would put the code on the page as text; these go whole.
            var body = Parse(_sanitizer.Sanitize("<p>a</p><script>var secret = 1;</script><style>.x{}</style><p>b</p>"));
            Assert.Equal("ab", body.TextContent);
        }

        [Fact]
        public void An_authors_rel_and_target_are_REPLACED_not_merged()
        {
            var link = Parse(_sanitizer.Sanitize("<a href=\"https://x.example\" target=\"_self\" rel=\"opener\">x</a>")).QuerySelector("a")!;
            Assert.Equal(HelpHtmlSanitizer.ExternalRel, link.GetAttribute("rel"));
            Assert.Equal("_blank", link.GetAttribute("target"));

            // ...and a relative link carries none, even if the markup asked for one.
            var local = Parse(_sanitizer.Sanitize("<a href=\"/x\" target=\"_top\">x</a>")).QuerySelector("a")!;
            Assert.Null(local.GetAttribute("target"));
        }

        [Theory]
        [InlineData("//evil.example/x")]
        [InlineData("\\\\evil.example/x")]
        [InlineData("/\\evil.example/x")]
        [InlineData("HTTPS://example.com")]
        public void A_protocol_relative_or_upper_case_url_counts_as_EXTERNAL(string href)
        {
            Assert.True(HelpHtmlSanitizer.TryClassifyUrl(href, allowMailto: true, out var external));
            Assert.True(external);
        }

        [Theory]
        [InlineData("mailto:x@example.com", true, true)]
        [InlineData("mailto:x@example.com", false, false)]     // an image is never mail
        [InlineData("ftp://example.com", true, false)]
        [InlineData("", true, false)]
        [InlineData("   ", true, false)]
        [InlineData("page?a=b:c", true, true)]                 // the colon is after a delimiter: no scheme
        [InlineData("a/b:c", true, true)]
        public void The_scheme_rule(string href, bool allowMailto, bool expected)
        {
            Assert.Equal(expected, HelpHtmlSanitizer.TryClassifyUrl(href, allowMailto, out _));
        }

        [Fact]
        public void Markdig_class_values_survive_and_anything_else_is_stripped()
        {
            var body = Parse(_sanitizer.Sanitize(
                "<ul class=\"contains-task-list\"><li class=\"task-list-item\">x</li></ul>" +
                "<pre><code class=\"language-csharp\">x</code></pre>" +
                "<p class=\"language-x\">p</p><ul class=\"contains-task-list evil\"><li>y</li></ul>"));

            Assert.Equal("contains-task-list", body.QuerySelector("ul")!.GetAttribute("class"));
            Assert.Equal("task-list-item", body.QuerySelector("li")!.GetAttribute("class"));
            Assert.Equal("language-csharp", body.QuerySelector("code")!.GetAttribute("class"));
            Assert.Null(body.QuerySelector("p")!.GetAttribute("class"));          // <p> takes no class at all
            Assert.Null(body.QuerySelectorAll("ul")[1].GetAttribute("class"));    // one foreign token costs the attribute
        }

        [Fact]
        public void Nothing_to_sanitise_is_an_empty_string()
        {
            Assert.Equal(string.Empty, _sanitizer.Sanitize(null));
            Assert.Equal(string.Empty, _sanitizer.Sanitize(string.Empty));
        }

        // ---- the oracle --------------------------------------------------------------------------------------------

        [Fact]
        public void The_inertness_check_itself_FAILS_on_live_markup()
        {
            // Validated the other way: an oracle that cannot go red proves nothing.
            foreach (var live in new[]
                     {
                         "<script>x</script>", "<b onclick=\"x\">y</b>", "<a href=\"javascript:x\">y</a>", "<img src=\"data:x\">",
                         "<iframe></iframe>", "<a href=\"java\tscript:x\">y</a>", "<input type=\"checkbox\">",
                         "<table><tr><td style=\"position:fixed\">x</td></tr></table>",
                     })
            {
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertInert(live));
            }
        }

        /// <summary>What a browser would build from <paramref name="html"/> contains nothing that runs or navigates somewhere unsafe.</summary>
        private static void AssertInert(string html)
        {
            var body = Parse(html);
            var elements = body.QuerySelectorAll("*");

            Assert.Empty(body.QuerySelectorAll("script, iframe, frame, object, embed, style, svg, math, form, meta, base, link, noscript"));
            foreach (var element in elements)
            {
                Assert.DoesNotContain(element.Attributes, IsEventHandler);
                foreach (var attribute in element.Attributes.Where(item => item.Name is "href" or "src" or "xlink:href" or "action" or "formaction"))
                {
                    Assert.True(HelpHtmlSanitizer.TryClassifyUrl(attribute.Value, allowMailto: attribute.Name == "href", out _),
                        $"<{element.LocalName} {attribute.Name}=\"{attribute.Value}\"> survived");
                }

                if (element.LocalName == "input")
                {
                    Assert.Equal("checkbox", element.GetAttribute("type"));
                    Assert.True(element.HasAttribute("disabled"), "an input a reader can operate survived");
                }

                // CSS runs nothing, but it can DRAW: a fixed-position cell over the whole page is a phishing surface.
                // The only style that may survive is the one Markdig writes for pipe-table alignment.
                if (element.GetAttribute("style") is { } style)
                {
                    Assert.Matches(@"^\s*text-align:\s*(left|right|center)\s*;?\s*$", style);
                }
            }
        }

        private static bool IsEventHandler(IAttr attribute) => attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase);

        private static IElement Parse(string html)
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
            document.Body!.InnerHtml = html;
            return document.Body;
        }
    }
}
