using System.Diagnostics.CodeAnalysis;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Enums;
using Stomme.Engine.Pipelines.Management.Models;
using System.Linq.Dynamic.Core;

namespace Stomme.Engine.Pipelines.Management.Filters;

/// <summary>
/// A filter for querying pipeline instances.
/// </summary>
public class PipelineInstanceFilter
{
    /// <summary>
    /// Filter pipeline instances by ID.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Filter pipeline instances by IDs.
    /// </summary>
    public ICollection<string>? Ids { get; set; }

    /// <summary>
    /// Filter pipeline instances that match the specified search term.
    /// </summary>
    public string? SearchTerm { get; set; }
    
    /// <summary>
    /// Filter pipeline instances that match the specified name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Filter pipeline instances by definition ID.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>
    /// Filter pipeline instances by definition version ID.
    /// </summary>
    public string? DefinitionVersionId { get; set; }

    /// <summary>
    /// Filter pipeline instances by definition IDs.
    /// </summary>
    public ICollection<string>? DefinitionIds { get; set; }

    /// <summary>
    /// Filter pipeline instances by definition version IDs.
    /// </summary>
    public ICollection<string>? DefinitionVersionIds { get; set; }

    /// <summary>
    /// Filter pipeline instances by version.
    /// </summary>
    public int? Version { get; set; }
    
    /// <summary>
    /// Filter pipeline instances by their parent instance IDs.
    /// </summary>
    public ICollection<string>? ParentPipelineInstanceIds { get; set; }

    /// <summary>
    /// Filter pipeline instances by correlation ID.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Filter pipeline instances by correlation IDs.
    /// </summary>
    public ICollection<string>? CorrelationIds { get; set; }

    /// <summary>
    /// Filter pipeline instances by status.
    /// </summary>
    public PipelineStatus? PipelineStatus { get; set; }

    /// <summary>
    /// Filter pipeline instances by a set of statuses.
    /// </summary>
    public ICollection<PipelineStatus>? PipelineStatuses { get; set; }

    /// <summary>
    /// Filter pipeline instances by sub-status.
    /// </summary>
    public PipelineSubStatus? PipelineSubStatus { get; set; }

    /// <summary>
    /// Filter pipeline instances by a set of sub-status.
    /// </summary>
    public ICollection<PipelineSubStatus>? PipelineSubStatuses { get; set; }

    /// <summary>
    /// Filter pipeline instances by whether they are executing.
    /// </summary>
    public bool? IsExecuting { get; set; }

    /// <summary>
    /// Filter pipeline instances by whether they have incidents.
    /// </summary>
    public bool? HasIncidents { get; set; }
    
    /// <summary>
    /// Filter on pipelines that are system pipelines.
    /// </summary>
    public bool? IsSystem { get; set; }

    /// <summary>
    /// Filter pipeline instances that are older than the specified timestamp.
    /// </summary>
    public DateTimeOffset? BeforeLastUpdated { get; set; }

    /// <summary>
    /// Filter pipeline instances by timestamp.
    /// </summary>
    public ICollection<TimestampFilter>? TimestampFilters { get; set; }

    /// <summary>
    /// Filter pipeline instances by name.
    /// </summary>
    public List<string>? Names { get; set; }

