using Stomme.Engine.Common.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// A message that can be delivered to a pipeline instance.
/// </summary>
public class PipelineInboxMessage : Entity
{
    /// <summary>
    /// The type name of the step to deliver the message to.
    /// </summary>
    public string StepTypeName { get; set; } = default!;

    /// <summary>
    /// An optional bookmark payload that can be used to filter the pipeline instances to deliver the message to.
    /// </summary>
    public object BookmarkPayload { get; set; } = default!;
    
    /// <summary>
    /// The hash of the bookmark.
    /// </summary>
    public string Hash { get; set; } = default!;

    /// <summary>
    /// An optional pipeline instance ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// An optional correlation ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// An optional step instance ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// An optional set of inputs to deliver to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }
    
    /// <summary>
    /// The date and time the message was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The date and time the message expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}