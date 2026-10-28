using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Options;

/// <summary>
/// Options for configuring flowchart execution behavior.
/// </summary>
public class FlowchartOptions
{
    /// <summary>
    /// Gets or sets the default execution mode for flowcharts when not explicitly specified.
    /// Defaults to <see cref="FlowchartExecutionMode.CounterBased"/>.
    /// </summary>
    public FlowchartExecutionMode DefaultExecutionMode { get; set; } = FlowchartExecutionMode.CounterBased; // Default to counter-based in order to maintain the same behavior with 3.5.2 out of the box.
}
