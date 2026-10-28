namespace Stomme.Engine.Pipelines.Steps.Flowchart.Models;

/// <summary>
/// Specifies the execution mode for flowchart steps.
/// </summary>
public enum FlowchartExecutionMode
{
    /// <summary>
    /// Use the default mode as specified by <see cref="Steps.Flowchart.UseTokenFlow"/>.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Use token-based flow logic.
    /// </summary>
    TokenBased = 1,

    /// <summary>
    /// Use counter-based flow logic (legacy mode).
    /// </summary>
    CounterBased = 2
}
