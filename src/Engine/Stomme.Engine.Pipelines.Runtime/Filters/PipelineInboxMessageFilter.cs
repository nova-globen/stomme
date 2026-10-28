using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// A filter that can be used to find messages in the pipeline inbox.
/// </summary>
public class PipelineInboxMessageFilter
{
    /// <summary>
    /// The step type name to filter by.
    /// </summary>
    public string? StepTypeName { get; set; }

    /// <summary>
    /// The hash of the bookmark payload to filter by.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// The ID of the pipeline instance to filter by.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// The correlation ID of the pipeline instance to filter by.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The ID of the step instance to filter by.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// A flag indicating whether to filter by messages that have expired.
    /// </summary>
    public bool? IsExpired { get; set; }

    /// <summary>
    /// Applies the filter to the specified query.
    /// </summary>
    /// <returns>The filtered query.</returns>
    public IQueryable<PipelineInboxMessage> Apply(IQueryable<PipelineInboxMessage> query, DateTimeOffset now)
    {
        var filter = this;
        if (filter.CorrelationId != null) query = query.Where(x => filter.CorrelationId == x.CorrelationId);
        if (filter.PipelineInstanceId != null) query = query.Where(x => filter.PipelineInstanceId == x.PipelineInstanceId);
        if (filter.Hash != null) query = query.Where(x => filter.Hash == x.Hash);
        if (filter.StepTypeName != null) query = query.Where(x => filter.StepTypeName == x.StepTypeName);
        if (filter.StepInstanceId != null) query = query.Where(x => filter.StepInstanceId == x.StepInstanceId);
        if (filter.IsExpired != null) query = query.Where(x => x.ExpiresAt <= now);

        return query;
    }
}