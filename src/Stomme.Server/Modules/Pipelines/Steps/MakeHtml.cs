#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What Make HTML makes.</summary>
    public enum HtmlMake
    {
        Table,
        Template,
    }

    /// <summary>
    /// Make HTML (n8n's HTML node - its Convert to HTML Table and Generate HTML Template; its Extract HTML Content is
    /// <c>ExtractHtml</c>): records as an HTML table - a header row of their fields, a row each, EVERY value encoded, so a record
    /// cannot write markup into the page it lands in - or a template the author writes, its expressions read, as n8n's is. A value
    /// a template puts in is put in as it is: the <c>| html</c> filter encodes it, <c>| raw</c> says it is HTML the author made, and the
    /// problems panel says so of a value put in with neither.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Make HTML: records as an HTML table, every value encoded - or a template you write, with the run's values in it.", DisplayName = "Make HTML")]
    public class MakeHtml : DataStep
    {
        public const string StepTypeName = "Stomme.MakeHtml";

        [Input(DisplayName = "Make", DefaultValue = HtmlMake.Table, Description = "Table: the records as an HTML table. Template: the HTML written below.", Order = 1)]
        public Input<HtmlMake> Make { get; set; } = new(new Literal<HtmlMake>(HtmlMake.Table));

        [Input(UIHint = InputUIHints.MultiLine, Description = "With Template: the HTML. Make it an expression to put a run's values in it - {{ order.customer | html }}: the html filter encodes a value; | raw puts in HTML you made, as it is.", Order = 2)]
        public Input<string?>? Html { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "With Table: the records. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Records { get; set; }

        [Input(Description = "With Table: the columns, comma-separated and in order. Empty: every field, in the order it first appears.", Order = 4)]
        public Input<string?>? Columns { get; set; }

        [Input(Description = "With Table: a caption above it.", Order = 5)]
        public Input<string?>? Caption { get; set; }

        [Input(DisplayName = "Capitalize headers", Description = "With Table: each header in words with capitals - orderId and order_id as Order Id.", Order = 6)]
        public Input<bool> CapitalizeHeaders { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "Row count", Description = "With Table: how many records it holds.")]
        public Output<int>? RowCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (Make.GetOrDefault(context) == HtmlMake.Template)
            {
                await SucceedAsync(context, Html?.GetOrDefault(context) ?? string.Empty).ConfigureAwait(false);
                return;
            }

            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var error))
            {
                await FailAsync(context, "RECORDS_INVALID", error!).ConfigureAwait(false);
                return;
            }

            var named = (Columns?.GetOrDefault(context) ?? string.Empty).Split(',').Select(column => column.Trim()).Where(column => column.Length > 0).ToList();
            var columns = named.Count > 0 ? named : Steps.Records.Columns(rows);
            context.Set(RowCount, rows.Count);
            await SucceedAsync(context, Table(columns, rows, Caption?.GetOrDefault(context), CapitalizeHeaders.GetOrDefault(context))).ConfigureAwait(false);
        }

        /// <summary>The records as an HTML table - every header and value encoded; a value that is an object or a list as its JSON.</summary>
        public static string Table(IReadOnlyList<string> columns, IReadOnlyList<JsonObject> rows, string? caption, bool capitalize)
        {
            var html = new StringBuilder("<table>\n");
            if (!string.IsNullOrWhiteSpace(caption))
            {
                html.Append("  <caption>").Append(WebUtility.HtmlEncode(caption.Trim())).Append("</caption>\n");
            }

            html.Append("  <thead>\n    <tr>");
            foreach (var column in columns)
            {
                html.Append("<th>").Append(WebUtility.HtmlEncode(capitalize ? Header(column) : column)).Append("</th>");
            }

            html.Append("</tr>\n  </thead>\n  <tbody>\n");
            foreach (var row in rows)
            {
                html.Append("    <tr>");
                foreach (var column in columns)
                {
                    html.Append("<td>").Append(WebUtility.HtmlEncode(row.TryGetPropertyValue(column, out var value) ? Steps.Records.Text(value) ?? string.Empty : string.Empty)).Append("</td>");
                }

                html.Append("</tr>\n");
            }

            return html.Append("  </tbody>\n</table>").ToString();
        }

        /// <summary>A field's name as a header: its words - split at capitals, underscores, hyphens and spaces - each with a capital.</summary>
        public static string Header(string name)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            for (var at = 0; at < name.Length; at++)
            {
                var letter = name[at];
                if (letter is '_' or '-' or ' ')
                {
                    Flush();
                    continue;
                }

                if (char.IsUpper(letter) && word.Length > 0 && !char.IsUpper(name[at - 1]))
                {
                    Flush();
                }

                word.Append(letter);
            }

            Flush();
            return string.Join(" ", words);

            void Flush()
            {
                if (word.Length > 0)
                {
                    words.Add(char.ToUpper(word[0], CultureInfo.InvariantCulture) + word.ToString(1, word.Length - 1));
                    word.Clear();
                }
            }
        }
    }
}
