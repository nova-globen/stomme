namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Represents the options for creating a pipeline host.
/// </summary>
public class PipelineHostOptions
{
    /// <summary>
    /// Gets or sets the ID of the pipeline instance to use when creating the pipeline host and its pipeline state.
    /// </summary>
    public string? NewPipelineInstanceId { get; set; }
}