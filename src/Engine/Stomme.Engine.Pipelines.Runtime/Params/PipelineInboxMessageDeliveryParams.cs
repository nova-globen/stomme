namespace Stomme.Engine.Pipelines.Runtime.Params;

/// <summary>
/// Options for delivering a pipeline inbox message.
/// </summary>
[Obsolete("This type is obsolete. Use the new IBookmarkQueue service instead.")]
public class PipelineInboxMessageDeliveryParams
{
    /// <summary>
    /// Whether to dispatch the message to the pipeline dispatcher or send immediately.
    /// </summary>
    public bool DispatchAsynchronously { get; set; } = true;
}