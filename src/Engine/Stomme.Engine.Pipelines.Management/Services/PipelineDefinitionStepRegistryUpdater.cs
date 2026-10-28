using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Service responsible for updating the step registry based on step providers.
/// </summary>
public class PipelineDefinitionStepRegistryUpdater(PipelineDefinitionStepProvider provider, IStepRegistry registry) : IPipelineDefinitionStepRegistryUpdater
{
    private readonly Type _providerType = typeof(PipelineDefinitionStepProvider);
    
    /// <inheritdoc />
    public async Task AddToRegistry(string pipelineDefinitionVersionId, CancellationToken cancellationToken)
    {
        var descriptors = await provider.GetDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var descriptorToAdd = descriptors
            .FirstOrDefault(d =>
                d.CustomProperties.TryGetValue("PipelineDefinitionVersionId", out var val) &&
                val.ToString() == pipelineDefinitionVersionId);
        
        if (descriptorToAdd is not null)
            registry.Add(_providerType, descriptorToAdd);
    }

    /// <inheritdoc />
    public void RemoveDefinitionFromRegistry(string pipelineDefinitionId)
    {
        var providerDescriptors = registry.ListByProvider(_providerType);
        
        var descriptorsToRemove = providerDescriptors
            .Where(d =>
                d.CustomProperties.TryGetValue("PipelineDefinitionId", out var val) &&
                val.ToString() == pipelineDefinitionId).ToList();

        foreach (StepDescriptor stepDescriptor in descriptorsToRemove)
        {
            registry.Remove(_providerType, stepDescriptor);
        }
    }

    /// <inheritdoc />
    public void RemoveDefinitionVersionFromRegistry(string pipelineDefinitionVersionId)
    {
        var providerDescriptors = registry.ListByProvider(_providerType);
        
        var descriptorToRemove = providerDescriptors
            .FirstOrDefault(d =>
                d.CustomProperties.TryGetValue("PipelineDefinitionVersionId", out var val) &&
                val.ToString() == pipelineDefinitionVersionId);

        if (descriptorToRemove is not null)
            registry.Remove(_providerType, descriptorToRemove);
    }
}