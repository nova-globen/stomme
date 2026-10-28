namespace Stomme.Engine.Pipelines.Management.Options;

/// <summary>
/// Options for configuring the pipeline reference graph builder behavior.
/// </summary>
public class PipelineReferenceGraphOptions
{
    /// <summary>
    /// The maximum depth to traverse when building the reference graph.
    /// A value of 0 means no limit.
    /// Default is 100.
    /// </summary>
    public int MaxDepth { get; set; } = 100;

    /// <summary>
    /// The maximum number of pipeline definitions to include in the graph.
    /// A value of 0 means no limit.
    /// Default is 1000.
    /// </summary>
    public int MaxDefinitions { get; set; } = 1000;
}

