#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// the predecessor platform's rules-based web extraction as an engine step over HTML TEXT (the fetch is <see cref="SendHttpRequest"/>'s,
    /// with its egress guard): a row selector names the elements that become records (none: the document is one record),
    /// and each field line is <c>name = css selector</c> (the element's text), <c>name = css selector @attribute</c>
    /// (an attribute), or <c>name = regex: pattern</c> (the first group, or the match, over the row's HTML). The parser is
    /// AngleSharp, already a dependency; a selector that does not parse is a failure that names the field. the predecessor platform's
    /// crawling, robots policy, API discovery and AI extraction are not carried.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Extract records from HTML by CSS selector or pattern.", DisplayName = "Extract from HTML")]
    public class ExtractHtml : DataStep
    {
        private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

        [Input(UIHint = InputUIHints.MultiLine, Description = "The HTML. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Html { get; set; }

        [Input(DisplayName = "Row selector", Description = "A CSS selector for the elements that become records. Empty: the whole document is one record.", Order = 2)]
        public Input<string?>? RowSelector { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: name = selector | name = selector @attribute | name = regex: pattern.", Order = 3)]
        public Input<string?>? Fields { get; set; }

        [Output(DisplayName = "Row count")]
        public Output<int>? RowCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var html = TextOrLastResult(context, Html) ?? "";
            List<Field> fields;
            try
            {
                fields = Records.Lines(Fields?.GetOrDefault(context)).Select(Field.Parse).ToList();
            }
            catch (FormatException ex)
            {
                await FailAsync(context, "FIELD_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            if (fields.Count == 0)
            {
                await FailAsync(context, "FIELDS_REQUIRED", "The step needs at least one field line.").ConfigureAwait(false);
                return;
            }

            var document = new HtmlParser().ParseDocument(html);
            var rowSelector = RowSelector?.GetOrDefault(context);
            IEnumerable<IElement> rows;
            try
            {
                rows = string.IsNullOrWhiteSpace(rowSelector) ? new[] { document.DocumentElement } : document.QuerySelectorAll(rowSelector);
            }
            catch (DomException ex)
            {
                await FailAsync(context, "SELECTOR_INVALID", $"The row selector '{rowSelector}' is invalid: {ex.Message}").ConfigureAwait(false);
                return;
            }

            var records = new List<JsonObject>();
            foreach (var row in rows)
            {
                var record = new JsonObject();
                foreach (var field in fields)
                {
                    try
                    {
                        record[field.Name] = field.Read(row);
                    }
                    catch (DomException ex)
                    {
                        await FailAsync(context, "SELECTOR_INVALID", $"The selector for '{field.Name}' is invalid: {ex.Message}").ConfigureAwait(false);
                        return;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        await FailAsync(context, "PATTERN_TIMEOUT", $"The pattern for '{field.Name}' did not finish within its time budget.").ConfigureAwait(false);
                        return;
                    }
                }

                records.Add(record);
            }

            context.Set(RowCount, records.Count);
            await SucceedAsync(context, Records.ToJson(records)).ConfigureAwait(false);
        }

        private sealed record Field(string Name, string? Selector, string? Attribute, Regex? Pattern)
        {
            public static Field Parse(string line)
            {
                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    throw new FormatException($"Field '{line}' is not 'name = selector'.");
                }

                var name = line[..equals].Trim();
                var rest = line[(equals + 1)..].Trim();
                if (rest.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                {
                    var pattern = rest[6..].Trim();
                    try
                    {
                        return new Field(name, null, null, new Regex(pattern, RegexOptions.Singleline, RegexBudget));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new FormatException($"Field '{name}' has an invalid pattern: {ex.Message}");
                    }
                }

                var at = rest.LastIndexOf(" @", StringComparison.Ordinal);
                if (at > 0)
                {
                    return new Field(name, rest[..at].Trim(), rest[(at + 2)..].Trim(), null);
                }

                if (rest.Length == 0)
                {
                    throw new FormatException($"Field '{name}' has no selector.");
                }

                return new Field(name, rest, null, null);
            }

            public string? Read(IElement row)
            {
                if (Pattern is not null)
                {
                    var match = Pattern.Match(row.OuterHtml);
                    return match.Success ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).Trim() : null;
                }

                var element = row.QuerySelector(Selector!) ?? (row.Matches(Selector!) ? row : null);
                if (element is null)
                {
                    return null;
                }

                return Attribute is null ? element.TextContent.Trim() : element.GetAttribute(Attribute);
            }
        }
    }
}
