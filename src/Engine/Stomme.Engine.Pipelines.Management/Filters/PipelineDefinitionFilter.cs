using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Filters;

/// <summary>
/// A specification to use when finding pipeline definitions. Only non-null fields will be included in the conditional expression.
/// </summary>
[PublicAPI]
public class PipelineDefinitionFilter
{
    /// <summary>
    /// Filter by the ID of the pipeline definition.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Filter by the IDs of the pipeline definitions.
    /// </summary>
    public ICollection<string>? Ids { get; set; }

    /// <summary>
    /// Filter by the ID of the pipeline definition.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>
    /// Filter by the IDs of the pipeline definitions.
    /// </summary>
    public ICollection<string>? DefinitionIds { get; set; }

    /// <summary>
    /// Filter by the version options.
    /// </summary>
    public VersionOptions? VersionOptions { get; set; }

    /// <summary>
    /// Filter by the handle of the pipeline definition.
    /// </summary>
    public PipelineDefinitionHandle? DefinitionHandle { get; set; }

    /// <summary>
    /// Filter by the name of the pipeline definition.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Filter by the name or id of the pipeline definition.
    /// </summary>
    public string? SearchTerm { get; set; }

    /// <summary>
    /// Filter by the names of the pipeline definitions.
    /// </summary>
    public ICollection<string>? Names { get; set; }

    /// <summary>
    /// Filter by the name of the pipeline definition materializer.
    /// </summary>
    public string? MaterializerName { get; set; }

    /// <summary>
    /// Filter on pipelines that are usable as steps.
    /// </summary>
    public bool? UsableAsStep { get; set; }

    /// <summary>
    /// Filter on pipelines that are system pipelines.
    /// </summary>
    public bool? IsSystem { get; set; }

    /// <summary>
    /// Filter on pipelines that are read-only.
    /// </summary>
    public bool? IsReadonly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to include tenant matching in the filter.
    /// </summary>
    public bool TenantAgnostic { get; set; }

    /// <summary>
    /// Applies the filter to the specified queryable.
    /// </summary>
    /// <param name="queryable">The queryable to apply the filter to.</param>
    /// <returns>The filtered queryable.</returns>
    public IQueryable<PipelineDefinition> Apply(IQueryable<PipelineDefinition> queryable)
    {
        var definitionId = DefinitionId ?? DefinitionHandle?.DefinitionId;
        var versionOptions = VersionOptions ?? DefinitionHandle?.VersionOptions;
        var id = Id ?? DefinitionHandle?.DefinitionVersionId;

        if (definitionId != null) queryable = queryable.Where(x => x.DefinitionId == definitionId);
        if (DefinitionIds != null) queryable = queryable.Where(x => DefinitionIds.Contains(x.DefinitionId));
        if (id != null) queryable = queryable.Where(x => x.Id == id);
        if (Ids != null) queryable = queryable.Where(x => Ids.Contains(x.Id));
        if (versionOptions != null) queryable = queryable.WithVersion(versionOptions.Value);
        if (MaterializerName != null) queryable = queryable.Where(x => x.MaterializerName == MaterializerName);
        if (Name != null) queryable = queryable.Where(x => x.Name == Name);
        if (Names != null) queryable = queryable.Where(x => Names.Contains(x.Name!));
        if (UsableAsStep != null) queryable = queryable.Where(x => x.Options.UsableAsStep == UsableAsStep);
        if (!string.IsNullOrWhiteSpace(SearchTerm)) queryable = queryable.Where(x => x.Name!.ToLower().Contains(SearchTerm.ToLower()) || x.Description!.ToLower().Contains(SearchTerm.ToLower()) || x.Id.Contains(SearchTerm) || x.DefinitionId.Contains(SearchTerm));
        if (IsSystem != null) queryable = queryable.Where(x => x.IsSystem == IsSystem);
        if (IsReadonly != null) queryable = queryable.Where(x => x.IsReadonly == IsReadonly);

        return queryable;
    }
}