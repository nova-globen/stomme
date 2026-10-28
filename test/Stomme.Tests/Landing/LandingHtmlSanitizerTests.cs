#nullable enable
using System;
using System.Linq;
using Stomme.Modules.Landing.Infrastructure;
using Xunit;

namespace Stomme.Tests.Landing
{
    /// <summary>
    /// BL-98 increment 3 - the allowlist a generated landing page passes through (<see cref="LandingHtmlSanitizer"/>): a page
    /// every visitor sees is held to structure, text, links, images and a narrow inline style - no script in any spelling, no
    /// handler, no frame, no form, no second parser, no style that fetches or positions over the site - and each section keeps
    /// its name once. What was taken out is reported, for the agent to put right.
    /// </summary>
    public sealed class LandingHtmlSanitizerTests
    {
        private readonly LandingHtmlSanitizer _sanitizer = new();

        [Fact]
        public void A_well_formed_page_passes_unchanged_but_for_its_links_targets()
        {
            const string html = "<section data-section=\"hero\" style=\"padding: 2rem; background-color: #123456\"><h1>Welcome</h1><p>Hello <strong>there</strong>.</p><a href=\"/about\">About</a></section>";

            var result = _sanitizer.Sanitize(html);

            Assert.Empty(result.Removed);
            Assert.Equal(new[] { "hero" }, result.Sections);
            Assert.Contains("<h1>Welcome</h1>", result.Html);
            Assert.Contains("style=\"padding: 2rem; background-color: #123456\"", result.Html);
            Assert.Contains("<a href=\"/about\" target=\"_top\">About</a>", result.Html);
        }

        [Theory]
        [InlineData("<script>alert(1)</script>", "<script>")]
        [InlineData("<img src=x onerror=alert(1)>", "onerror")]
        [InlineData("<div onclick=\"alert(1)\">x</div>", "onclick")]
        [InlineData("<iframe src=\"https://evil.test\"></iframe>", "<iframe>")]
        [InlineData("<form action=\"https://evil.test\"><input name=p></form>", "<form>")]
        [InlineData("<svg><script>alert(1)</script></svg>", "<svg>")]
        [InlineData("<style>body{display:none}</style>", "<style>")]
        [InlineData("<a href=\"javascript:alert(1)\">x</a>", "a link to")]
        [InlineData("<a href=\"java&#9;script:alert(1)\">x</a>", "a link to")]
        [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "a link to")]
        [InlineData("<img src=\"javascript:alert(1)\">", "an image from")]
        [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">", "<meta>")]
        [InlineData("<object data=\"x.swf\"></object>", "<object>")]
        public void Anything_that_runs_fetches_a_frame_or_submits_is_taken_out_and_reported(string html, string reported)
        {
            var result = _sanitizer.Sanitize("<section data-section=\"s\">" + html + "</section>");

            Assert.Contains(result.Removed, removed => removed.Contains(reported, StringComparison.Ordinal));
            Assert.DoesNotContain("script", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onerror", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<iframe", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<form", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<meta", result.Html, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("background: url(https://tracker.test/p.gif)")]
        [InlineData("background-color: red; background-image: url(x)")]
        [InlineData("width: expression(alert(1))")]
        [InlineData("position: fixed; top: 0")]
        [InlineData("color: red\\;")]
        [InlineData("behavior: url(x.htc)")]
        public void A_style_that_fetches_runs_or_positions_is_cut_to_what_is_safe(string style)
        {
            var result = _sanitizer.Sanitize($"<section data-section=\"s\" style=\"{style}\">x</section>");

            Assert.NotEmpty(result.Removed);
            Assert.DoesNotContain("url(", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expression", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("position", result.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("behavior", result.Html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_external_link_opens_apart_and_an_https_image_stays()
        {
            var result = _sanitizer.Sanitize("<section data-section=\"s\"><a href=\"https://example.com/x\">x</a><img src=\"https://example.com/logo.png\" alt=\"Logo\"></section>");

            Assert.Empty(result.Removed);
            Assert.Contains("target=\"_blank\"", result.Html);
            Assert.Contains("rel=\"noopener noreferrer nofollow\"", result.Html);
            Assert.Contains("src=\"https://example.com/logo.png\"", result.Html);
        }

        [Fact]
        public void A_section_name_is_a_lowercase_token_used_once()
        {
            var result = _sanitizer.Sanitize("<section data-section=\"hero\">a</section><section data-section=\"hero\">b</section><section data-section=\"Bad Name\">c</section><section data-section=\"contact\">d</section>");

            Assert.Equal(new[] { "hero", "contact" }, result.Sections);
            Assert.Equal(2, result.Removed.Count(removed => removed.StartsWith("a section name", StringComparison.Ordinal)));
        }

        [Fact]
        public void An_unknown_element_keeps_its_text_and_a_comment_is_dropped()
        {
            var result = _sanitizer.Sanitize("<section data-section=\"s\"><custom-card>Card text</custom-card><!-- hidden --></section>");

            Assert.Contains("Card text", result.Html);
            Assert.DoesNotContain("custom-card", result.Html);
            Assert.DoesNotContain("hidden", result.Html);
        }

        [Fact]
        public void A_page_past_the_size_bound_is_refused_whole()
        {
            var result = _sanitizer.Sanitize("<section data-section=\"s\">" + new string('x', LandingHtmlSanitizer.MaxLength) + "</section>");

            Assert.Equal(string.Empty, result.Html);
            Assert.Single(result.Removed);
        }

        [Fact]
        public void The_frame_document_carries_a_policy_that_runs_nothing_and_calls_nowhere()
        {
            var document = Stomme.Landing.LandingFrame.Srcdoc("<section data-section=\"s\">x</section>");

            Assert.Contains("default-src 'none'", document);
            Assert.DoesNotContain("script-src", document);
            Assert.DoesNotContain("connect-src", document);
            Assert.Contains("<section data-section=\"s\">x</section>", document);
        }
    }
}
