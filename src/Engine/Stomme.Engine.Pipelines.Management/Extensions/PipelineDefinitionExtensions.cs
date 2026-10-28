using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Extension methods for <see cref="IPipelineDefinitionService"/>.
/// </summary>
public static class PipelineDefinitionServiceExtensions
{
    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified definition ID and <see cref="VersionOptions"/>.
    /// </summary>
    public static Task<PipelineDefinition?> FindPipelineDefinitionAsync(this IPipelineDefinitionService service, string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        return service.FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified version ID.
    /// </summary>
    public static Task<PipelineDefinition?> FindPipelineDefinitionAsync(this IPipelineDefinitionService service, string definitionVersionId, CancellationToken cancellationToken = default)
    {
        return service.FindPipelineDefinitionAsync(definitionVersionId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Looks for a <see cref="Pipeline"/> by the specified definition ID and <see cref="VersionOptions"/>.
    /// </summary>
    public static async Task<Pipeline?> FindPipelineAsync(this IPipelineDefinitionService service, string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        return (await service.FindPipelineGraphAsync(definitionId, versionOptions, cancellationToken: cancellationToken).ConfigureAwait(false))?.Pipeline;
    }

    /// <summary>
    /// Looks for a <see cref="Pipeline"/> by the specified version ID.
    /// </summary>
    public static async Task<Pipeline?> FindPipelineAsync(this IPipelineDefinitionService service, string definitionVersionId, CancellationToken cancellationToken = default)
    {
        return (await service.FindPipelineGraphAsync(definitionVersionId, cancellationToken: cancellationToken).ConfigureAwait(false))?.Pipeline;
    }
}