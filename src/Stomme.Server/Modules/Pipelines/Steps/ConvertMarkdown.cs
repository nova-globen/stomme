#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Modules.ContextualHelp.Infrastructure;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>Which way <see cref="ConvertMarkdown"/> turns its text.</summary>
    public enum MarkdownOperation
    {
        ToHtml,
        HtmlToText,
    }

    /// <summary>
    /// Markdown (BL-229, n8n's Markdown node): Markdown into HTML - the help module's pipeline (tables, autolinks, task lists,
    /// raw HTML off) and then its allowlist, so what comes out is safe to show or mail - or HTML into plain text (paragraphs,
    /// headings and rows on lines of their own, list items as "- ", scripts and styles dropped), for a message or an AI prompt.
    /// At most a million characters.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Turn Markdown into safe HTML, or HTML into plain text.", DisplayName = "Markdown")]
    public class ConvertMarkdown : DataStep
    {
        public const int MaxCharacters = 1_000_000;

        private static readonly string[] Blocks = ["p", "div", "section", "article", "header", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table", "ul", "ol", "pre", "blockquote"];

        [Input(Description = "ToHtml | HtmlToText.", DefaultValue = MarkdownOperation.ToHtml, Order = 1)]
        public Input<MarkdownOperation> Operation { get; set; } = new(new Literal<MarkdownOperation>(MarkdownOperation.ToHtml));

        [Input(UIHint = InputUIHints.MultiLine, Description = "The Markdown, or the HTML. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Value { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var text = TextOrLastResult(context, Value) ?? string.Empty;
            if (text.Length > MaxCharacters)
            {
                await FailAsync(context, "MARKDOWN_TOO_LONG", $"The value is over {MaxCharacters:N0} characters.").ConfigureAwait(false);
                return;
            }

            var result = Operation.GetOrDefault(context) == MarkdownOperation.HtmlToText
                ? ToText(text)
                : new HelpHtmlSanitizer().Sanitize(Markdig.Markdown.ToHtml(text, HelpMarkdownRenderer.CreatePipeline()));
            await SucceedAsync(context, result).ConfigureAwait(false);
        }

        /// <summary>HTML as plain text: blocks on lines of their own, list items as "- ", line breaks kept, the rest's spaces collapsed.</summary>
        public static string ToText(string html)
        {
            var document = new HtmlParser().ParseDocument(html);
            var text = new StringBuilder();
            Walk(document.Body ?? (INode)document, text);
            var lines = text.ToString().Split('\n').Select(line => Regex.Replace(line, @"[ \t\r\f\v]+", " ").Trim());
            return Regex.Replace(string.Join('\n', lines), @"\n{3,}", "\n\n").Trim();
        }

        private static void Walk(INode node, StringBuilder text)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText piece:
                        text.Append(piece.Data.Replace('\n', ' '));
                        break;
                    case IElement element:
                        var name = element.LocalName;
                        if (name is "script" or "style" or "template" or "head")
                        {
                            break;
                        }

                        if (name == "br")
                        {
                            text.Append('\n');
                            break;
                        }

                        var block = Blocks.Contains(name);
                        if (block || name == "li")
                        {
                            text.Append('\n');
                        }

                        if (name == "li")
                        {
                            text.Append("- ");
                        }
                        else if (name is "td" or "th")
                        {
                            text.Append(' ');
                        }

                        Walk(element, text);
                        if (block)
                        {
                            text.Append('\n');
                        }

                        break;
                }
            }
        }
    }
}
