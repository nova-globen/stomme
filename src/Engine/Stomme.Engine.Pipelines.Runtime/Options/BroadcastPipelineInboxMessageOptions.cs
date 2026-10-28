namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Represents the options for broadcasting a pipeline inbox message.
/// </summary>
public class BroadcastPipelineInboxMessageOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the dispatch should be executed asynchronously.
    /// </summary>
    /// <value>
    /// <c>true</c> if the dispatch should be executed asynchronously; otherwise, <c>false</c>.
    /// </value>
    public bool DispatchAsynchronously { get; set; } = true;
}