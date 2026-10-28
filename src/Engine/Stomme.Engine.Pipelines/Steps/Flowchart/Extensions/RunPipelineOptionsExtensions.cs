using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

/// <summary>
/// Extension methods for <see cref="RunPipelineOptions"/> to configure flowchart execution mode.
/// </summary>
public static class RunPipelineOptionsExtensions
{
    extension(RunPipelineOptions options)
    {
        /// <summary>
        /// Sets the flowchart execution mode to token-based.
        /// </summary>
        public RunPipelineOptions WithTokenBasedFlowchart()
        {
            return options.WithFlowchartExecutionMode(FlowchartExecutionMode.TokenBased);
        }

        /// <summary>
        /// Sets the flowchart execution mode to counter-based (legacy mode).
        /// </summary>
        public RunPipelineOptions WithCounterBasedFlowchart()
        {
            return options.WithFlowchartExecutionMode(FlowchartExecutionMode.CounterBased);
        }

        /// <summary>
        /// Sets the flowchart execution mode.
        /// </summary>
        public RunPipelineOptions WithFlowchartExecutionMode(FlowchartExecutionMode mode)
        {
            options.Properties ??= new Dictionary<string, object>();
            options.Properties[Steps.Flowchart.ExecutionModePropertyKey] = mode;
            return options;
        }
    }
}
