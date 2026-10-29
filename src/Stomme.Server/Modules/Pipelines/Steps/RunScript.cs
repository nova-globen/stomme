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
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A JavaScript script action as an engine step: the script sees <c>input</c> (the input text, or the
    /// previous step's result), a <c>log(...)</c> sink and the variables given as <c>name = value</c> lines; its
    /// completion value is the result (the log lines first, one per line, when any were written). Refused unless
    /// <c>Pipelines:Scripts:Enabled</c>; bounded by <see cref="JavaScriptSandbox"/>. Or written for n8n's Code node
    /// (<see cref="ScriptStyle"/>): its items, <c>$input</c> and <c>$json</c>, run once for all items or once for each,
    /// the items it returns the records (<see cref="N8nCode"/>) - its log in Log alone.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Scripting", "Run a JavaScript script in a bounded sandbox (Pipelines:Scripts:Enabled).", DisplayName = "Run script")]
    public class RunScript : DataStep
    {
        [Input(UIHint = InputUIHints.CodeEditor, Description = "The script. Its completion value is the result; log(x) appends a line.", Order = 1)]
        public Input<string> Script { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "The 'input' variable. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Input { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Extra variables, one 'name = value' per line.", Order = 3)]
        public Input<string?>? Variables { get; set; }

        [Input(DisplayName = "Written for", Description = "JavaScript, or n8n's Code node: N8nAllItems runs it once over the input's records as n8n's items ($input, $json, items), N8nEachItem once for each; the items it returns are the records.", DefaultValue = ScriptStyle.JavaScript, Order = 4)]
        public Input<ScriptStyle> Style { get; set; } = new(new Literal<ScriptStyle>(ScriptStyle.JavaScript));

        [Output(DisplayName = "Log", Description = "What the script logged, as a JSON array of lines.")]
        public Output<string>? Log { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var options = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Scripts ?? new ScriptStepOptions();
            if (!options.Enabled)
            {
                await FailAsync(context, "SCRIPTS_DISABLED", "Script execution is disabled (Pipelines:Scripts:Enabled=false).").ConfigureAwait(false);
                return;
            }

            var script = Required(context, Script, "Script");
            var variables = new Dictionary<string, string?> { ["input"] = TextOrLastResult(context, Input) };
            foreach (var line in await TemplatedLines.ReadAsync(Variables, context, '=').ConfigureAwait(false)) // a value from the run is one variable's
            {
                if (line.Name is null)
                {
                    await FailAsync(context, "VARIABLE_INVALID", $"'{line.Line}' is not 'name = value'.").ConfigureAwait(false);
                    return;
                }

                variables[line.Name] = line.Value;
            }

            var style = Style.GetOrDefault(context);
            var evaluation = JavaScriptSandbox.Evaluate(style == ScriptStyle.JavaScript ? script : N8nCode.Wrap(script, eachItem: style == ScriptStyle.N8nEachItem), variables, options);
            context.Set(Log, System.Text.Json.JsonSerializer.Serialize(evaluation.Log));
            if (!evaluation.Succeeded)
            {
                await FailAsync(context, "SCRIPT_ERROR", $"The script failed or exceeded its sandbox limits: {evaluation.Error}").ConfigureAwait(false);
                return;
            }

            if (style != ScriptStyle.JavaScript)
            {
                await SucceedAsync(context, evaluation.Value ?? "[]").ConfigureAwait(false); // the records alone; the log is in Log
                return;
            }

            var lines = evaluation.Log.ToList();
            if (evaluation.Value is not null)
            {
                lines.Add(evaluation.Value);
            }

            await SucceedAsync(context, string.Join("\n", lines)).ConfigureAwait(false);
        }
    }
}
