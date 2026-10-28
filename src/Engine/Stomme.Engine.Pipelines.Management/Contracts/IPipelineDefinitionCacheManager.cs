using Stomme.Engine.Caching;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Filters;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Specifies the contract for managing the cache of pipeline definitions.
/// </summary>
public interface IPipelineDefinitionCacheManager
{
    /// <summary>
    /// Gets the cache manager.
    /// </summary>
    ICacheManager Cache { get; }

    /// <summary>
    /// Creates a cache key for the specified pipeline definition ID and version options.
    /// </summary>
    string CreatePipelineDefinitionVersionCacheKey(string definitionId, VersionOptions versionOptions);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition filter.
    /// </summary>
    string CreatePipelineDefinitionFilterCacheKey(PipelineDefinitionFilter filter);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition ID.
    /// </summary>
    string CreatePipelineVersionCacheKey(string definitionId, VersionOptions versionOptions);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition version ID.
    /// </summary>
    string CreatePipelineVersionCacheKey(string definitionVersionId);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition filter.
    /// </summary>
    string CreatePipelineFilterCacheKey(PipelineDefinitionFilter filter);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition version ID.
    /// </summary>
    string CreatePipelineDefinitionVersionCacheKey(string definitionVersionId);

    /// <summary>
    /// Creates a cache key for the specified pipeline definition ID.
    /// </summary>
    string CreatePipelineDefinitionChangeTokenKey(string definitionId);

    /// <summary>
    /// Evicts the specified pipeline definition from the cache.
    /// </summary>
    Task EvictPipelineDefinitionAsync(string definitionId, CancellationToken cancellationToken = default);
}