namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Provides options to the pipeline dispatcher.
/// </summary>
public class PipelineDispatcherOptions
{
    /// <summary>
    /// A list of available channels to dispatch pipelines to.
    /// </summary>
    /// <remarks>
    /// Channels are used to dispatch pipelines to different queues or endpoints.
    /// </remarks>
    public ICollection<DispatcherChannel> Channels { get; set; } = new List<DispatcherChannel>();
}