using Stomme.Engine.Common.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// Represents a bookmark that has been stored in the database.
/// </summary>
public class StoredBookmark : Entity
{
    /// <summary>
    /// The name of the step type associated with the bookmark.
    /// </summary>
    [Obsolete("Use Name instead.")] public string StepTypeName
    {
        get => Name!;
        set => Name = value;
    }

    /// <summary>
    /// The name of the bookmark, if any. Typically assigned a stimulus name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The hash of the bookmark.
    /// </summary>
    public string Hash { get; set; } = null!;

    /// <summary>
    /// The ID of the pipeline instance associated with the bookmark.
    /// </summary>
    public string PipelineInstanceId { get; set; } = null!;

    /// <summary>
    /// The ID of the step instance associated with the bookmark.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// The correlation ID of the pipeline instance associated with the bookmark.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The data associated with the bookmark.
    /// </summary>
    public object? Payload { get; set; }

    /// <summary>
    /// Custom properties associated with the bookmark.
    /// </summary>
    public IDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// The date and time the bookmark was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}