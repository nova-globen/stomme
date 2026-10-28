#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// BL-229 - n8n's Execution Data node: saves key/value pairs about the run - "order: 42", "customer: Ada" - which its page and
    /// the runs list show and the runs search finds ("order: 42", or just "42"). Pairs saved again replace those keys; at most
    /// <see cref="PipelineRunData.MaxKeys"/> per run. The previous step's result passes through, so the step sits in a chain.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Control Flow", "Save key/value data about this run - shown on its page and in the runs list, and found by the runs search.", DisplayName = "Save run data")]
    public class SaveRunData : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "One 'key: value' per line - e.g. 'order: {{ order.id }}' - or a JSON object of values.", Order = 1)]
        public Input<string> Data { get; set; } = null!;

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var pairs = PipelineRunData.Parse(Required(context, Data, "Data"), out var error);
            if (pairs is null)
            {
                await FailAsync(context, "RUN_DATA_INVALID", error!).ConfigureAwait(false);
                return;
            }

            var keyValues = context.GetService<IKeyValueStore>()
                ?? throw new FaultException("RUN_DATA_UNAVAILABLE", "Pipelines", "Configuration", "The engine's key-value store is not composed in this host.");
            try
            {
                await PipelineRunDataStore.MergeAsync(keyValues, context.GetService<ITenantAccessor>()?.TenantId, context.PipelineExecutionContext.Id, pairs, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await FailAsync(context, "RUN_DATA_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            context.AddExecutionLogEntry("Saved", $"Saved {pairs.Count} piece(s) of run data.");
            await SucceedAsync(context, TextOrLastResult(context, null)).ConfigureAwait(false);
        }
    }
}
