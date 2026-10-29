#nullable enable
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// CSV text to records (a step over TEXT: the file read is
    /// <see cref="ReadTextFile"/>'s job, the HTTP fetch is <see cref="SendHttpRequest"/>'s, composition over a
    /// monolith). The header row names the fields; without one they are <c>column1</c>, <c>column2</c>...; a record
    /// shorter than the header leaves the rest empty, a longer one keeps its extra values under generated names.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Parse CSV text into records (a JSON array of objects).", DisplayName = "Parse CSV")]
    public class ParseCsv : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The CSV text. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Text { get; set; }

        [Input(Description = "The field delimiter; one character.", DefaultValue = ",", Order = 2)]
        public Input<string> Delimiter { get; set; } = new(new Literal<string>(","));

        [Input(DisplayName = "Has header", Description = "Whether the first record names the fields.", DefaultValue = true, Order = 3)]
        public Input<bool> HasHeader { get; set; } = new(new Literal<bool>(true));

        [Input(DisplayName = "Skip lines", Description = "Records to skip before the header (a title line, a comment).", DefaultValue = 0, Order = 4)]
        public Input<int> SkipLines { get; set; } = new(new Literal<int>(0));

        [Input(DisplayName = "Max rows", Description = "The most records to take.", DefaultValue = 100000, Order = 5)]
        public Input<int> MaxRows { get; set; } = new(new Literal<int>(100_000));

        [Output(DisplayName = "Row count", Description = "How many records were parsed.")]
        public Output<int>? RowCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var text = TextOrLastResult(context, Text) ?? "";
            var delimiter = Delimiter.GetOrDefault(context);
            if (string.IsNullOrEmpty(delimiter) || delimiter.Length != 1)
            {
                await FailAsync(context, "CSV_DELIMITER_INVALID", "The delimiter must be exactly one character.").ConfigureAwait(false);
                return;
            }

            var skip = SkipLines.GetOrDefault(context);
            var max = MaxRows.GetOrDefault(context);
            if (skip < 0 || max <= 0)
            {
                await FailAsync(context, "CSV_PARAMETER_INVALID", "Skip lines must be zero or more and max rows more than zero.").ConfigureAwait(false);
                return;
            }

            var records = Csv.Parse(text, delimiter[0]);
            var start = skip;
            var hasHeader = HasHeader.GetOrDefault(context);
            var header = hasHeader && start < records.Count ? records[start] : null;
            if (hasHeader)
            {
                start++;
            }

            var rows = new List<JsonObject>();
            for (var i = start; i < records.Count && rows.Count < max; i++)
            {
                var record = records[i];
                var row = new JsonObject();
                for (var f = 0; f < record.Count; f++)
                {
                    var name = header is not null && f < header.Count && header[f].Length > 0 ? header[f] : "column" + (f + 1).ToString(CultureInfo.InvariantCulture);
                    row[name] = record[f];
                }

                if (header is not null)
                {
                    for (var f = record.Count; f < header.Count; f++)
                    {
                        row[header[f].Length > 0 ? header[f] : "column" + (f + 1).ToString(CultureInfo.InvariantCulture)] = "";
                    }
                }

                rows.Add(row);
            }

            context.Set(RowCount, rows.Count);
            await SucceedAsync(context, Records.ToJson(rows)).ConfigureAwait(false);
        }
    }
}
