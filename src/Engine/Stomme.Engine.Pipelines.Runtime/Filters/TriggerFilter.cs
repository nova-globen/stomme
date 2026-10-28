using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// Represents a filter for triggers.
/// </summary>
public class TriggerFilter
{
    /// <summary>
    /// Gets or sets the ID of the trigger to find.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the triggers to find.
    /// </summary>
    public ICollection<string>? Ids { get; set; }

    /// <summary>
    /// Gets or sets the ID of the pipeline definition.
    /// </summary>
    public string? PipelineDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the ID of the pipeline definition version.
    /// </summary>
    public string? PipelineDefinitionVersionId { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the pipeline definitions.
    /// </summary>
    public ICollection<string>? PipelineDefinitionIds { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the pipeline definition versions.
    /// </summary>
    public ICollection<string>? PipelineDefinitionVersionIds { get; set; }

    /// <summary>
    /// Gets or sets the name of the trigger to find.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the names of the triggers to find.
    /// </summary>
    public ICollection<string>? Names { get; set; }

    /// <summary>
    /// Gets or sets the hash of the trigger to find.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Get or sets if the triggers to find is a tenant agnostic search
    /// </summary>
    public bool TenantAgnostic { get; set; }

    /// <summary>
    /// Applies the filter to the specified query.
    /// </summary>
    public IQueryable<StoredTrigger> Apply(IQueryable<StoredTrigger> queryable)
    {
        if (Id != null) queryable = queryable.Where(x => x.Id == Id);
        if (Ids != null) queryable = queryable.Where(x => Ids.Contains(x.Id));
        if (PipelineDefinitionId != null) queryable = queryable.Where(x => x.PipelineDefinitionId == PipelineDefinitionId);
        if (PipelineDefinitionIds != null) queryable = queryable.Where(x => PipelineDefinitionIds.Contains(x.PipelineDefinitionId));
        if (PipelineDefinitionVersionId != null) queryable = queryable.Where(x => x.PipelineDefinitionVersionId == PipelineDefinitionVersionId);
        if (PipelineDefinitionVersionIds != null) queryable = queryable.Where(x => PipelineDefinitionVersionIds.Contains(x.PipelineDefinitionVersionId));
        if (Name != null) queryable = queryable.Where(x => x.Name == Name);
        if (Names != null) queryable = queryable.Where(x => Names.Contains(x.Name!));
        if (Hash != null) queryable = queryable.Where(x => x.Hash == Hash);
        return queryable;
    }

    public static TriggerFilter ByNames(IEnumerable<string> names) => new()
    {
        Names = names.ToList()
    };
}