#nullable enable
using System;
using Stomme.Help;
using Markdig;

namespace Stomme.Modules.ContextualHelp.Infrastructure
{
    /// <summary>
    /// Turns a help topic's Markdown into the <c>SafeHtml</c> a reader receives (BL-84, D103 item 5): Markdig, then the
    /// allowlist. Server-side only, at READ - HTML is never stored, and no Markdig type is referenced from the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pipeline is named extension by extension, and <c>UseAdvancedExtensions()</c> is deliberately absent.</b>
    /// Markdig's own XML documentation says it includes <c>UseGenericAttributes</c>, which lets a topic write
    /// <c># Heading {onclick="..."}</c> - and that survives <c>DisableHtml()</c>, because it is not HTML parsing. So the
    /// set is: pipe tables, autolinks, task lists, the emphasis extras, soft line breaks as hard ones, and HTML OFF.
    /// </para>
    /// <para>
    /// <b>And the sanitiser runs anyway.</b> A pipeline is one line someone can change; the allowlist is the property the
    /// reader relies on. Nothing on this type returns Markdig's output without it.
    /// </para>
    /// </remarks>
    public sealed class HelpMarkdownRenderer
    {
        private readonly MarkdownPipeline _pipeline;
        private readonly HelpHtmlSanitizer _sanitizer;

        public HelpMarkdownRenderer(HelpHtmlSanitizer sanitizer)
        {
            _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
            _pipeline = CreatePipeline();
        }

        /// <summary>The pipeline, exactly - exposed so the suite can assert what it does and does not contain.</summary>
        public static MarkdownPipeline CreatePipeline() =>
            new MarkdownPipelineBuilder()
                .UsePipeTables()
                .UseAutoLinks()
                .UseTaskLists()
                .UseEmphasisExtras()
                .UseSoftlineBreakAsHardlineBreak()
                .DisableHtml()
                .Build();

        /// <summary>
        /// Renders and sanitises. A body over the domain's bound is refused rather than rendered: every stored topic is
        /// within it, so an over-long input can only be an unsaved preview, and the caller answers it as bad input.
        /// </summary>
        public string Render(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return string.Empty;
            }

            if (markdown.Length > HelpTopicContribution.MaximumMarkdownCharacters)
            {
                throw new ArgumentException($"A help topic body is at most {HelpTopicContribution.MaximumMarkdownCharacters} characters.", nameof(markdown));
            }

            return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));
        }
    }
}
