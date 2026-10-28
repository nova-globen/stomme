using Stomme.Engine.Caching;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management.Filters;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineDefinitionCacheManager(ICacheManager cache, IHasher hasher, ITenantAccessor tenantAccessor) : IPipelineDefinitionCacheManager
{
    /// <inheritdoc />
    public ICacheManager Cache => cache;

    /// <inheritdoc />
    public string CreatePipelineDefinitionVersionCacheKey(string definitionId, VersionOptions versionOptions) => TenantPrefix() + $"PipelineDefinition:{definitionId}:{versionOptions}";

    /// <inheritdoc />
    public string CreatePipelineDefinitionFilterCacheKey(PipelineDefinitionFilter filter)
    {
        var hash = hasher.Hash(filter);
        return TenantPrefix() + $"PipelineDefinition:{hash}";
    }

    /// <inheritdoc />
    public string CreatePipelineVersionCacheKey(string definitionId, VersionOptions versionOptions) => TenantPrefix() + $"Pipeline:{definitionId}:{versionOptions}";

    /// <inheritdoc />
    public string CreatePipelineVersionCacheKey(string definitionVersionId) => TenantPrefix() + $"Pipeline:{definitionVersionId}";

    /// <inheritdoc />
    public string CreatePipelineFilterCacheKey(PipelineDefinitionFilter filter)
    {
        var hash = hasher.Hash(filter);
        return TenantPrefix() + $"Pipeline:{hash}";
    }

    /// <inheritdoc />
    public string CreatePipelineDefinitionVersionCacheKey(string definitionVersionId) => TenantPrefix() + $"PipelineDefinition:{definitionVersionId}";

    /// <inheritdoc />
    public string CreatePipelineDefinitionChangeTokenKey(string definitionId) => TenantPrefix() + $"PipelineChangeToken:{definitionId}";

    /// <inheritdoc />
    public async Task EvictPipelineDefinitionAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        var changeTokenKey = CreatePipelineDefinitionChangeTokenKey(definitionId);
        await cache.TriggerTokenAsync(changeTokenKey, cancellationToken).ConfigureAwait(false);
    }

    private string TenantPrefix()
    {
        var tenantId = tenantAccessor.TenantId;
        return string.IsNullOrEmpty(tenantId) ? string.Empty : $"{tenantId}:";
    }
}