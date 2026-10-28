#nullable enable
using System.Collections.Generic;
using System.Linq;
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
    /// Records to CSV text (the predecessor platform's <c>write-csv</c> file operation split from the file write: this step renders,
    /// <see cref="WriteTextFile"/> stores). The columns are the record keys in order of first appearance unless named.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Render records (a JSON array of objects) as CSV text.", DisplayName = "Records to CSV")]
    public class ToCsv : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The records. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Records { get; set; }

        [Input(Description = "The columns to write, comma-separated and in order. Empty: every key, in the order it first appears.", Order = 2)]
        public Input<string?>? Columns { get; set; }

        [Input(Description = "The field delimiter; one character.", DefaultValue = ",", Order = 3)]
        public Input<string> Delimiter { get; set; } = new(new Literal<string>(","));

        [Output(DisplayName = "Row count", Description = "How many records were written.")]
        public Output<int>? RowCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            if (!Steps.Records.TryParse(TextOrLastResult(context, Records), out var rows, out var error))
            {
                await FailAsync(context, "RECORDS_INVALID", error!).ConfigureAwait(false);
                return;
            }

            var delimiter = Delimiter.GetOrDefault(context);
            if (string.IsNullOrEmpty(delimiter) || delimiter.Length != 1)
            {
                await FailAsync(context, "CSV_DELIMITER_INVALID", "The delimiter must be exactly one character.").ConfigureAwait(false);
                return;
            }

            var named = (Columns?.GetOrDefault(context) ?? "").Split(',').Select(column => column.Trim()).Where(column => column.Length > 0).ToList();
            var columns = named.Count > 0 ? named : Steps.Records.Columns(rows);
            var table = rows.Select(row => (IReadOnlyDictionary<string, string?>)columns.ToDictionary(column => column, column => Steps.Records.Text(row[column])));
            context.Set(RowCount, rows.Count);
            await SucceedAsync(context, Csv.Write(columns, table, delimiter[0])).ConfigureAwait(false);
        }
    }
}