    /// <summary>
    /// Applies the filter to the specified query.
    /// </summary>
    [RequiresUnreferencedCode("The method uses reflection to create an expression tree.")]
    public IQueryable<PipelineInstance> Apply(IQueryable<PipelineInstance> query)
    {
        var filter = this;
        
        if (!string.IsNullOrWhiteSpace(filter.Id)) query = query.Where(x => x.Id == filter.Id);
        if (filter.Ids != null) query = query.Where(x => filter.Ids.Contains(x.Id));
        if (!string.IsNullOrWhiteSpace(filter.DefinitionId)) query = query.Where(x => x.DefinitionId == filter.DefinitionId);
        if (!string.IsNullOrWhiteSpace(filter.DefinitionVersionId)) query = query.Where(x => x.DefinitionVersionId == filter.DefinitionVersionId);
        if (filter.DefinitionIds != null) query = query.Where(x => filter.DefinitionIds.Contains(x.DefinitionId));
        if (filter.DefinitionVersionIds != null) query = query.Where(x => filter.DefinitionVersionIds.Contains(x.DefinitionVersionId));
        if (filter.Version != null) query = query.Where(x => x.Version == filter.Version);
        if (filter.ParentPipelineInstanceIds != null) query = query.Where(x => x.ParentPipelineInstanceId != null && filter.ParentPipelineInstanceIds.Contains(x.ParentPipelineInstanceId));
        if (!string.IsNullOrWhiteSpace(filter.CorrelationId)) query = query.Where(x => x.CorrelationId == filter.CorrelationId);
        if (filter.CorrelationIds != null) query = query.Where(x => filter.CorrelationIds.Contains(x.CorrelationId!));
        if (filter.Names != null) query = query.Where(x => filter.Names.Contains(x.Name!));
        if (filter.PipelineStatus != null) query = query.Where(x => x.Status == filter.PipelineStatus);
        if (filter.PipelineSubStatus != null) query = query.Where(x => x.SubStatus == filter.PipelineSubStatus);
        if (filter.PipelineStatuses != null) query = query.Where(x => filter.PipelineStatuses.Contains(x.Status));
        if (filter.PipelineSubStatuses != null) query = query.Where(x => filter.PipelineSubStatuses.Contains(x.SubStatus));
        if (filter.IsExecuting != null) query = query.Where(x => x.IsExecuting == filter.IsExecuting);
        if (filter.HasIncidents != null) query = filter.HasIncidents == true ? query.Where(x => x.IncidentCount > 0) : query.Where(x => x.IncidentCount == 0);
        if (filter.IsSystem != null) query = query.Where(x => x.IsSystem == filter.IsSystem);
        if (filter.Name != null) query = query.Where(x => x.Name!.ToLower().Contains(filter.Name.ToLower()));
        if (filter.BeforeLastUpdated != null) query = query.Where(x => x.UpdatedAt < filter.BeforeLastUpdated);

        if (TimestampFilters != null)
        {
            foreach (var timestampFilter in TimestampFilters)
            {
                var column = timestampFilter.Column;
                var timestamp = timestampFilter.Timestamp;
                var isZeroTime = timestamp.TimeOfDay == TimeSpan.Zero;
                var startDay = new DateTimeOffset(timestamp.Date);
                var endDay = startDay.AddDays(1);

                query = timestampFilter.Operator switch
                {
                    TimestampFilterOperator.Is when isZeroTime => query.Where($"{column} >= @0 && {column} < @1", startDay, endDay),
                    TimestampFilterOperator.Is => query.Where($"{column} == @0", timestamp),
                    TimestampFilterOperator.IsNot when isZeroTime => query.Where($"{column} < @0 || {column} >= @1", startDay, endDay),
                    TimestampFilterOperator.IsNot => query.Where($"{column} != @0", timestamp),
                    TimestampFilterOperator.GreaterThan when isZeroTime => query.Where($"{column} > @0", endDay),
                    TimestampFilterOperator.GreaterThan => query.Where($"{column} > @0", timestamp),
                    TimestampFilterOperator.GreaterThanOrEqual when isZeroTime => query.Where($"{column} >= @0", startDay),
                    TimestampFilterOperator.GreaterThanOrEqual => query.Where($"{column} >= @0", timestamp),
                    TimestampFilterOperator.LessThan when isZeroTime => query.Where($"{column} < @0", startDay),
                    TimestampFilterOperator.LessThan => query.Where($"{column} < @0", timestamp),
                    TimestampFilterOperator.LessThanOrEqual when isZeroTime => query.Where($"{column} <= @0", endDay),
                    TimestampFilterOperator.LessThanOrEqual => query.Where($"{column} <= @0", timestamp),
                    _ => query
                };
            }
        }

        var searchTerm = filter.SearchTerm;
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query =
                from instance in query
                where instance.Name!.ToLower().Contains(searchTerm.ToLower())
                      || instance.DefinitionVersionId.Contains(searchTerm)
                      || instance.DefinitionId.Contains(searchTerm)
                      || instance.Id.Contains(searchTerm)
                      || instance.CorrelationId!.Contains(searchTerm)
                select instance;
        }

        return query;
    }
}