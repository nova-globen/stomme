using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;

/// <summary>
/// Provides step descriptors based on <see cref="PipelineDefinition"/>s stored in the database.
/// </summary>
public class PipelineDefinitionStepProvider(IPipelineDefinitionStore store, PipelineDefinitionStepDescriptorFactory pipelineDefinitionStepDescriptorFactory, ITenantAccessor tenantAccessor) : IStepProvider
{
    /// <inheritdoc />
    public async ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var filter = new PipelineDefinitionFilter
        {
            UsableAsStep = true,
            VersionOptions = VersionOptions.All
        };

        var definitions = (await store.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();
        return CreateDescriptors(definitions).ToList();
    }

    private IEnumerable<StepDescriptor> CreateDescriptors(ICollection<PipelineDefinition> definitions)
    {
        return definitions.Select(x => CreateDescriptor(x, definitions));
    }

    private StepDescriptor CreateDescriptor(PipelineDefinition definition, ICollection<PipelineDefinition> allDefinitions)
    {
        var latestPublishedVersion = allDefinitions
            .Where(x => x.DefinitionId == definition.DefinitionId && x.IsPublished)
            .MaxBy(x => x.Version);
        return pipelineDefinitionStepDescriptorFactory.CreateDescriptor(definition, latestPublishedVersion);
    }
}